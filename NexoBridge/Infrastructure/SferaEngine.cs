using InsERT.Moria.Sfera;
using InsERT.Mox.Product;
using DotNetEnv;
using System;
using System.IO;
using System.Reflection;
using NexoBridge.Infrastructure;

namespace NexoBridge.Infrastruktura
{
    public class SferaEngine : IDisposable
    {
        public Uchwyt Sfera { get; private set; }

        public SferaEngine()
        {
            // [NEXO:] Sfera wymaga wczytania swoich bibliotek do pamięci RAM przed uruchomieniem jakichkolwiek obiektów.
            Env.Load();

            AppDomain.CurrentDomain.AssemblyResolve += ResolvingAssemblies;
            if (System.Windows.Application.Current == null) new System.Windows.Application();
        }

        public void Uruchom()
        {
            string server = Environment.GetEnvironmentVariable("DB_SERVER");
            string dbName = Environment.GetEnvironmentVariable("DB_NAME");
            string dbUser = Environment.GetEnvironmentVariable("DB_USER");
            string dbPass = Environment.GetEnvironmentVariable("DB_PASS");
            string nexoUser = Environment.GetEnvironmentVariable("NEXO_USER");
            string nexoPass = Environment.GetEnvironmentVariable("NEXO_PASS");

            var polaczenieSql = DanePolaczenia.Jawne(server, dbName, false, dbUser, dbPass);

            var dane = new DaneDoUruchomieniaSfery()
            {
                DanePolaczenia = polaczenieSql,
                Produkt = ProductId.Rachmistrz,
                LoginNexo = nexoUser,
                HasloNexo = nexoPass
            };

            // [NEXO:] Utworzenie uchwytu to fizyczne zalogowanie się do bazy SQL i "zajęcie" licencji na program.
            this.Sfera = Uchwyty.UtworzNowy(dane, new PostepLadowaniaSfery());
        }


        private static Assembly ResolvingAssemblies(object sender, ResolveEventArgs args)
        {
            string nexoBinPath = @"C:\Automatyzacja\NexoBridge\NexoBridge\NexoDLLs";
            string assemblyName = new AssemblyName(args.Name).Name + ".dll";
            string assemblyPath = Path.Combine(nexoBinPath, assemblyName);
            return File.Exists(assemblyPath) ? Assembly.LoadFrom(assemblyPath) : null;
        }

        public void Dispose()
        {
            // [NEXO:] Pamiętaj, aby w API zawsze zamykać uchwyt, by zwalniać licencje InsERT!
            Sfera?.Dispose();
        }
    }
}