using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ImportKsiegowy;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Narzedzia.EPP;
using InsERT.Moria.Sfera;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class NexoImportService
    {
        private readonly Uchwyt _sfera;

        public NexoImportService(Uchwyt sfera)
        {
            _sfera = sfera;
        }

        public async Task PrzetworzZadanieAsync(ImportJob job, Func<int, string, Task> raportujPostep)
        {
            await raportujPostep(20, "Deserializacja plików EPP...");
            var serializator = _sfera.PodajObiektTypu<ISerializatorEPP>();
            var wszystkieObiekty = new List<object>();

            foreach (var plik in job.Files)
            {
                // Zabezpieczenie: Wymuszamy .epp zamiast .tmp
                string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".epp");
                File.WriteAllBytes(tempFile, plik.Content);
                var obiektyZPliku = serializator.DeserializujObiektyZPliku(tempFile);
                wszystkieObiekty.AddRange(obiektyZPliku);
                File.Delete(tempFile);
            }

            if (wszystkieObiekty.Count == 0) throw new Exception("Wszystkie pliki EPP były puste!");

            await raportujPostep(40, "Etap 1: Synchronizacja słowników Sfery...");
            var menedzerInstancji = _sfera.PodajObiektTypu<IInstancjeBazDanych>();
            var obecnaInstancja = menedzerInstancji.Dane.Wszystkie().FirstOrDefault();
            var menedzerOdbioru = _sfera.PodajObiektTypu<IOdbiorDanychKlientaBiuraRachunkowego>();

            var operacjaSlownikow = menedzerOdbioru.OdbierzOfflineEpp(wszystkieObiekty.ToArray(), obecnaInstancja, new ProstyInformatorOStatusieOdbioruDokumentuEPP());
            var operacjaDokumentow = operacjaSlownikow.Zapisz();

            await raportujPostep(60, "Etap 2: Wrzucanie faktur do Poczekalni...");
            operacjaDokumentow.Zapisz();

            await raportujPostep(70, "Analiza dokumentów oczekujących...");
            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            var menedzerImportu = _sfera.PodajObiektTypu<IOperacjeImportuKsiegowego>();
            var menedzerOkresow = _sfera.PodajObiektTypu<InsERT.Moria.Ksiegowosc.IOkresyObrachunkowe>();

            var oczekujace = menedzerDokumentow.Dane.Wszystkie().Where(d => (int)d.StatusKsiegowy == 2).ToList();
            if (oczekujace.Count == 0)
            {
                await raportujPostep(100, "Zakończono! (Brak nowych dokumentów do zadekretowania).");
                return;
            }

            var obecnyOkres = menedzerOkresow.Dane.Wszystkie().ToList().LastOrDefault();

            // --- LATARKA DIAGNOSTYCZNA (Pokaże nam co dokładnie widzi Sfera) ---
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"\n[LATARKA] Sędzia analizuje okres: '{obecnyOkres?.Nazwa}'");
            Console.WriteLine($"[LATARKA] Dokumentów w poczekalni do analizy: {oczekujace.Count}");
            foreach (var d in oczekujace)
            {
                string kat = d.KategoriaDokumentu != null ? d.KategoriaDokumentu.Nazwa : "BRAK KATEGORII";
                Console.WriteLine($" - ID: {d.Id} | Kategoria: {kat}");
            }
            Console.ResetColor();
            // -------------------------------------------------------------------

            await raportujPostep(80, $"Sędzia weryfikuje Warunki Wyboru dla okresu '{obecnyOkres?.Nazwa}'...");
            dynamic menedzerDynamiczny = menedzerImportu;
            dynamic werdykt = menedzerDynamiczny.WyszukajSchematyDlaDokumentow(oczekujace, obecnyOkres);

            // --- RAPORT SĘDZIEGO (Dlaczego odrzucono?) ---
            var typ = werdykt.GetType();
            var brakSchematu = typ.GetProperty("DokumentyONieokreslonychSchematach")?.GetValue(werdykt) as System.Collections.IEnumerable;
            var zBledami = typ.GetProperty("DokumentyOBlednychSchematach")?.GetValue(werdykt) as System.Collections.IEnumerable;

            int brakCount = 0; if (brakSchematu != null) foreach (var b in brakSchematu) brakCount++;
            int bledyCount = 0; if (zBledami != null) foreach (var b in zBledami) bledyCount++;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WERDYKT] Odrzucono (brak spełnionych warunków schematu): {brakCount}");
            Console.WriteLine($"[WERDYKT] Odrzucono (błędy krytyczne w fakturze): {bledyCount}");
            Console.ResetColor();
            // ---------------------------------------------

            var zatwierdzone = PobierzZaakceptowanePary(werdykt);
            if (zatwierdzone.Count == 0) throw new Exception("Żadna z wrzuconych faktur nie pasuje do schematów dekretacji!");

            await raportujPostep(90, $"Fizyczna dekretacja {zatwierdzone.Count} dokumentów w bazie...");

            // DOKŁADNA kopia Twoich działających parametrów (przywrócenie ObslugaNieusuwalnychDokumentow)
            var parametry = new ParametryOperacjiImportuKsiegowegoDokumentow();
            parametry.TrybSeryjnegoImportu = TrybSeryjnegoImportu.KontynuujGdyBlad;
            parametry.ObslugaUsuwalnychDokumentow = ObslugaBleduIstnieniaUsuwalnychDokumentow.WycofajIZaimportujJeszczeRaz;
            parametry.ObslugaNieusuwalnychDokumentow = ObslugaBleduIstnieniaNieusuwalnychDokumentow.KontynuujGdyBlad;
            parametry.ImportZPotwierdzeniem = false;

            var operacjaSeryjna = menedzerImportu.UtworzOperacjeImportuDokumentow(new CichaObslugaImportu());
            dynamic operacjaBypass = operacjaSeryjna;

            // Fizyczny import do ksiąg!
            operacjaBypass.WykonajOperacje(zatwierdzone, parametry);

            await raportujPostep(100, $"[SUKCES] Proces zakończony. Pomyślnie zadekretowano {zatwierdzone.Count} faktur.");
        }

        private List<Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>> PobierzZaakceptowanePary(dynamic werdykt)
        {
            // Nexo żąda dokładnie takiej kolekcji Tupli
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
                    string numer = paraDok.DokumentDoKsiegowaniaGlowny?.NumerDokumentu ?? paraDok.Id.ToString();

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[SUKCES] Odpakowano z teczki Sędziego: {numer} -> Schemat: {schemat.Nazwa}");
                    Console.ResetColor();

                    // Wrzucamy do poprawnego pudełka
                    gotowe.Add(new Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>(paraDok, schemat));
                }
            }
            return gotowe;
        }
        public class CichaObslugaImportu : IObslugaZdarzenSeryjnegoImportu
        {
            public InterakcjaWOperacjiImportuKsiegowego InteraktywnyTryb { get; } = new InterakcjaWOperacjiImportuKsiegowego();

            public CichaObslugaImportu()
            {
                // 1. Jeśli jedna faktura wybuchnie, księguj pozostałe
                InteraktywnyTryb.KontynuowacImportKolejnegoDokumentuPoBledzie = (dokument, blad) => true;

                // 2. Jeśli dokument wynikowy jest niepoprawny, po prostu go pomiń (nie wyświetlaj okienka naprawy)
                InteraktywnyTryb.SprobujNaprawicNiepoprawneDokumentyWynikowe = (p1, p2, p3, p4, p5, p6, p7) => default(WynikFragmentuOperacjiImportu);

                // 3. Jeśli faktura już istnieje, nadpisz ją (lub pomiń zgodnie z ustawieniami ogólnymi)
                InteraktywnyTryb.ZapytajOUsuwanieIstniejacych = (dokumenty) => default(WynikFragmentuOperacjiImportu);
            }

            public void RozpoczecieCalosci(int ilosc) { }
            public void ZakonczenieWszystkich() { }
            public void RozpoczeciePojedynczego(ImportSeryjnyEventArgs e) { }
            public void RozpoczecieFragmentuWImporciePojedynczego(ImportSeryjnyEventArgs e) { }
            public void ZakonczeniePojedynczego(ImportSeryjnyEventArgs e) { }
        }
    }
}