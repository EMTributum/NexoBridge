using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using InsERT.Moria.Ksiegowosc;
using InsERT.Moria.KsiegowoscUproszczona;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Infrastructure;
using Serilog;
using Serilog.Extensions.Logging;

namespace NexoBridge.Services
{
    /// <summary>
    /// Ręczny test poprawki KontekstOkresuObrachunkowegoScope na żywej bazie klienta, BEZ zapisu
    /// czegokolwiek: tworzy w pamięci zapis EP rodzaju "Wynajem" ze stawką 8,5% i ustawia kwotę -
    /// dokładnie ta zmiana właściwości odpala w Sferze walidację limitu najmu, która 2026-10-05 wywaliła
    /// dekretację FS 28/2026 u COR-CAD. Najpierw bez kontekstu okresu (oczekiwany ten sam wyjątek), potem
    /// w KontekstOkresuObrachunkowegoScope (oczekiwany brak wyjątku), na końcu kontrola, że scope
    /// przywrócił kontekst sesji. Zapisz() nie jest wołane nigdzie - obiekty są tylko zwalniane.
    ///
    /// Użycie: NexoBridge.exe --diag-kontekst-okresu --db "Nexo_..." --user "login" [--data 2026-09-30]
    /// Hasło: zmienna NEXO_DIAG_PASS albo pytanie na konsoli (wpisywane bez echa).
    /// </summary>
    internal static class KontekstOkresuDiagnostyka
    {
        private static readonly Guid RodzajZapisuWynajemId = new Guid("b68c645a-075c-4543-867e-71c395ac01d2");
        private const string SymbolStawki85 = "8,5%";

        public static int Run(string[] args)
        {
            string database = Argument(args, "--db");
            string user = Argument(args, "--user");
            DateTime data = DateTime.TryParse(Argument(args, "--data"), out DateTime parsed) ? parsed.Date : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
            if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(user))
            {
                Console.WriteLine("Użycie: NexoBridge.exe --diag-kontekst-okresu --db \"Nexo_...\" --user \"login\" [--data yyyy-MM-dd]");
                return 2;
            }

            string password = Environment.GetEnvironmentVariable("NEXO_DIAG_PASS");
            if (string.IsNullOrEmpty(password))
            {
                password = WczytajHaslo($"Hasło operatora nexo '{user}': ");
            }

            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateLogger();
            Microsoft.Extensions.Logging.ILogger logger = new SerilogLoggerFactory(Log.Logger).CreateLogger("diag-kontekst-okresu");

            int bledy = 0;
            void Wynik(bool ok, string opis)
            {
                Console.WriteLine($"{(ok ? "[OK]  " : "[BŁĄD]")} {opis}");
                if (!ok) bledy++;
            }

            try
            {
                using var silnik = new SferaEngine();
                Console.WriteLine($"Logowanie do Sfery (Rachmistrz, jak joby importu): baza={database}, operator={user}...");
                silnik.Uruchom(user, password, database);
                Uchwyt sfera = silnik.Sfera;

                var kontekst = sfera.PodajObiektTypu<IKontekstOkresuObrachunkowego>();
                PropertyInfo dataSystemowaProp = kontekst?.GetType().GetProperty("DataSystemowa", BindingFlags.Instance | BindingFlags.Public);
                DateTime? DataSystemowa() => dataSystemowaProp?.GetValue(kontekst) as DateTime?;

                Console.WriteLine($"Kontekst sesji: typ={kontekst?.GetType().FullName ?? "null"}; okres={Opisz(kontekst?.OkresObrachunkowy)}; dataSystemowa={DataSystemowa():yyyy-MM-dd}");
                Wynik(kontekst != null, "PodajObiektTypu<IKontekstOkresuObrachunkowego>() zwraca kontekst sesji");
                if (kontekst == null) return 1;

                OkresObrachunkowy okresStart = kontekst.OkresObrachunkowy;
                DateTime? dataStart = DataSystemowa();
                Wynik(okresStart == null, $"świeża sesja NexoBridge ma PUSTY okres w kontekście (potwierdzenie przyczyny; jest: {Opisz(okresStart)})");

                OkresObrachunkowy okres = ZnajdzOkres(sfera, data);
                Console.WriteLine($"Okres dla daty {data:yyyy-MM-dd}: {Opisz(okres)}; FormaKsiegowosci={okres?.FormaKsiegowosci}");
                if (okres == null) { Wynik(false, "brak okresu obrachunkowego dla podanej daty"); return 1; }

                var zapisy = sfera.PodajObiektTypu<IZapisyWEP>();
                RodzajZapisuKsiegowego wynajem = ZnajdzRodzajWynajem(zapisy, okres);
                Wynik(wynajem != null, $"rodzaj zapisu 'Wynajem' ({RodzajZapisuWynajemId}) odczytany z istniejącego zapisu EP w okresie");
                if (wynajem == null) return 1;

                // A: bez kontekstu - oczekiwany ten sam wyjątek co w jobach z 2026-10-05.
                if (okresStart == null)
                {
                    Exception a = UstawKwoteWynajmu(zapisy, data, wynajem, kontekst, out bool tenSamKontekstA);
                    Wynik(tenSamKontekstA, "obiekt zapisu EP widzi TEN SAM obiekt kontekstu co PodajObiektTypu (ustawienie trafi tam, gdzie czyta walidacja)");
                    bool odtworzono = a is ArgumentNullException ane && string.Equals(ane.ParamName, "okresObrachunkowy", StringComparison.OrdinalIgnoreCase);
                    Wynik(odtworzono, "A) bez kontekstu: odtworzono ArgumentNullException('okresObrachunkowy') z produkcji" + (odtworzono ? "" : $" - zamiast tego: {a?.GetType().Name ?? "brak wyjątku"}: {a?.Message}"));
                }
                else
                {
                    Console.WriteLine("A) pominięte - kontekst sesji już miał okres, więc błędu nie da się odtworzyć.");
                }

                // B: w scope - oczekiwany brak wyjątku.
                using (var scope = KontekstOkresuObrachunkowegoScope.Ustaw(sfera, okres, logger))
                {
                    Wynik(scope.Aktywny && kontekst.OkresObrachunkowy?.Id == okres.Id, $"B) scope ustawił okres w kontekście (jest: {Opisz(kontekst.OkresObrachunkowy)}; dataSystemowa={DataSystemowa():yyyy-MM-dd})");
                    Exception b = UstawKwoteWynajmu(zapisy, data, wynajem, kontekst, out _);
                    Wynik(b == null, "B) z kontekstem: ustawienie kwoty na zapisie 'Wynajem' 8,5% przechodzi bez wyjątku" + (b == null ? "" : $" - wyjątek: {b.GetType().Name}: {b.Message}"));
                }

                Wynik(kontekst.OkresObrachunkowy?.Id == okresStart?.Id, $"po scope kontekst wrócił do stanu wyjściowego (okres: {Opisz(kontekst.OkresObrachunkowy)})");
                Wynik(DataSystemowa() == dataStart, $"po scope data systemowa bez zmian ({dataStart:yyyy-MM-dd} -> {DataSystemowa():yyyy-MM-dd})");
            }
            catch (Exception ex)
            {
                Wynik(false, $"nieoczekiwany wyjątek: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
                Console.WriteLine(ex);
            }
            finally
            {
                Log.CloseAndFlush();
            }

            Console.WriteLine(bledy == 0 ? "WYNIK: wszystkie sprawdzenia OK (nic nie zostało zapisane w bazie)." : $"WYNIK: {bledy} sprawdzeń nie przeszło (nic nie zostało zapisane w bazie).");
            return bledy == 0 ? 0 : 1;
        }

        /// <summary>Tworzy zapis EP w pamięci i ustawia kwotę w stawce 8,5% - bez Zapisz(). Zwraca wyjątek
        /// (najgłębszy) albo null.</summary>
        private static Exception UstawKwoteWynajmu(IZapisyWEP zapisy, DateTime data, RodzajZapisuKsiegowego wynajem, IKontekstOkresuObrachunkowego kontekst, out bool tenSamKontekst)
        {
            tenSamKontekst = false;
            using IZapisWEP zapis = zapisy.Utworz();
            object kontekstZapisu = PoleWHierarchii(zapis, "_kontekstOkresuObrachunkowego");
            tenSamKontekst = ReferenceEquals(kontekstZapisu, kontekst);
            try
            {
                zapis.Dane.DataZapisu = data;
                zapis.Dane.DataZdarzenia = data;
                zapis.Dane.Rodzaj = wynajem;
                ((IPozycjeZapisuWEP)zapis).UstawKwoteWWalucie(SymbolStawki85, 100m);
                return null;
            }
            catch (Exception ex)
            {
                Exception e = ex;
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                return e;
            }
        }

        private static OkresObrachunkowy ZnajdzOkres(Uchwyt sfera, DateTime data)
        {
            var okresy = ((IEnumerable)sfera.PodajObiektTypu<IOkresyObrachunkowe>().Dane.Wszystkie()).Cast<OkresObrachunkowy>().ToList();
            return okresy
                .Where(o => o.Okres != null && o.Okres.DataPoczatkowa.Date <= data && o.Okres.DataKoncowa.Date >= data)
                .OrderByDescending(o => o.Okres.DataPoczatkowa)
                .FirstOrDefault();
        }

        /// <summary>Słownik rodzajów zapisów nie ma w API listy, więc encję bierzemy z dowolnego istniejącego
        /// zapisu EP tego rodzaju w okresie (tylko odczyt) - u COR-CAD to FS 28/2026.</summary>
        private static RodzajZapisuKsiegowego ZnajdzRodzajWynajem(IZapisyWEP zapisy, OkresObrachunkowy okres)
        {
            return ((IEnumerable)zapisy.Dane.PobierzZapisyZOkresu(okres.Okres.DataPoczatkowa, okres.Okres.DataKoncowa))
                .Cast<ZapisWEP>()
                .Select(z => z.Rodzaj)
                .FirstOrDefault(r => r != null && r.Id == RodzajZapisuWynajemId);
        }

        private static object PoleWHierarchii(object obj, string nazwa)
        {
            for (Type t = obj?.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(nazwa, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(obj);
            }
            return null;
        }

        private static string Opisz(OkresObrachunkowy okres) =>
            okres == null ? "brak" : $"{okres.Nazwa} (Id={okres.Id})";

        private static string Argument(string[] args, string nazwa)
        {
            int i = Array.IndexOf(args, nazwa);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static string WczytajHaslo(string prompt)
        {
            Console.Write(prompt);
            if (Console.IsInputRedirected)
            {
                return Console.ReadLine() ?? string.Empty;
            }

            var haslo = new System.Text.StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (haslo.Length > 0) haslo.Length--; continue; }
                haslo.Append(key.KeyChar);
            }
            Console.WriteLine();
            return haslo.ToString();
        }
    }
}
