using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace NexoBridge.Services
{
    /// <summary>Czy w bazie klienta w ogóle prowadzono kadry: Present / Absent (brak umów pracowniczych,
    /// list płac i wynagrodzeń) / Unknown (nie udało się sprawdzić - wtedy liczymy normalnie, niczego nie maskujemy).</summary>
    internal enum PayrollPresence
    {
        Unknown,
        Present,
        Absent
    }

    /// <summary>
    /// Prosty, tylko-do-odczytu SELECT w bazie klienta (bez Sfery) PRZED uruchomieniem procesu liczącego kadry.
    /// Baza, w której kadry nigdy nie były prowadzone, przy starcie Gratyfikanta w Sferze kończy się wyjątkiem
    /// "Operation is not valid due to the current state of the object." (InvalidOperationException - ten sam
    /// komunikat co przy logowaniu złym produktem, patrz BackfillService.ZlyProduktKomunikat). Takiej bazy nie ma
    /// sensu liczyć (wynik zawsze 0), a nie powinna wyglądać jak błąd - dostaje status NO_PAYROLL.
    /// Zapytanie sprawdzone w NexoBridgeKonsola (--scan-dbs-sql) na wszystkich 203 bazach Nexo_*: bazy z błędem
    /// startu Gratyfikanta (51 w październiku) miały 0 wierszy w trzech tabelach, a wszystkie bazy z danymi
    /// liczyły się poprawnie. Błąd z wersją bazy (np. Brzostowska) dotyczy bazy Z kadrami, więc nadal jest FAILED.
    /// </summary>
    internal static class PayrollPresenceChecker
    {
        public const string NoPayrollStatus = "NO_PAYROLL";

        // Komunikat InvalidOperationException z Uruchom(): Sfera nie startuje Gratyfikanta w bazie bez danych kadrowych.
        private const string StartFailureMessage = "Operation is not valid due to the current state of the object";

        private const string Sql = @"
            SELECT CASE WHEN EXISTS (SELECT 1 FROM ModelDanychContainer.UmowyPracownicze)
                          OR EXISTS (SELECT 1 FROM ModelDanychContainer.ListyWynagrodzen)
                          OR EXISTS (SELECT 1 FROM ModelDanychContainer.Wynagrodzenia)
                        THEN 1 ELSE 0 END";

        public static bool IsStartFailure(string error)
        {
            return !string.IsNullOrEmpty(error) && error.Contains(StartFailureMessage, StringComparison.Ordinal);
        }

        public static async Task<PayrollPresence> CheckAsync(string databaseName, ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                await using var connection = new SqlConnection(OfficeReportsSqlService.BuildConnectionString(databaseName));
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand(Sql, connection) { CommandTimeout = 30 };
                object result = await command.ExecuteScalarAsync(cancellationToken);
                return Convert.ToInt32(result) == 1 ? PayrollPresence.Present : PayrollPresence.Absent;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Brak tabel / połączenia / konfiguracji SQL - nie zgadujemy, liczymy po staremu (błąd zostanie błędem).
                logger.LogWarning(ex, "Nie udało się sprawdzić obecności danych kadrowych w bazie {Database} - liczę bez wstępnego sprawdzenia.", databaseName);
                return PayrollPresence.Unknown;
            }
        }
    }
}
