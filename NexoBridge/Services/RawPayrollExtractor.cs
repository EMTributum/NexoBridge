using System;
using System.Linq;
using InsERT.Moria.Sfera;
using static NexoBridge.Services.SferaReflectionHelpers;

namespace NexoBridge.Services
{
    /// <summary>
    /// Liczy SUROWE rachunki do umów pracowniczych i wypłaty bezpośrednio z bazy Gratyfikant klienta
    /// (IRachunkiDoUmowPracowniczychDane / IWyplatyDane .WszystkieDostepne() + filtr po dacie w kodzie),
    /// BEZ pośrednictwa licznika obiektów cennika (ILicznikObiektow) - port trybu diagnostycznego
    /// --dump-raw-payroll z NexoBillingKonsola/Program.cs, zweryfikowanego na żywych danych 4 klientów
    /// (dokładna zgodność z licznikiem cennikowym tam, gdzie ten drugi w ogóle istniał).
    ///
    /// W odróżnieniu od PayrollLineSpecExtractor (cennikowego, patrz ten plik) ten ekstraktor NIE zależy
    /// od żadnej konfiguracji cennika kadrowego klienta biura - liczy dla KAŻDEGO klienta z własną bazą
    /// Nexo, w tym tych bez cennika kadrowego (rozliczanych ryczałtem w cenie księgowości), którzy przy
    /// mechanizmie cennikowym byli całkowicie pomijani (patrz ExtractEligibleSpecs w PayrollCountsService).
    ///
    /// Musi być wołany w sesji Sfery połączonej WPROST z bazą KLIENTA (ProductId.Gratyfikant) - patrz
    /// RawPayrollCountsService.PoliczDlaKlientaWProcesie / Program.RunRawPayrollClientWorker.
    /// </summary>
    internal static class RawPayrollExtractor
    {
        public static int CountRachunkiDoUmowPracowniczych(Uchwyt sfera, DateTime periodStart, DateTime periodEnd)
        {
            InsERT.Moria.Place.Duze.IRachunkiDoUmowPracowniczych manager =
                GetRequiredService<InsERT.Moria.Place.Duze.IRachunkiDoUmowPracowniczych>(sfera, DateTime.Today);
            InsERT.Moria.Place.Duze.IRachunkiDoUmowPracowniczychDane dane =
                GetManagerDataOrContainer<InsERT.Moria.Place.Duze.IRachunkiDoUmowPracowniczychDane>(
                    sfera, manager, "IRachunkiDoUmowPracowniczych.Dane");

            return QueryAllViaWszystkieDostepne(dane, new[] { "Podmiot", "Umowa" })
                .Count(r => IsWithinPeriod(ReadDateCandidate(r, "Data", "DataWystawienia"), periodStart, periodEnd));
        }

        public static int CountWyplaty(Uchwyt sfera, DateTime periodStart, DateTime periodEnd)
        {
            InsERT.Moria.Place.Duze.IWyplaty manager =
                GetRequiredService<InsERT.Moria.Place.Duze.IWyplaty>(sfera, DateTime.Today);
            InsERT.Moria.Place.Duze.IWyplatyDane dane =
                GetManagerDataOrContainer<InsERT.Moria.Place.Duze.IWyplatyDane>(sfera, manager, "IWyplaty.Dane");

            return QueryAllViaWszystkieDostepne(dane, new[] { "ListaPlac", "ListaPlac.Definicja" })
                .Count(w => IsWithinPeriod(
                    ReadDateCandidate(w, "DataWyplaty", "ListaPlac.DataWyplaty", "ListaPlac.Definicja.Miesiac", "ListaPlac.Definicja.DataWyplaty"),
                    periodStart, periodEnd));
        }

        private static bool IsWithinPeriod(DateTime? date, DateTime periodStart, DateTime periodEndExclusive)
        {
            return date.HasValue && date.Value >= periodStart && date.Value < periodEndExclusive;
        }
    }
}
