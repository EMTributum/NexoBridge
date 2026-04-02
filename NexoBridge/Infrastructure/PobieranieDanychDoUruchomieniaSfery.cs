using System;
using InsERT.Mox.Product;

namespace NexoBridge.Infrastructure
{
    public class PobieranieDanychDoUruchomieniaSfery
    {
        public DaneDoUruchomieniaSfery Pobierz()
        {
            Console.WriteLine("Podaj serwer:");
            string serwer = Console.ReadLine();

            Console.WriteLine("Podaj bazę:");
            string baza = Console.ReadLine();

            Console.WriteLine("Podaj login do serwera (pusty, jeśli chcesz się uwierzytelnić kontem Windows):");
            string loginSql = Console.ReadLine();

            Console.WriteLine("Podaj hasło do serwera (pusty, jeśli chcesz się uwierzytelnić kontem Windows):");
            string hasloSql = PobierzHaslo();

            Console.WriteLine("Podaj login nexo:");
            string loginNexo = Console.ReadLine();

            Console.WriteLine("Podaj hasło nexo:");
            string hasloNexo = PobierzHaslo();

            Console.WriteLine("Podaj produkt:");
            string produktJakoTekst = Console.ReadLine();
            var produkt = ParsujProdukt(produktJakoTekst);

            return new DaneDoUruchomieniaSfery()
            {
                Serwer = serwer,
                Baza = baza,
                LoginSql = loginSql,
                HasloSql = hasloSql,
                LoginNexo = loginNexo,
                HasloNexo = hasloNexo,
                Produkt = produkt
            };
        }

        private ProductId ParsujProdukt(string produktJakoTekst)
        {
            if (Enum.TryParse<ProductId>(produktJakoTekst, out var productId))
            {
                return productId;
            }
            else
            {
                throw new ArgumentException("Nie ma takiego produktu: " + produktJakoTekst);
            }
        }

        public static string PobierzHaslo()
        {
            string haslo = string.Empty;

            var info = Console.ReadKey(intercept: true);
            while (info.Key != ConsoleKey.Enter)
            {
                if (info.Key == ConsoleKey.Backspace)
                {
                    if (haslo.Length > 0)
                    {
                        haslo = haslo.Remove(haslo.Length - 1);
                        Console.Write("\b \b");
                    }
                }
                else if (!char.IsControl(info.KeyChar))
                {
                    Console.Write("*");
                    haslo += info.KeyChar;
                }
                info = Console.ReadKey(intercept: true);
            }

            Console.WriteLine();

            return haslo;
        }
    }
}
