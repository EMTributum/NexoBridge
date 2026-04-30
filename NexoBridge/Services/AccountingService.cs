using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ImportKsiegowy;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Infrastructure;
using System;
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

        public async Task<(dynamic Rezultat, List<Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>> Zatwierdzone)> DekretujAsync(Func<int, string, Task> raportujPostep)
        {
            await raportujPostep(70, "Analiza dokumentów oczekujących...");
            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            var menedzerImportu = _sfera.PodajObiektTypu<IOperacjeImportuKsiegowego>();
            var menedzerOkresow = _sfera.PodajObiektTypu<InsERT.Moria.Ksiegowosc.IOkresyObrachunkowe>();

            var oczekujace = menedzerDokumentow.Dane.Wszystkie().Where(d => (int)d.StatusKsiegowy == 2).ToList();
            if (oczekujace.Count == 0)
            {
                _logger.LogInformation("Zakończono: Brak nowych dokumentów do zadekretowania po synchronizacji.");
                return (null, new List<Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>>());
            }

            var obecnyOkres = menedzerOkresow.Dane.Wszystkie().ToList().LastOrDefault();
            string nazwaOkresu = obecnyOkres != null ? obecnyOkres.Nazwa.ToString() : "BRAK_OKRESU";

            await raportujPostep(80, $"Sędzia weryfikuje Warunki Wyboru dla okresu '{nazwaOkresu}'...");
            dynamic menedzerDynamiczny = menedzerImportu;
            dynamic werdykt = menedzerDynamiczny.WyszukajSchematyDlaDokumentow(oczekujace, obecnyOkres);

            var typ = werdykt.GetType();
            var brakSchematu = typ.GetProperty("DokumentyONieokreslonychSchematach")?.GetValue(werdykt) as System.Collections.IEnumerable;
            var zBledami = typ.GetProperty("DokumentyOBlednychSchematach")?.GetValue(werdykt) as System.Collections.IEnumerable;

            int brakCount = 0; if (brakSchematu != null) foreach (var b in brakSchematu) brakCount++;
            int bledyCount = 0; if (zBledami != null) foreach (var b in zBledami) bledyCount++;

            if (brakCount > 0) _logger.LogWarning("[WERDYKT] Odrzucono (brak spełnionych warunków schematu): {BrakCount}", brakCount);
            if (bledyCount > 0) _logger.LogWarning("[WERDYKT] Odrzucono (błędy krytyczne w fakturze): {BledyCount}", bledyCount);

            var zatwierdzone = PobierzZaakceptowanePary(werdykt);
            if (zatwierdzone.Count == 0)
            {
                _logger.LogError("Żadna z wrzuconych faktur nie pasuje do schematów dekretacji!");
                throw new Exception("Żadna z wrzuconych faktur nie pasuje do schematów dekretacji!");
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

            return (rezultat, zatwierdzone);
        }

        private List<Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>> PobierzZaakceptowanePary(dynamic werdykt)
        {
            var gotowe = new List<Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>>();
            var szufladka = werdykt.GetType().GetProperty("DokumentyZeSchematami")?.GetValue(werdykt) as System.Collections.IEnumerable;

            if (szufladka == null) return gotowe;

            foreach (var item in szufladka)
            {
                var typItemu = item.GetType();
                var dok = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.Name.Contains("DokumentDoKsiegowania"))?.GetValue(item);
                InsERT.Moria.ModelDanych.SchematImportu schemat = null;
                var schematyProp = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Any(g => g.Name.Contains("SchematImportu")));

                if (schematyProp != null && schematyProp.GetValue(item) is System.Collections.IEnumerable lista)
                {
                    foreach (var s in lista) { schemat = (InsERT.Moria.ModelDanych.SchematImportu)s; break; }
                }
                else
                {
                    var pojedynczyProp = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.Name.Contains("SchematImportu"));
                    if (pojedynczyProp != null) schemat = (InsERT.Moria.ModelDanych.SchematImportu)pojedynczyProp.GetValue(item);
                }

                if (dok != null && schemat != null)
                {
                    var paraDok = (DokumentDoKsiegowania)dok;
                    string numer = paraDok.NumerDokumentu;
                    if (string.IsNullOrEmpty(numer)) numer = paraDok.Id.ToString();

                    _logger.LogInformation("[SUKCES DEKRETACJI] Odpakowano z teczki Sędziego: {Numer} -> Schemat: {Schemat}", numer, schemat.Nazwa);
                    gotowe.Add(new Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>(paraDok, schemat));
                }
            }
            return gotowe;
        }
    }
}