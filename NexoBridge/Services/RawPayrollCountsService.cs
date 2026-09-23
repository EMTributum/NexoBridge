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
    /// Liczy SUROWE rachunki do umów pracowniczych i wypłaty (RawPayrollExtractor) per klient biura -
    /// w JEDNEJ fazie, w odróżnieniu od PayrollCountsService: nie ma tu żadnego odpowiednika Fazy 1
    /// (odczyt cennika z sesji biura), bo ten mechanizm NIE zależy od konfiguracji cennika kadrowego -
    /// liczy się bezpośrednio z bazy Gratyfikant KAŻDEGO klienta przekazanego w ClientDatabases.
    ///
    /// Tak jak PayrollCountsService, każdy klient jest liczony w KOMPLETNIE OSOBNYM PROCESIE
    /// SYSTEMOWYM (patrz PoliczDlaKlientaWProcesie) - Sfera nie znosi wielokrotnego logowania/
    /// wylogowania w pętli w ramach jednego, długo działającego procesu (patrz pełne uzasadnienie w
    /// PayrollCountsService.cs). Błąd jednego klienta (zła/niedostępna baza, brak dostępu, wygasła
    /// licencja) nigdy nie przerywa całego batcha - trafia do RawPayrollCountsItem.Status=FAILED z
    /// komunikatem, reszta klientów jest liczona dalej.
    /// </summary>
    public class RawPayrollCountsService
    {
        private readonly ILogger _logger;

        public RawPayrollCountsService(ILogger logger)
        {
            _logger = logger;
        }

        internal async Task<RawPayrollCountsBatchReport> ComputeAllAsync(
            RawPayrollCountsBatchJob job, CancellationToken cancellationToken, Func<int, string, Task> raportujPostep)
        {
            var report = new RawPayrollCountsBatchReport { JobId = job.JobId, Status = "SUCCESS" };

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

                RawPayrollCountsItem item = await PoliczDlaKlientaWProcesie(client, job, cancellationToken);

                report.Items.Add(item);
                if (item.Status == "FAILED")
                {
                    failedCount++;
                }

                processed++;
            }

            report.Status = failedCount == 0 ? "SUCCESS" : (failedCount == report.Items.Count ? "FAILED" : "PARTIAL_SUCCESS");
            report.Message = $"Policzono surowo {report.Items.Count} klientów ({failedCount} błędów).";
            await raportujPostep(100, report.Message);
            return report;
        }

        /// <summary>Liczy surowe rachunki/wypłaty JEDNEGO klienta w KOMPLETNIE OSOBNYM procesie
        /// systemowym - odpala ponownie ten sam plik wykonywalny NexoBridge z flagą
        /// "--raw-payroll-client-worker" (patrz Program.cs.RunRawPayrollClientWorker), przekazuje dane
        /// wejściowe jako JSON na stdin i czyta wynik jako JSON ze stdout. Hasło idzie przez stdin, NIE
        /// przez argumenty procesu. Ta sama mechanika co PayrollCountsService.PoliczDlaKlientaWProcesie -
        /// patrz tam pełne uzasadnienie izolacji per-proces.</summary>
        private async Task<RawPayrollCountsItem> PoliczDlaKlientaWProcesie(
            ClientDatabaseRef client, RawPayrollCountsBatchJob job, CancellationToken cancellationToken)
        {
            var item = new RawPayrollCountsItem { Nip = client.Nip };

            (DateTime periodStart, DateTime periodEnd) = PayrollLineSpecExtractor.ResolvePeriodRange(job.PeriodYear, job.PeriodMonth);

            var request = new RawPayrollClientWorkerRequest
            {
                Username = job.Username,
                Password = job.Password,
                DatabaseName = client.DatabaseName,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
            };

            try
            {
                (string fileName, string argsPrefix) = ResolveSelfLaunchCommand();
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = argsPrefix + "--raw-payroll-client-worker",
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

                    // Twardy limit na JEDNEGO klienta - ten sam co PayrollCountsService (2 min).
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
                        _logger.LogWarning("Proces liczący surowe kadry dla klienta NIP={Nip} (baza {Database}) zakończył się bez wyniku: {Error}",
                            client.Nip, client.DatabaseName, item.Error);
                        return item;
                    }

                    string resultJson = ExtractBetweenMarkers(stdout, global::NexoBridge.Program.PayrollWorkerResultStartMarker, global::NexoBridge.Program.PayrollWorkerResultEndMarker);
                    if (resultJson == null)
                    {
                        item.Status = "FAILED";
                        item.Error = "Proces potomny nie zwrócił rozpoznawalnego wyniku (brak znaczników w stdout).";
                        _logger.LogWarning(
                            "Proces liczący surowe kadry dla klienta NIP={Nip} (baza {Database}): nie znaleziono znaczników wyniku w stdout. " +
                            "Surowe stdout ({StdoutLength} znaków): {Stdout} | stderr: {Stderr}",
                            client.Nip, client.DatabaseName, stdout.Length, Truncate(stdout, 2000), Truncate(stderr, 2000));
                        return item;
                    }

                    RawPayrollClientWorkerResponse response;
                    try
                    {
                        response = JsonSerializer.Deserialize<RawPayrollClientWorkerResponse>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (JsonException ex)
                    {
                        item.Status = "FAILED";
                        item.Error = $"Nie udało się rozebrać wyniku procesu potomnego: {ex.Message}";
                        _logger.LogWarning(ex,
                            "Proces liczący surowe kadry dla klienta NIP={Nip} (baza {Database}): niepoprawny JSON między znacznikami: {ResultJson}",
                            client.Nip, client.DatabaseName, Truncate(resultJson, 2000));
                        return item;
                    }

                    item.Status = response.Status;
                    item.Error = response.Error;
                    item.RachunekCount = response.RachunekCount;
                    item.WyplataCount = response.WyplataCount;

                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        _logger.LogWarning("Proces liczący surowe kadry dla klienta NIP={Nip} (baza {Database}) zgłosił ostrzeżenia: {Stderr}",
                            client.Nip, client.DatabaseName, stderr.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                item.Status = "FAILED";
                item.Error = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "Nie udało się uruchomić osobnego procesu liczącego surowe kadry dla klienta NIP={Nip} (baza {Database}).",
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
        /// potomny - identyczna logika co PayrollCountsService.ResolveSelfLaunchCommand.</summary>
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
