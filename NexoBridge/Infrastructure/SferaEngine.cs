using InsERT.Moria.Sfera;
using InsERT.Mox.Product;
using System;
using System.IO;
using System.Reflection;

namespace NexoBridge.Infrastructure
{
    public class SferaEngine : IDisposable
    {
        public Uchwyt Sfera { get; private set; }

        static SferaEngine()
        {
            if (System.Windows.Application.Current == null) new System.Windows.Application();
        }

        // Teraz przyjmujemy login i hasło przekazane prosto z żądania aplikacji!
        public void Uruchom(string nexoUser, string nexoPass, string dbName)
        {
            Uruchom(nexoUser, nexoPass, dbName, ProductId.Rachmistrz, null);
        }

        public void Uruchom(string nexoUser, string nexoPass, string dbName, ProductId product)
        {
            Uruchom(nexoUser, nexoPass, dbName, product, null);
        }

        public void Uruchom(string nexoUser, string nexoPass, string dbName, Action<int, string> raportujPostep)
        {
            Uruchom(nexoUser, nexoPass, dbName, ProductId.Rachmistrz, raportujPostep);
        }

        public void Uruchom(string nexoUser, string nexoPass, string dbName, ProductId product, Action<int, string> raportujPostep)
        {
            raportujPostep?.Invoke(5, "Ładowanie bibliotek nexo...");
            string nexoBinPath = Environment.GetEnvironmentVariable("NEXO_BIN_PATH");

            // 1. Oszukujemy system, zmieniając katalog roboczy na folder z InsERTem
            Directory.SetCurrentDirectory(nexoBinPath);

            // 2. Dodajemy folder InsERTu do systemowej zmiennej PATH w locie
            var currentPath = Environment.GetEnvironmentVariable("PATH");
            if (!currentPath.Contains(nexoBinPath))
            {
                Environment.SetEnvironmentVariable("PATH", nexoBinPath + ";" + currentPath);
            }

            raportujPostep?.Invoke(15, "Przygotowanie połączenia z bazą danych...");
            string server = Environment.GetEnvironmentVariable("DB_SERVER");
            string dbUser = Environment.GetEnvironmentVariable("DB_USER");
            string dbPass = Environment.GetEnvironmentVariable("DB_PASS");

            var polaczenieSql = DanePolaczenia.Jawne(server, dbName, false, dbUser, dbPass);

            var dane = new DaneDoUruchomieniaSfery
            {
                DanePolaczenia = polaczenieSql,
                Produkt = product,
                LoginNexo = nexoUser,
                HasloNexo = nexoPass
            };

            var postepSfery = new PostepLadowaniaSfery((procent, opis) =>
            {
                int lokalnyProcent = 20 + (int)Math.Round(procent * 0.75m, MidpointRounding.AwayFromZero);
                raportujPostep?.Invoke(lokalnyProcent, "Logowanie do Sfery: " + opis);
            });

            this.Sfera = Uchwyty.UtworzNowy(dane, postepSfery);
            raportujPostep?.Invoke(100, "Sfera gotowa.");
        }

        public void Dispose()
        {
            // Bezpieczne zwalnianie licencji po zakończeniu paczki EPP
            Sfera?.Dispose();
        }
    }
}
