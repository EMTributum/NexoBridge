using System;
using System.Collections.Generic;
using System.Linq;
using InsERT.Moria.Klienci;
using InsERT.Moria.ModelDanych;
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
    /// WszystkieWgUprawnien() zwraca surowe encje ModelDanych.NaliczenieSkladekZus, które NIE mają
    /// kwot (DoZaplaty, SumaSkladek, ...) - te istnieją dopiero na obiekcie biznesowym
    /// INaliczenieSkladekZus, dlatego każdą encję otwieramy przez manager.Znajdz(encja) i od razu
    /// zwalniamy (bez Zapisz() - nic nie trafia do bazy).
    ///
    /// Musi być wołany w sesji Sfery połączonej WPROST z bazą KLIENTA jako ProductId.Rachmistrz -
    /// rozliczenia właścicielskie to moduł Rachmistrza (tak jak import/dekretacja), nie Gratyfikanta -
    /// patrz ZusOwnerContributionService.PoliczDlaKlientaWProcesie / Program.RunZusOwnerClientWorker.
    ///
    /// Dotyczy WYŁĄCZNIE ZUS-u właściciela (JDG płacący za siebie / osoba współpracująca) -
    /// zbiorczy ZUS od wynagrodzeń pracowników żyje w zupełnie innej strukturze (elementy
    /// pojedynczych wypłat, patrz RawPayrollExtractor) i nie jest tu liczony.
    /// </summary>
    internal static class ZusOwnerContributionExtractor
    {
        public static List<ZusOwnerContributionEntry> GetOwnerContributionsForPeriod(Uchwyt sfera, int periodYear, int periodMonth)
        {
            INaliczeniaSkladekZus manager = GetRequiredService<INaliczeniaSkladekZus>(sfera, DateTime.Today);
            INaliczeniaSkladekZusDane dane = GetManagerDataOrContainer<INaliczeniaSkladekZusDane>(
                sfera, manager, "INaliczeniaSkladekZus.Dane");

            DateTime periodStart = new DateTime(periodYear, periodMonth, 1);
            DateTime periodEnd = periodStart.AddMonths(1);

            List<NaliczenieSkladekZus> naliczenia = dane.WszystkieWgUprawnien()
                .Where(n => n.MiesiacNaliczenia >= periodStart && n.MiesiacNaliczenia < periodEnd)
                .ToList();

            var entries = new List<ZusOwnerContributionEntry>();
            foreach (NaliczenieSkladekZus naliczenie in naliczenia.Where(IsOwnerContribution))
            {
                using (INaliczenieSkladekZus bo = manager.Znajdz(naliczenie))
                {
                    // Kwoty na BO to pola (_doZaplaty itd.) wypełniane WYŁĄCZNIE przez ObliczPolaWyliczane() -
                    // Znajdz() go nie woła, więc bez tego wszystko byłoby 0. Liczy tylko w pamięci, ze
                    // składników (Kwota) zapisanych w bazie - ta sama formuła co w Nexo, bez zapisu.
                    bo.ObliczPolaWyliczane();

                    entries.Add(new ZusOwnerContributionEntry
                    {
                        PersonName = naliczenie.OsobaKtorejDotyczy?.Nazwa ?? naliczenie.Wspolnik?.Nazwa,
                        Rodzaj = naliczenie.Rodzaj?.Nazwa,
                        DoZaplaty = bo.DoZaplaty,
                        SumaSkladek = bo.SumaSkladek,
                        UbezpieczenieSpoleczne = bo.UbezpieczenieSpoleczne,
                        UbezpieczenieZdrowotne = bo.UbezpieczenieZdrowotne,
                    });
                }
            }

            return entries;
        }

        /// <summary>Encja NaliczenieSkladekZus ma też pola innych rozliczeń właścicielskich (darowizny,
        /// ulgi, wynajem) - bierzemy tylko rodzaje oznaczone jako naliczenie ZUS właściciela / osoby
        /// współpracującej. Brak rodzaju (nie powinno się zdarzyć) traktujemy jako ZUS, żeby nie zgubić kwoty.</summary>
        private static bool IsOwnerContribution(NaliczenieSkladekZus naliczenie)
        {
            RodzajRozliczeniaWlascicielskiego rodzaj = naliczenie.Rodzaj;
            return rodzaj == null
                || rodzaj.NaliczenieSkladekZUSWlasciciela
                || rodzaj.NaliczenieSkladekZUSOsobyWspolpracujacej;
        }
    }
}
