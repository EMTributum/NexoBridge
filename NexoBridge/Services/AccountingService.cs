using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ImportKsiegowy;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using InsERT.Moria;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using NexoBridge.Infrastructure;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class AccountingService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<AccountingService> _logger;

        public AccountingService(Uchwyt sfera, ILogger<AccountingService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task<(
            dynamic Rezultat,
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> Zatwierdzone,
            List<DokumentDoKsiegowania> Oczekujace,
            List<DokumentDoKsiegowania> BrakSchematu,
            List<DokumentDoKsiegowania> BledneSchematy)> DekretujAsync(DateTime dataRozliczenia, ImportPackageContext packageContext, Func<int, string, Task> raportujPostep)
        {
            await raportujPostep(70, "Analiza dokumentów oczekujących...");
            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            var menedzerImportu = _sfera.PodajObiektTypu<IOperacjeImportuKsiegowego>();
            var menedzerOkresow = _sfera.PodajObiektTypu<InsERT.Moria.Ksiegowosc.IOkresyObrachunkowe>();

            var wszystkieOczekujace = ((IEnumerable)menedzerDokumentow.Dane.Wszystkie())
                .Cast<DokumentDoKsiegowania>()
                .Where(d => (int)d.StatusKsiegowy == 2)
                .ToList();
            var wybor = WaitingRoomDocumentFilter.SelectForNewMarker(wszystkieOczekujace, PobierzKontekstZnacznikaNowosci());
            LogujWyborPoczekalni(wybor);
            var oczekujace = wybor.Included;

            if (oczekujace.Count == 0)
            {
                _logger.LogInformation("Zakończono: Brak nowych dokumentów do zadekretowania po synchronizacji.");
                return (null, new List<Tuple<DokumentDoKsiegowania, SchematImportu>>(), oczekujace, new List<DokumentDoKsiegowania>(), new List<DokumentDoKsiegowania>());
            }

            var obecnyOkres = ((IEnumerable)menedzerOkresow.Dane.Wszystkie())
                .Cast<object>()
                .LastOrDefault();
            string nazwaOkresu = "BRAK_OKRESU";
            if (obecnyOkres != null)
            {
                try
                {
                    dynamic obecnyOkresDyn = obecnyOkres;
                    nazwaOkresu = obecnyOkresDyn.Nazwa?.ToString() ?? nazwaOkresu;
                }
                catch { }
            }

            await raportujPostep(80, $"Sędzia weryfikuje Warunki Wyboru dla okresu '{nazwaOkresu}'...");
            dynamic menedzerDynamiczny = menedzerImportu;
            dynamic werdykt = menedzerDynamiczny.WyszukajSchematyDlaDokumentow(oczekujace, obecnyOkres);

            var typ = werdykt.GetType();
            var brakSchematuRaw = typ.GetProperty("DokumentyONieokreslonychSchematach")?.GetValue(werdykt) as System.Collections.IEnumerable;
            var zBledamiRaw = typ.GetProperty("DokumentyOBlednychSchematach")?.GetValue(werdykt) as System.Collections.IEnumerable;

            var brakSchematu = PobierzDokumentyZElementow(brakSchematuRaw);
            var bledneSchematy = PobierzDokumentyZElementow(zBledamiRaw);

            if (brakSchematu.Count > 0) _logger.LogWarning("[WERDYKT] Odrzucono (brak spełnionych warunków schematu): {BrakCount}; dokumenty={Dokumenty}", brakSchematu.Count, OpiszDokumenty(brakSchematu));
            if (bledneSchematy.Count > 0) _logger.LogWarning("[WERDYKT] Odrzucono (błędy krytyczne w fakturze): {BledyCount}; dokumenty={Dokumenty}", bledneSchematy.Count, OpiszDokumenty(bledneSchematy));

            var zatwierdzone = PobierzZaakceptowanePary(werdykt);
            if (zatwierdzone.Count == 0)
            {
                _logger.LogWarning("Żaden dokument z Poczekalni nie pasuje do schematów dekretacji. Nie przerywam procesu - raport zostanie zwrócony na front.");
                return (null, zatwierdzone, oczekujace, brakSchematu, bledneSchematy);
            }

            await raportujPostep(90, $"Fizyczna dekretacja {zatwierdzone.Count} dokumentów w bazie...");

            var parametry = new ParametryOperacjiImportuKsiegowegoDokumentow();
            parametry.TrybSeryjnegoImportu = TrybSeryjnegoImportu.KontynuujGdyBlad;
            parametry.ObslugaUsuwalnychDokumentow = ObslugaBleduIstnieniaUsuwalnychDokumentow.WycofajIZaimportujJeszczeRaz;
            parametry.ObslugaNieusuwalnychDokumentow = ObslugaBleduIstnieniaNieusuwalnychDokumentow.KontynuujGdyBlad;
            parametry.ImportZPotwierdzeniem = false;

            var operacjaSeryjna = menedzerImportu.UtworzOperacjeImportuDokumentow(new CichaObslugaImportu());
            dynamic operacjaBypass = operacjaSeryjna;

            dynamic rezultat = operacjaBypass.WykonajOperacje(zatwierdzone, parametry);
            int liczbaWynikow = PoliczWynikiOperacji(rezultat);
            _logger.LogInformation("[DEKRETACJA OPERACJA] Zlecono={Zlecono}; wynikiOperacji={Wyniki}", (object)zatwierdzone.Count, (object)liczbaWynikow);

            return (rezultat, zatwierdzone, oczekujace, brakSchematu, bledneSchematy);
        }

        private List<Tuple<DokumentDoKsiegowania, SchematImportu>> PobierzZaakceptowanePary(dynamic werdykt)
        {
            var gotowe = new List<Tuple<DokumentDoKsiegowania, SchematImportu>>();
            var szufladka = werdykt.GetType().GetProperty("DokumentyZeSchematami")?.GetValue(werdykt) as System.Collections.IEnumerable;

            if (szufladka == null) return gotowe;

            foreach (var item in szufladka)
            {
                var typItemu = item.GetType();
                var dok = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.Name.Contains("DokumentDoKsiegowania"))?.GetValue(item);
                SchematImportu schemat = null;
                var schematyProp = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Any(g => g.Name.Contains("SchematImportu")));

                if (schematyProp != null && schematyProp.GetValue(item) is System.Collections.IEnumerable lista)
                {
                    foreach (var s in lista) { schemat = (SchematImportu)s; break; }
                }
                else
                {
                    var pojedynczyProp = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.Name.Contains("SchematImportu"));
                    if (pojedynczyProp != null) schemat = (SchematImportu)pojedynczyProp.GetValue(item);
                }

                if (dok != null && schemat != null)
                {
                    var paraDok = (DokumentDoKsiegowania)dok;
                    string numer = paraDok.NumerDokumentu;
                    if (string.IsNullOrEmpty(numer)) numer = paraDok.Id.ToString();

                    _logger.LogInformation("[SCHEMAT DEKRETACJI] Dokument zaakceptowany przez Sędziego: {Numer} -> Schemat: {Schemat}", numer, schemat.Nazwa);
                    gotowe.Add(new Tuple<DokumentDoKsiegowania, SchematImportu>(paraDok, schemat));
                }
            }
            return gotowe;
        }

        private List<DokumentDoKsiegowania> PobierzDokumentyZElementow(System.Collections.IEnumerable elementy)
        {
            var dokumenty = new List<DokumentDoKsiegowania>();
            if (elementy == null) return dokumenty;

            foreach (var item in elementy)
            {
                if (item is DokumentDoKsiegowania dokument)
                {
                    dokumenty.Add(dokument);
                    continue;
                }

                var typItemu = item.GetType();
                var dok = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.Name.Contains("DokumentDoKsiegowania"))?.GetValue(item) as DokumentDoKsiegowania;
                if (dok != null)
                {
                    dokumenty.Add(dok);
                }
            }

            return dokumenty;
        }

        private int PoliczWynikiOperacji(dynamic rezultat)
        {
            if (rezultat == null) return 0;
            try { return ((System.Collections.IEnumerable)rezultat).Cast<object>().Count(); }
            catch { return 0; }
        }

        private string OpiszDokumenty(IEnumerable<DokumentDoKsiegowania> dokumenty)
        {
            if (dokumenty == null) return "brak";
            var opisy = dokumenty.Take(100).Select(InvoiceDocumentMatcher.Describe).ToList();
            return opisy.Count == 0 ? "brak" : string.Join(" || ", opisy);
        }

        private NewDocumentMarkerContext PobierzKontekstZnacznikaNowosci()
        {
            var parametryImportu = _sfera.PodajObiektTypu<IParametryImportuKsiegowego>();
            var parametrImportu = parametryImportu?.DaneDomyslne?.Domyslny;
            var dataSystemowa = _sfera.PodajObiektTypu<IDataSystemowa>();

            return new NewDocumentMarkerContext(parametrImportu, dataSystemowa);
        }

        private void LogujWyborPoczekalni(WaitingRoomDocumentSelection wybor)
        {
            if (wybor == null) return;

            _logger.LogInformation("[POCZEKALNIA FILTR N] wszystkie={All}; doDekretacji={Included}; noweN={IncludedByNewMarker}; wyjatkiKadrowe={IncludedPayrollException}; bezN={SkippedNotNew}; amortyzacjeCzastkowe={PartialAmortization}; rachunkiPracowniczeZPodmiotem={EmployeeBillsWithSubject}",
                wybor.Total,
                wybor.Included.Count,
                wybor.IncludedByNewMarker.Count,
                wybor.IncludedPayrollException.Count,
                wybor.SkippedNotNew.Count,
                wybor.PartialAmortization.Count,
                wybor.EmployeeBillsWithSubject.Count);

            LogujPominiete("[POCZEKALNIA BEZ N POMINIĘTA]", wybor.SkippedNotNew);
            LogujPominiete("[AMORTYZACJA CZĄSTKOWA POMINIĘTA]", wybor.PartialAmortization);
            LogujPominiete("[RACHUNEK PRACOWNICZY Z PODMIOTEM POMINIĘTY]", wybor.EmployeeBillsWithSubject);
        }

        private void LogujPominiete(string prefix, List<DokumentDoKsiegowania> dokumenty)
        {
            if (dokumenty == null || dokumenty.Count == 0) return;
            _logger.LogInformation("{Prefix} Liczba={Count}; dokumenty={Dokumenty}", prefix, dokumenty.Count, OpiszDokumenty(dokumenty));
        }
    }
}


