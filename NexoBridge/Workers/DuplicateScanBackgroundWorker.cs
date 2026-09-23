using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Hubs;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    public class DuplicateScanBackgroundWorker : BackgroundService
    {
        private readonly DuplicateScanJobQueue _jobQueue;
        private readonly DuplicateScanResultStore _resultStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<DuplicateScanBackgroundWorker> _workerLogger;

        public DuplicateScanBackgroundWorker(
            DuplicateScanJobQueue jobQueue,
            DuplicateScanResultStore resultStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<DuplicateScanBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[SKAN DUPLIKATÓW WORKER] Gotowy do pracy. Czekam na zlecenia rocznego skanu duplikatów...");

            while (!stoppingToken.IsCancellationRequested)
            {
                YearlyDuplicateScanJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation("Rozpoczynam roczny skan duplikatów: {JobId} (Baza: {Database}, Rok: {Year})",
                    job.JobId, job.DatabaseName, job.Year);

                try
                {
                    await WyslijPostep(job.JobId, 10, "Budzenie Sfery...");

                    using (await SferaSessionGate.AcquireAsync(stoppingToken))
                    using (var silnik = new SferaEngine())
                    {
                        // Bez jawnego ProductId - domyślny to Rachmistrz (SferaEngine.Uruchom), a to
                        // tam żyją rejestry VAT/KPiR/EP, których potrzebuje InvoiceDuplicateDetectionService.
                        silnik.Uruchom(job.Username, job.Password, job.DatabaseName);
                        await WyslijPostep(job.JobId, 30, "Połączono z Rachmistrzem. Pobieram zapisy za cały rok...");

                        var serviceLogger = _loggerFactory.CreateLogger<InvoiceDuplicateDetectionService>();
                        var service = new InvoiceDuplicateDetectionService(silnik.Sfera, serviceLogger);
                        var matches = await service.SprawdzDuplikatyRoczneAsync(job.Year);

                        await WyslijPostep(job.JobId, 90, "Zestawiam wyniki...");

                        var report = new YearlyDuplicateScanReport
                        {
                            JobId = job.JobId,
                            Status = "SUCCESS",
                            Message = matches.Count > 0
                                ? $"Znaleziono {matches.Count} potencjalnych duplikatów."
                                : "Nie znaleziono podejrzanych duplikatów.",
                            Nip = job.Nip,
                            Year = job.Year,
                            Matches = matches,
                        };

                        _resultStore.Store(report);
                        await WyslijPostep(job.JobId, 100, report.Message);
                        await WyslijRaport(job.JobId, report);
                    }
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[SKAN DUPLIKATÓW WORKER BŁĄD] Wystąpił błąd podczas rocznego skanu duplikatów {JobId}", job.JobId);

                    var report = new YearlyDuplicateScanReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = message,
                        Nip = job.Nip,
                        Year = job.Year,
                    };

                    report.Warnings.Add(message);
                    _resultStore.Store(report);

                    await WyslijPostep(job.JobId, 100, $"BŁĄD: {message}");
                    await WyslijRaport(job.JobId, report);
                }
            }
        }

        private async Task WyslijPostep(string jobId, int procent, string wiadomosc)
        {
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveProgress", procent, wiadomosc, jobId);
        }

        private async Task WyslijRaport(string jobId, YearlyDuplicateScanReport report)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonReport = JsonSerializer.Serialize(report, jsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveDuplicateScanReport", jsonReport);
        }
    }
}
