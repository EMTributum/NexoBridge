using System;
using System.Collections.Generic;
using System.Linq;
using InsERT.Moria.Klienci;
using InsERT.Moria.Sfera;
using NexoBridge.Models;
using static NexoBridge.Services.SferaReflectionHelpers;

namespace NexoBridge.Services
{
    /// <summary>
    /// Odczytuje JUŻ POLICZONE w Nexo naliczenia składek ZUS właściciela (INaliczenieSkladekZus,
    /// namespace InsERT.Moria.Klienci) bezpośrednio z bazy Gratyfikant klienta - WYŁĄCZNIE do
    /// odczytu, przez INaliczeniaSkladekZusDane.WszystkieWgUprawnien(), NIGDY przez
    /// IUbezpieczeniaWlascicielskieZus.NaliczSkladkiZUS(...) (ta druga ścieżka liczy/zapisuje NOWY
    /// rekord w produkcyjnej bazie klienta - celowo jej tu nie używamy, ryzyko niechcianego zapisu).
    /// Jeśli biuro nie policzyło jeszcze ZUS-u klienta za dany miesiąc w samym Nexo, zwracamy pustą
    /// listę - NIE liczymy niczego na siłę.
    ///
    /// Dotyczy WYŁĄCZNIE ZUS-u właściciela (JDG płacący za siebie / osoba współpracująca) -
    /// zbiorczy ZUS od wynagrodzeń pracowników żyje w zupełnie innej strukturze (elementy
    /// pojedynczych wypłat, patrz RawPayrollExtractor) i nie jest tu liczony.
    ///
    /// Musi być wołany w sesji Sfery połączonej WPROST z bazą KLIENTA (ProductId.Gratyfikant) - patrz
    /// ZusOwnerContributionService.PoliczDlaKlientaWProcesie / Program.RunZusOwnerClientWorker.
    /// </summary>
    internal static class ZusOwnerContributionExtractor
    {
        public static List<ZusOwnerContributionEntry> GetOwnerContributionsForPeriod(Uchwyt sfera, int periodYear, int periodMonth)
        {
            INaliczeniaSkladekZus manager = GetRequiredService<INaliczeniaSkladekZus>(sfera, DateTime.Today);
            INaliczeniaSkladekZusDane dane = GetManagerDataOrContainer<INaliczeniaSkladekZusDane>(
                sfera, manager, "INaliczeniaSkladekZus.Dane");

            List<object> naliczenia = InvokeParameterlessCollectionMethod(dane, "WszystkieWgUprawnien");

            return naliczenia
                .Where(n => IsWithinPeriod(ReadDateCandidate(n, "MiesiacNaliczenia", "Data"), periodYear, periodMonth))
                .Select(n => new ZusOwnerContributionEntry
                {
                    PersonName = ReadStringCandidate(n,
                        "OsobaKtorejDotyczy.NazwaPelna", "OsobaKtorejDotyczy.Nazwa",
                        "Wspolnik.NazwaPelna", "Wspolnik.Nazwa"),
                    DoZaplaty = ReadDecimalCandidate(n, "DoZaplaty"),
                    SumaSkladek = ReadDecimalCandidate(n, "SumaSkladek"),
                    UbezpieczenieSpoleczne = ReadDecimalCandidate(n, "UbezpieczenieSpoleczne"),
                    UbezpieczenieZdrowotne = ReadDecimalCandidate(n, "UbezpieczenieZdrowotne"),
                })
                .ToList();
        }

        private static bool IsWithinPeriod(DateTime? date, int periodYear, int periodMonth)
        {
            return date.HasValue && date.Value.Year == periodYear && date.Value.Month == periodMonth;
        }
    }
}
