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
    /// Podgląd podatków (TaxPreviewCalculator) per klient biura - każdy klient w KOMPLETNIE OSOBNYM
    /// PROCESIE SYSTEMOWYM ("--tax-preview-client-worker"), dokładnie jak ZusOwnerContributionService:
    /// Sfera nie znosi wielokrotnego logowania w jednym, długo działającym procesie (patrz
    /// PayrollCountsService.cs). Wszystkie okresy klienta liczone są w JEDNYM procesie potomnym (jedno
    /// logowanie ~13 s, potem ~1,5 s na okres - pomiary z NexoBridgeKonsola).
    ///
    /// Błąd jednego klienta nigdy nie przerywa paczki. Po NotAfterUtc kolejni klienci dostają
    /// SKIPPED_DEADLINE. Timeout procesu skaluje się z liczbą okresów, a proces potomny dostaje
    /// wcześniejszy miękki limit, żeby sam zwrócił wynik częściowy zamiast zostać zabitym (TryKill nie
    /// zwalnia licencji Sfery).
    /// </summary>
    public class TaxPreviewService
    {
        private const int DomyslnyTimeoutBazowySekundy = 90;
        private const int DomyslnyTimeoutNaOkresSekundy = 20;
        private const int ZapasMiekkiegoLimituSekundy = 25;

        private readonly ILogger _logger;

        public TaxPreviewService(ILogger logger)
        {
            _logger = logger;
        }

        internal async Task<TaxPreviewBatchReport> ComputeAllAsync(
            TaxPreviewBatchJob job, CancellationToken cancellationToken, Func<int, string, Task> raportujPostep)
        {
            var report = new TaxPreviewBatchReport { JobId = job.JobId, Status = "SUCCESS" };

            List<TaxPreviewClientRequest> clients = job.Clients
                .Where(c => !string.IsNullOrWhiteSpace(c.Nip) && !string.IsNullOrWhiteSpace(c.DatabaseName) && c.Periods.Count > 0)
                .ToList();

            int total = clients.Count;
            int processed = 0;
            int failedCount = 0;
            int skippedCount = 0;

            foreach (TaxPreviewClientRequest client in clients)
            {
                await raportujPostep(
                    5 + (int)(90.0 * processed / Math.Max(1, total)),
                    $"{processed + 1}/{total}: {client.Nip}...");

                TaxPreviewClientItem item;
                if (job.NotAfterUtc.HasValue && DateTimeOffset.UtcNow > job.NotAfterUtc.Value)
                {
                    item = new TaxPreviewClientItem
                    {
                        Nip = client.Nip,
                        Status = "SKIPPED_DEADLINE",
                        Error = "Nie rozpoczęto - przekroczono NotAfterUtc zlecenia."
                    };
                    skippedCount++;
                }
                else
                {
                    item = await PoliczDlaKlientaWProcesie(client, job, cancellationToken);
                    if (item.Status == "FAILED")
                    {
                        failedCount++;
                    }
                }

                report.Items.Add(item);
                processed++;
            }

            int policzeni = report.Items.Count - skippedCount;
            report.Status = failedCount == 0 && skippedCount == 0
                ? "SUCCESS"
                : failedCount == report.Items.Count ? "FAILED" : "PARTIAL_SUCCESS";
            report.Message = $"Policzono podgląd podatków {policzeni} klientów ({failedCount} błędów, {skippedCount} pominiętych po terminie).";
            await raportujPostep(100, report.Message);
            return report;
        }

        private async Task<TaxPreviewClientItem> PoliczDlaKlientaWProcesie(
            TaxPreviewClientRequest client, TaxPreviewBatchJob job, CancellationToken cancellationToken)
        {
            var item = new TaxPreviewClientItem { Nip = client.Nip };

            TimeSpan timeout = TimeSpan.FromSeconds(
                CzytajLiczbe("TAX_PREVIEW_CHILD_TIMEOUT_BASE_SECONDS", DomyslnyTimeoutBazowySekundy)
                + CzytajLiczbe("TAX_PREVIEW_CHILD_TIMEOUT_PER_PERIOD_SECONDS", DomyslnyTimeoutNaOkresSekundy) * client.Periods.Count);

            var request = new TaxPreviewWorkerRequest
            {
                Username = job.Username,
                Password = job.Password,
                DatabaseName = client.DatabaseName,
                Periods = client.Periods,
                SoftDeadlineUtc = DateTimeOffset.UtcNow + timeout - TimeSpan.FromSeconds(ZapasMiekkiegoLimituSekundy),
            };

            try
            {
                (string fileName, string argsPrefix) = ResolveSelfLaunchCommand();
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = argsPrefix + "--tax-preview-client-worker",
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

                    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutCts.CancelAfter(timeout);
                        try
                        {
                            await process.WaitForExitAsync(timeoutCts.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            TryKill(process);
                            item.Status = "FAILED";
                            item.Error = $"Przekroczono czas oczekiwania na osobny proces liczący tego klienta ({timeout.TotalSeconds:0} s).";
                            return item;
                        }
                    }

                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;

                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    {
                        item.Status = "FAILED";
                        item.Error = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"Proces potomny zakończył się kodem {process.ExitCode} bez wyniku.";
                        _logger.LogWarning("Proces podglądu podatków dla klienta NIP={Nip} (baza {Database}) zakończył się bez wyniku: {Error}",
                            client.Nip, client.DatabaseName, item.Error);
                        return item;
                    }

                    string resultJson = ExtractBetweenMarkers(stdout, global::NexoBridge.Program.PayrollWorkerResultStartMarker, global::NexoBridge.Program.PayrollWorkerResultEndMarker);
                    if (resultJson == null)
                    {
                        item.Status = "FAILED";
                        item.Error = "Proces potomny nie zwrócił rozpoznawalnego wyniku (brak znaczników w stdout).";
                        _logger.LogWarning(
                            "Proces podglądu podatków dla klienta NIP={Nip} (baza {Database}): nie znaleziono znaczników wyniku w stdout. " +
                            "Surowe stdout ({StdoutLength} znaków): {Stdout} | stderr: {Stderr}",
                            client.Nip, client.DatabaseName, stdout.Length, Truncate(stdout, 2000), Truncate(stderr, 2000));
                        return item;
                    }

                    TaxPreviewWorkerResponse response;
                    try
                    {
                        response = JsonSerializer.Deserialize<TaxPreviewWorkerResponse>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (JsonException ex)
                    {
                        item.Status = "FAILED";
                        item.Error = $"Nie udało się rozebrać wyniku procesu potomnego: {ex.Message}";
                        _logger.LogWarning(ex,
                            "Proces podglądu podatków dla klienta NIP={Nip} (baza {Database}): niepoprawny JSON między znacznikami: {ResultJson}",
                            client.Nip, client.DatabaseName, Truncate(resultJson, 2000));
                        return item;
                    }

                    item.Status = response.Status;
                    item.Error = response.Error;
                    item.Periods = response.Periods ?? new List<TaxPreviewPeriodResult>();

                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        _logger.LogWarning("Proces podglądu podatków dla klienta NIP={Nip} (baza {Database}) zgłosił ostrzeżenia: {Stderr}",
                            client.Nip, client.DatabaseName, stderr.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                item.Status = "FAILED";
                item.Error = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "Nie udało się uruchomić osobnego procesu podglądu podatków dla klienta NIP={Nip} (baza {Database}).",
                    client.Nip, client.DatabaseName);
            }

            return item;
        }

        private static int CzytajLiczbe(string nazwaZmiennej, int domyslna)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(nazwaZmiennej), out int wartosc) && wartosc > 0 ? wartosc : domyslna;
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

        /// <summary>Identyczna logika co ZusOwnerContributionService.ResolveSelfLaunchCommand.</summary>
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
