using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Infrastructure
{
    /// <summary>
    /// Globalna, procesowa blokada: w danej chwili aktywna jest NAJWYŻEJ JEDNA sesja Sfery w całym
    /// procesie NexoBridge, niezależnie z którego workera/kolejki pochodzi.
    ///
    /// Wprowadzona po serii produkcyjnych awarii w Payroll Counts występujących wyłącznie wtedy, gdy
    /// inny job (np. odczyt listy klientów do rozliczenia) uruchamiał się w tym samym momencie:
    ///   - System.IndexOutOfRangeException wewnątrz wewnętrznego HashSet-a Sfery
    ///     (RegulyAutomatyzacjiModule.ConfigureCore -> ZarejestrujObslugiwaneBO),
    ///   - "Item has already been added. Key in dictionary: 'TouchScreenMode'" w WPF ResourceDictionary
    ///     (InsERT.Moria.Components.UI.WindowManager),
    ///   - "Brak zalogowanego operatora" w trakcie wołania licznika (Zlicz) na sesji, która przecież
    ///     poprawnie się zalogowała chwilę wcześniej.
    /// Wszystkie trzy to klasyczne objawy współbieżnego dostępu do procesowego, statycznego stanu
    /// wewnątrz zamkniętego SDK InsERT-u (Sfera), które ewidentnie zakłada, że w danym procesie działa
    /// NAJWYŻEJ JEDNA aktywna sesja na raz - typowe dla SDK projektowanego pod aplikację desktopową,
    /// nie pod wieloworkerowy serwis z wieloma niezależnymi kolejkami (BillingClients, RcpImport,
    /// OfficeVatFlags, InvoiceCreation, PayrollCounts, ...), które w NexoBridge potrafią uruchamiać się
    /// w tym samym momencie.
    ///
    /// KAŻDE miejsce otwierające SferaEngine musi trzymać tę bramkę przez cały czas AKTYWNEGO
    /// korzystania z sesji (od Uruchom() do ostatniej operacji na tej sesji) - patrz użycia w
    /// Workers/*.cs i Services/PayrollCountsService.cs.
    /// </summary>
    public static class SferaSessionGate
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken);
            return new Releaser();
        }

        private sealed class Releaser : IDisposable
        {
            private bool _released;

            public void Dispose()
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                Gate.Release();
            }
        }
    }
}
