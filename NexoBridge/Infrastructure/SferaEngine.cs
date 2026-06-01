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
            Uruchom(nexoUser, nexoPass, dbName, ProductId.Rachmistrz);
        }

        public void Uruchom(string nexoUser, string nexoPass, string dbName, ProductId product)
        {
            string nexoBinPath = Environment.GetEnvironmentVariable("NEXO_BIN_PATH");

            // 1. Oszukujemy system, zmieniając katalog roboczy na folder z InsERTem
            Directory.SetCurrentDirectory(nexoBinPath);

            // 2. Dodajemy folder InsERTu do systemowej zmiennej PATH w locie
            var currentPath = Environment.GetEnvironmentVariable("PATH");
            if (!currentPath.Contains(nexoBinPath))
            {
                Environment.SetEnvironmentVariable("PATH", nexoBinPath + ";" + currentPath);
            }
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

            this.Sfera = Uchwyty.UtworzNowy(dane, new PostepLadowaniaSfery());
        }

        public void Dispose()
        {
            // Bezpieczne zwalnianie licencji po zakończeniu paczki EPP
            Sfera?.Dispose();
        }
    }
}
