using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;

namespace NexoBridge.Services
{
    /// <summary>
    /// Odczytuje naliczenia ZUS właściciela (ZusOwnerContributionExtractor) per klient biura - w
    /// JEDNEJ fazie, tak jak RawPayrollCountsService: nie ma tu żadnej konfiguracji cennika do
    /// odczytania w sesji biura, bo ZUS właściciela nie jest pozycją cennikową - czyta się
    /// bezpośrednio z bazy Gratyfikant KAŻDEGO klienta przekazanego w ClientDatabases.
    ///
    /// Tak jak RawPayrollCountsService/PayrollCountsService, każdy klient jest liczony w KOMPLETNIE
    /// OSOBNYM PROCESIE SYSTEMOWYM (patrz PoliczDlaKlientaWProcesie) - Sfera nie znosi wielokrotnego
    /// logowania/wylogowania w pętli w ramach jednego, długo działającego procesu (patrz pełne
    /// uzasadnienie w PayrollCountsService.cs). Błąd jednego klienta (zła/niedostępna baza, brak
    /// dostępu, wygasła licencja) nigdy nie przerywa całego batcha - trafia do
    /// ZusOwnerContributionItem.Status=FAILED z komunikatem, reszta klientów jest liczona dalej.
    /// </summary>
    public class ZusOwnerContributionService
    {
        private readonly ILogger _logger;

        public ZusOwnerContributionService(ILogger logger)
        {
            _logger = logger;
        }

        internal async Task<ZusOwnerContributionBatchReport> ComputeAllAsync(
            ZusOwnerContributionBatchJob job, CancellationToken cancellationToken, Func<int, string, Task> raportujPostep)
        {
            var report = new ZusOwnerContributionBatchReport { JobId = job.JobId, Status = "SUCCESS" };

            List<ClientDatabaseRef> clients = job.ClientDatabases
                .Where(c => !string.IsNullOrWhiteSpace(c.Nip) && !string.IsNullOrWhiteSpace(c.DatabaseName))
                .ToList();

            int total = clients.Count;
            int processed = 0;
            int failedCount = 0;

            foreach (ClientDatabaseRef client in clients)
            {
                await raportujPostep(
                    5 + (int)(90.0 * processed / Math.Max(1, total)),
                    $"{processed + 1}/{total}: {client.Nip}...");

                ZusOwnerContributionItem item = await PoliczDlaKlientaWProcesie(client, job, cancellationToken);

                report.Items.Add(item);
                if (item.Status == "FAILED")
                {
                    failedCount++;
                }

                processed++;
            }

            report.Status = failedCount == 0 ? "SUCCESS" : (failedCount == report.Items.Count ? "FAILED" : "PARTIAL_SUCCESS");
            report.Message = $"Odczytano ZUS właściciela {report.Items.Count} klientów ({failedCount} błędów).";
            await raportujPostep(100, report.Message);
            return report;
        }

        /// <summary>Odczytuje naliczenia ZUS właściciela JEDNEGO klienta w KOMPLETNIE OSOBNYM
        /// procesie systemowym - odpala ponownie ten sam plik wykonywalny NexoBridge z flagą
        /// "--zus-owner-client-worker" (patrz Program.cs.RunZusOwnerClientWorker), przekazuje dane
        /// wejściowe jako JSON na stdin i czyta wynik jako JSON ze stdout. Hasło idzie przez stdin,
        /// NIE przez argumenty procesu. Ta sama mechanika co
        /// RawPayrollCountsService.PoliczDlaKlientaWProcesie - patrz tam pełne uzasadnienie izolacji
        /// per-proces.</summary>
        private async Task<ZusOwnerContributionItem> PoliczDlaKlientaWProcesie(
            ClientDatabaseRef client, ZusOwnerContributionBatchJob job, CancellationToken cancellationToken)
        {
            var item = new ZusOwnerContributionItem { Nip = client.Nip };

            var request = new ZusOwnerContributionWorkerRequest
            {
                Username = job.Username,
                Password = job.Password,
                DatabaseName = client.DatabaseName,
                PeriodYear = job.PeriodYear,
                PeriodMonth = job.PeriodMonth,
            };

            try
            {
                (string fileName, string argsPrefix) = ResolveSelfLaunchCommand();
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = argsPrefix + "--zus-owner-client-worker",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();

                    string requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    await process.StandardInput.WriteAsync(requestJson);
                    process.StandardInput.Close();

                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                    // Twardy limit na JEDNEGO klienta - ten sam co PayrollCountsService/RawPayrollCountsService (2 min).
                    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
                        try
                        {
                            await process.WaitForExitAsync(timeoutCts.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            TryKill(process);
                            item.Status = "FAILED";
                            item.Error = "Przekroczono czas oczekiwania na osobny proces liczący tego klienta (2 min).";
                            return item;
                        }
                    }

                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;

                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    {
                        item.Status = "FAILED";
                        item.Error = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"Proces potomny zakończył się kodem {process.ExitCode} bez wyniku.";
                        _logger.LogWarning("Proces odczytujący ZUS właściciela dla klienta NIP={Nip} (baza {Database}) zakończył się bez wyniku: {Error}",
                            client.Nip, client.DatabaseName, item.Error);
                        return item;
                    }

                    string resultJson = ExtractBetweenMarkers(stdout, global::NexoBridge.Program.PayrollWorkerResultStartMarker, global::NexoBridge.Program.PayrollWorkerResultEndMarker);
                    if (resultJson == null)
                    {
                        item.Status = "FAILED";
                        item.Error = "Proces potomny nie zwrócił rozpoznawalnego wyniku (brak znaczników w stdout).";
                        _logger.LogWarning(
                            "Proces odczytujący ZUS właściciela dla klienta NIP={Nip} (baza {Database}): nie znaleziono znaczników wyniku w stdout. " +
                            "Surowe stdout ({StdoutLength} znaków): {Stdout} | stderr: {Stderr}",
                            client.Nip, client.DatabaseName, stdout.Length, Truncate(stdout, 2000), Truncate(stderr, 2000));
                        return item;
                    }

                    ZusOwnerContributionWorkerResponse response;
                    try
                    {
                        response = JsonSerializer.Deserialize<ZusOwnerContributionWorkerResponse>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (JsonException ex)
                    {
                        item.Status = "FAILED";
                        item.Error = $"Nie udało się rozebrać wyniku procesu potomnego: {ex.Message}";
                        _logger.LogWarning(ex,
                            "Proces odczytujący ZUS właściciela dla klienta NIP={Nip} (baza {Database}): niepoprawny JSON między znacznikami: {ResultJson}",
                            client.Nip, client.DatabaseName, Truncate(resultJson, 2000));
                        return item;
                    }

                    item.Status = response.Status;
                    item.Error = response.Error;
                    item.Entries = response.Entries ?? new List<ZusOwnerContributionEntry>();

                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        _logger.LogWarning("Proces odczytujący ZUS właściciela dla klienta NIP={Nip} (baza {Database}) zgłosił ostrzeżenia: {Stderr}",
                            client.Nip, client.DatabaseName, stderr.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                item.Status = "FAILED";
                item.Error = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "Nie udało się uruchomić osobnego procesu odczytującego ZUS właściciela dla klienta NIP={Nip} (baza {Database}).",
                    client.Nip, client.DatabaseName);
            }

            return item;
        }

        private static string ExtractBetweenMarkers(string text, string startMarker, string endMarker)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            int startIndex = text.IndexOf(startMarker, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                return null;
            }

            startIndex += startMarker.Length;
            int endIndex = text.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
            if (endIndex < 0)
            {
                return null;
            }

            return text.Substring(startIndex, endIndex - startIndex);
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength) + "... (obcięto)";
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Proces mógł już się zakończyć między timeoutem a próbą zabicia - nieistotne.
            }
        }

        /// <summary>Ustala, jak ponownie odpalić TEN SAM plik wykonywalny NexoBridge jako proces
        /// potomny - identyczna logika co PayrollCountsService/RawPayrollCountsService.ResolveSelfLaunchCommand.</summary>
        private static (string FileName, string ArgsPrefix) ResolveSelfLaunchCommand()
        {
            string processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                processPath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            string fileNameOnly = Path.GetFileNameWithoutExtension(processPath ?? string.Empty);
            if (string.Equals(fileNameOnly, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                return (processPath, $"\"{dllPath}\" ");
            }

            return (processPath, string.Empty);
        }
    }
}
