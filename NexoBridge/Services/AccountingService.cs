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
        private readonly PoczekalniaBaselineStore _baselineStore;
        private readonly NexoBridgeErrorReporter _errorReporter;
        private readonly ILogger<AccountingService> _logger;

        public AccountingService(
            Uchwyt sfera,
            PoczekalniaBaselineStore baselineStore,
            NexoBridgeErrorReporter errorReporter,
            ILogger<AccountingService> logger)
        {
            _sfera = sfera;
            _baselineStore = baselineStore;
            _errorReporter = errorReporter;
            _logger = logger;
        }

        public async Task<(
            dynamic Rezultat,
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> Zatwierdzone,
            List<DokumentDoKsiegowania> Oczekujace,
            List<DokumentDoKsiegowania> BrakSchematu,
            List<DokumentDoKsiegowania> BledneSchematy)> DekretujAsync(DateTime dataRozliczenia, ImportPackageContext packageContext, ImportJob job, Func<int, string, Task> raportujPostep)
        {
            await raportujPostep(70, "Analiza dokumentów oczekujących...");
            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            var menedzerImportu = _sfera.PodajObiektTypu<IOperacjeImportuKsiegowego>();
            var menedzerOkresow = _sfera.PodajObiektTypu<InsERT.Moria.Ksiegowosc.IOkresyObrachunkowe>();

            var wszystkieOczekujace = PobierzOczekujace(menedzerDokumentow);
            var znaneNumery = await _baselineStore.PobierzZnaneNumeryAsync(job?.DatabaseName);
            bool bootstrap = znaneNumery == null;
            var wybor = WaitingRoomDocumentFilter.SelectForBaseline(wszystkieOczekujace, doc => bootstrap || znaneNumery.Contains(doc.Nr));
            LogujWyborPoczekalni(wybor, bootstrap);
            var oczekujace = wybor.Included;

            if (oczekujace.Count == 0)
            {
                await ZapiszBaselinePoDekretacjiAsync(job, menedzerDokumentow, wybor);
                _logger.LogInformation("Zakończono: Brak nowych dokumentów do zadekretowania po synchronizacji.");
                return (null, new List<Tuple<DokumentDoKsiegowania, SchematImportu>>(), oczekujace, new List<DokumentDoKsiegowania>(), new List<DokumentDoKsiegowania>());
            }

            var obecnyOkres = PobierzOkresObrachunkowy(menedzerOkresow, dataRozliczenia);
            string nazwaOkresu = OpiszOkres(obecnyOkres);

            await raportujPostep(80, $"Sędzia weryfikuje Warunki Wyboru dla okresu '{nazwaOkresu}'...");
            var dokumentyDoWyszukaniaSchematow = oczekujace
                .Cast<DokumentDoKsiegowania>()
                .ToList();
            var werdykt = ((IOperacjeImportuKsiegowego)menedzerImportu)
                .WyszukajSchematyDlaDokumentow(dokumentyDoWyszukaniaSchematow, obecnyOkres);

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
                await ZapiszBaselinePoDekretacjiAsync(job, menedzerDokumentow, wybor);
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

            await ZapiszBaselinePoDekretacjiAsync(job, menedzerDokumentow, wybor);

            return (rezultat, zatwierdzone, oczekujace, brakSchematu, bledneSchematy);
        }

        private List<DokumentDoKsiegowania> PobierzOczekujace(IDokumentyDoKsiegowania menedzerDokumentow)
        {
            return ((IEnumerable)menedzerDokumentow.Dane.Wszystkie())
                .Cast<DokumentDoKsiegowania>()
                .Where(d => (int)d.StatusKsiegowy == 2)
                .ToList();
        }

        /// <summary>
        /// Nadpisuje baseline poczekalni żywym stanem puli po dekretacji i weryfikuje, że dokumenty
        /// wysłane do dekretacji (wybor.Included) faktycznie z niej zniknęły. Jeśli coś zostało (np.
        /// nieudany import/brak schematu), to jest realny problem operacyjny - zgłaszamy go aktywnie do
        /// Klasyfikatora, bo inaczej baseline "po cichu" uznałby ten dokument za znany i nikt by go
        /// więcej automatycznie nie spróbował zadekretować.
        /// </summary>
        private async Task ZapiszBaselinePoDekretacjiAsync(ImportJob job, IDokumentyDoKsiegowania menedzerDokumentow, WaitingRoomDocumentSelection wybor)
        {
            var poDekretacji = PobierzOczekujace(menedzerDokumentow);
            var poDekretacjiNumery = poDekretacji.Select(d => d.Nr).ToHashSet();

            var utkniete = (wybor?.Included ?? new List<DokumentDoKsiegowania>())
                .Where(d => poDekretacjiNumery.Contains(d.Nr))
                .ToList();

            if (utkniete.Count > 0)
            {
                string opis = OpiszDokumenty(utkniete);
                _logger.LogWarning("[POCZEKALNIA WERYFIKACJA] {Count} dokumentów wysłanych do dekretacji wciąż jest w Poczekalni po zakończeniu operacji: {Dokumenty}", utkniete.Count, opis);
                await _errorReporter.ReportJobFailureAsync(
                    job,
                    component: "AccountingService",
                    activity: "Dekretacja",
                    operation: "WeryfikacjaPoczekalni",
                    message: $"{utkniete.Count} dokumentów przekazanych do dekretacji nie zniknęło z Poczekalni po zakończeniu operacji: {opis}",
                    exception: null,
                    cancellationToken: default);
            }

            await _baselineStore.ZapiszZnaneNumeryAsync(job?.DatabaseName, poDekretacjiNumery);
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

        private OkresObrachunkowy PobierzOkresObrachunkowy(InsERT.Moria.Ksiegowosc.IOkresyObrachunkowe menedzerOkresow, DateTime dataRozliczenia)
        {
            var okresy = ((IEnumerable)menedzerOkresow.Dane.Wszystkie())
                .Cast<OkresObrachunkowy>()
                .ToList();

            if (okresy.Count == 0)
            {
                throw new InvalidOperationException("Nie znaleziono żadnego okresu obrachunkowego w bazie klienta.");
            }

            DateTime data = dataRozliczenia.Date;
            var okresDlaDaty = okresy
                .Where(o => o.Okres != null &&
                            o.Okres.DataPoczatkowa.Date <= data &&
                            o.Okres.DataKoncowa.Date >= data)
                .OrderByDescending(o => o.Okres.DataPoczatkowa)
                .FirstOrDefault();

            if (okresDlaDaty != null)
            {
                return okresDlaDaty;
            }

            var ostatniOkres = okresy
                .OrderByDescending(o => o.Okres?.DataPoczatkowa ?? DateTime.MinValue)
                .First();

            _logger.LogWarning("[OKRES OBRACHUNKOWY] Nie znaleziono okresu zawierającego datę {Data}. Używam ostatniego dostępnego okresu: {Okres}.",
                data.ToString("yyyy-MM-dd"),
                OpiszOkres(ostatniOkres));

            return ostatniOkres;
        }

        private string OpiszOkres(OkresObrachunkowy okres)
        {
            if (okres == null) return "BRAK_OKRESU";
            string zakres = okres.Okres == null
                ? "brak zakresu"
                : $"{okres.Okres.DataPoczatkowa:yyyy-MM-dd} - {okres.Okres.DataKoncowa:yyyy-MM-dd}";
            return $"{okres.Nazwa ?? "bez nazwy"} ({zakres})";
        }

        private void LogujWyborPoczekalni(WaitingRoomDocumentSelection wybor, bool bootstrap)
        {
            if (wybor == null) return;

            _logger.LogInformation("[POCZEKALNIA FILTR BASELINE] bootstrap={Bootstrap}; wszystkie={All}; doDekretacji={Included}; nowe={IncludedNew}; wyjatkiKadrowe={IncludedPayrollException}; znaneZBaseline={SkippedNotNew}; amortyzacjeCzastkowe={PartialAmortization}; rachunkiPracowniczeZPodmiotem={EmployeeBillsWithSubject}; dokumentyWewnetrzne={InternalDocuments}",
                bootstrap,
                wybor.Total,
                wybor.Included.Count,
                wybor.IncludedNew.Count,
                wybor.IncludedPayrollException.Count,
                wybor.SkippedNotNew.Count,
                wybor.PartialAmortization.Count,
                wybor.EmployeeBillsWithSubject.Count,
                wybor.InternalDocuments.Count);

            if (bootstrap)
            {
                _logger.LogInformation("[POCZEKALNIA BASELINE] Zainicjalizowano punkt odniesienia: {Count} dokumentów potraktowano jako znane; żaden nie zostanie automatycznie zadekretowany w tym przebiegu.", wybor.Total);
            }

            LogujPominiete("[POCZEKALNIA ZNANE Z BASELINE POMINIĘTA]", wybor.SkippedNotNew);
            LogujPominiete("[AMORTYZACJA CZĄSTKOWA POMINIĘTA]", wybor.PartialAmortization);
            LogujPominiete("[RACHUNEK PRACOWNICZY Z PODMIOTEM POMINIĘTY]", wybor.EmployeeBillsWithSubject);
            LogujPominiete("[DOKUMENT WEWNĘTRZNY RACHMISTRZA POMINIĘTY]", wybor.InternalDocuments);
        }

        private void LogujPominiete(string prefix, List<DokumentDoKsiegowania> dokumenty)
        {
            if (dokumenty == null || dokumenty.Count == 0) return;
            _logger.LogInformation("{Prefix} Liczba={Count}; dokumenty={Dokumenty}", prefix, dokumenty.Count, OpiszDokumenty(dokumenty));
        }
    }
}


