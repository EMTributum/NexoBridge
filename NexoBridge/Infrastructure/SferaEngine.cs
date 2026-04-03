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
            AppDomain.CurrentDomain.AssemblyResolve += ResolvingAssemblies;
            if (System.Windows.Application.Current == null) new System.Windows.Application();
        }

        // Teraz przyjmujemy login i hasło przekazane prosto z żądania aplikacji!
        public void Uruchom(string nexoUser, string nexoPass)
        {
            string server = Environment.GetEnvironmentVariable("DB_SERVER");
            string dbName = Environment.GetEnvironmentVariable("DB_NAME");
            string dbUser = Environment.GetEnvironmentVariable("DB_USER");
            string dbPass = Environment.GetEnvironmentVariable("DB_PASS");

            var polaczenieSql = DanePolaczenia.Jawne(server, dbName, false, dbUser, dbPass);

            var dane = new DaneDoUruchomieniaSfery
            {
                DanePolaczenia = polaczenieSql,
                Produkt = ProductId.Rachmistrz,
                LoginNexo = nexoUser,
                HasloNexo = nexoPass
            };

            this.Sfera = Uchwyty.UtworzNowy(dane, new PostepLadowaniaSfery());
        }

        private static Assembly ResolvingAssemblies(object sender, ResolveEventArgs args)
        {
            // Zamiast wpisywać na sztywno, pobieramy ścieżkę do prawdziwego folderu Nexo z .env
            string nexoBinPath = Environment.GetEnvironmentVariable("NEXO_BIN_PATH");

            if (string.IsNullOrEmpty(nexoBinPath)) throw new Exception("Brak NEXO_BIN_PATH w pliku .env!");

            string assemblyName = new AssemblyName(args.Name).Name + ".dll";
            string assemblyPath = Path.Combine(nexoBinPath, assemblyName);

            return File.Exists(assemblyPath) ? Assembly.LoadFrom(assemblyPath) : null;
        }

        public void Dispose()
        {
            // Bezpieczne zwalnianie licencji po zakończeniu paczki EPP
            Sfera?.Dispose();
        }
    }
}