using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ImportKsiegowy;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Narzedzia.EPP;
using InsERT.Moria.Sfera;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NexoBridge.Serwisy
{
    /// <summary>
    /// Główny mózg operacyjny odpowiedzialny za procesy księgowe w Nexo.
    /// Rozdziela proces na Import (EPP) oraz Dekretację (Księgowanie).
    /// </summary>
    public class NexoImportService
    {
        private readonly Uchwyt _sfera;

        public NexoImportService(Uchwyt sfera)
        {
            _sfera = sfera;
        }

        /// <summary>
        /// Wczytuje plik EPP, aktualizuje kontrahentów/kategorie i wrzuca faktury do "Poczekalni".
        /// </summary>
        public void PrzetworzPlikEpp(string sciezkaPliku)
        {
            var serializator = _sfera.PodajObiektTypu<ISerializatorEPP>();
            var wczytaneDane = serializator.DeserializujObiektyZPliku(sciezkaPliku).ToArray();

            var menedzerInstancji = _sfera.PodajObiektTypu<IInstancjeBazDanych>();
            var obecnaInstancja = menedzerInstancji.Dane.Wszystkie().FirstOrDefault();
            var menedzerOdbioru = _sfera.PodajObiektTypu<IOdbiorDanychKlientaBiuraRachunkowego>();

            // Etap słowników (kontrahenci, kategorie)
            var operacjaSlownikow = menedzerOdbioru.OdbierzOfflineEpp(wczytaneDane, obecnaInstancja, new ProstyInformatorOStatusieOdbioruDokumentuEPP());

            // Etap dokumentów (faktury)
            var operacjaDokumentow = operacjaSlownikow.Zapisz();
            operacjaDokumentow.Zapisz();
        }

        /// <summary>
        /// Uruchamia proces automatycznego dobierania schematów i fizycznego księgowania w KPiR.
        /// </summary>
        public void UruchomAutomatycznaDekretacje()
        {
            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            var menedzerImportu = _sfera.PodajObiektTypu<IOperacjeImportuKsiegowego>();
            var menedzerOkresow = _sfera.PodajObiektTypu<InsERT.Moria.Ksiegowosc.IOkresyObrachunkowe>();

            // Pobieramy tylko dokumenty oczekujące (status 2)
            var oczekujace = menedzerDokumentow.Dane.Wszystkie()
                .Where(d => (int)d.StatusKsiegowy == 2)
                .ToList();
            if (!oczekujace.Any())
            {
                Console.WriteLine("[INFO] Brak dokumentów do zadekretowania.");
                return;
            }

            var obecnyOkres = menedzerOkresow.Dane.Wszystkie().ToList().LastOrDefault();

            // Wywołanie wbudowanego silnika Nexo do analizy Warunków Wyboru w schematach
            dynamic menedzerDynamiczny = menedzerImportu;
            dynamic werdykt = menedzerDynamiczny.WyszukajSchematyDlaDokumentow(oczekujace, obecnyOkres);

            // 1. Logowanie odrzuconych faktur (brak pasującego schematu)
            ZalogujOdrzucone(werdykt);

            // 2. Pobieranie zaakceptowanych par [Dokument + Schemat]
            var zatwierdzone = PobierzZaakceptowanePary(werdykt);

            if (zatwierdzone.Count == 0)
            {
                Console.WriteLine("[UWAGA] Żadna faktura nie spełniła warunków schematów.");
                return;
            }

            // 3. Fizyczna operacja księgowania (Import Księgowy)
            var parametry = new ParametryOperacjiImportuKsiegowegoDokumentow
            {
                TrybSeryjnegoImportu = TrybSeryjnegoImportu.KontynuujGdyBlad,
                ImportZPotwierdzeniem = false,
                ObslugaUsuwalnychDokumentow = ObslugaBleduIstnieniaUsuwalnychDokumentow.WycofajIZaimportujJeszczeRaz
            };

            Console.WriteLine($"\nPrzekazuję {zatwierdzone.Count} dokumentów do zaksięgowania...");
            var operacjaSeryjna = menedzerImportu.UtworzOperacjeImportuDokumentow(new CichaObslugaImportu());
            dynamic operacjaBypass = operacjaSeryjna;
            operacjaBypass.WykonajOperacje(zatwierdzone, parametry);
        }

        private void ZalogujOdrzucone(dynamic werdykt)
        {
            var typ = werdykt.GetType();

            // Dokumenty, które "odbiły się" od wszystkich schematów
            if (typ.GetProperty("DokumentyONieokreslonychSchematach")?.GetValue(werdykt) is System.Collections.IEnumerable brakSchematu)
            {
                foreach (var d in brakSchematu)
                    Console.WriteLine($"[BRAK SCHEMATU] Faktura ID nie pasuje do żadnego zdefiniowanego warunku wyboru.");
            }

            // Dokumenty, które wywołały błąd logiczny
            if (typ.GetProperty("DokumentyOBlednychSchematach")?.GetValue(werdykt) is System.Collections.IEnumerable zBledami)
            {
                foreach (var d in zBledami)
                    Console.WriteLine($"[BŁĄD SCHEMATU] Napotkano błąd przy dopasowywaniu faktury.");
            }
        }

        private List<Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>> PobierzZaakceptowanePary(dynamic werdykt)
        {
            var gotowe = new List<Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>>();
            var szufladka = werdykt.GetType().GetProperty("DokumentyZeSchematami")?.GetValue(werdykt) as System.Collections.IEnumerable;

            if (szufladka == null) return gotowe;

            foreach (var item in szufladka)
            {
                var typItemu = item.GetType();

                // Wykorzystujemy Refleksję, by znaleźć Dokument i Schemat po ich typach (najbezpieczniejsza metoda)
                var dok = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.Name.Contains("DokumentDoKsiegowania"))?.GetValue(item);

                InsERT.Moria.ModelDanych.SchematImportu schemat = null;
                var schematyProp = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Any(g => g.Name.Contains("SchematImportu")));

                if (schematyProp != null && schematyProp.GetValue(item) is System.Collections.IEnumerable lista)
                {
                    foreach (var s in lista) { schemat = (InsERT.Moria.ModelDanych.SchematImportu)s; break; }
                }

                if (dok != null && schemat != null)
                {
                    var para = (DokumentDoKsiegowania)dok;

                    // --- POPRAWKA TUTAJ ---
                    // Pobieramy numer (NumerWlasny) i kategorię (przez obiekt KategoriaDokumentu)
                    string numer = para.DokumentDoKsiegowaniaGlowny.NumerDokumentu ?? "Brak numeru";
                    string kat = para.KategoriaDokumentu != null ? para.KategoriaDokumentu.Nazwa : "Brak kategorii";

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  [OK] Dopasowano: {numer} -> Schemat: {schemat.Nazwa}");
                    Console.ResetColor();

                    gotowe.Add(new Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>(para, schemat));
                }
            }
            return gotowe;
        }
    }

    public class CichaObslugaImportu : IObslugaZdarzenSeryjnegoImportu
    {
        public InterakcjaWOperacjiImportuKsiegowego InteraktywnyTryb { get; } = new InterakcjaWOperacjiImportuKsiegowego();

        public CichaObslugaImportu()
        {
            InteraktywnyTryb.KontynuowacImportKolejnegoDokumentuPoBledzie = (dokument, blad) => true;
            InteraktywnyTryb.SprobujNaprawicNiepoprawneDokumentyWynikowe = (p1, p2, p3, p4, p5, p6, p7) => default(WynikFragmentuOperacjiImportu);
            InteraktywnyTryb.ZapytajOUsuwanieIstniejacych = (dokumenty) => default(WynikFragmentuOperacjiImportu);
        }
        public void RozpoczecieCalosci(int ilosc) { }
        public void ZakonczenieWszystkich() { }
        public void RozpoczeciePojedynczego(ImportSeryjnyEventArgs e) { }
        public void RozpoczecieFragmentuWImporciePojedynczego(ImportSeryjnyEventArgs e) { }
        public void ZakonczeniePojedynczego(ImportSeryjnyEventArgs e) { }
    }
}