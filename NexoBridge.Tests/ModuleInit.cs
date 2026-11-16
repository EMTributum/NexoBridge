using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NexoBridge.Tests
{
    // Biblioteki nexo leza w katalogu wyjsciowym glownego projektu, nie testow - dociagamy je na zadanie.
    internal static class ModuleInit
    {
        [ModuleInitializer]
        internal static void Init()
        {
            string dir = Path.Combine(Path.GetDirectoryName(typeof(ModuleInit).Assembly.Location), "..", "..", "..", "..", "NexoBridge", "bin", "Debug", "NexoDLLs");
            dir = Path.GetFullPath(dir);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string f = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(f) ? Assembly.LoadFrom(f) : null;
            };
        }
    }
}
