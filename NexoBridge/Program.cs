using System;
using NexoBridge.Infrastruktura;
using NexoBridge.Serwisy;

namespace NexoBridge
{
    public class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Console.WriteLine("Inicjalizacja NexoBridge...");

            // W przyszłości (API) ten blok będzie zarządzany przez wbudowany kontener DI (Dependency Injection)
            using (var silnik = new SilnikSfery())
            {
                try
                {
                    silnik.Uruchom();
                    Console.WriteLine("[SUKCES] Połączono ze Sferą!");

                    var serwisKsiegowy = new SerwisKsiegowy(silnik.Sfera);

                    Console.WriteLine("\n[ETAP 1] Wczytywanie pliku EPP...");
                    serwisKsiegowy.PrzetworzPlikEpp(@"C:\Automatyzacja\NexoBridge\NexoBridge\faktury\faktura.epp");

                    Console.WriteLine("\n[ETAP 2] Automatyczna Dekretacja...");
                    serwisKsiegowy.UruchomAutomatycznaDekretacje();

                    Console.WriteLine("\n[GOTOWE] Proces zakończony pomyślnie.");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[BŁĄD KRYTYCZNY]: {ex.Message}");
                    Console.ResetColor();
                }
            }

            Console.ReadLine();
        }
    }
}