using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Hubs;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    /// <summary>Worker dla odczytu ZUS-u właściciela (ZusOwnerContributionService) - prostszy niż
    /// PayrollCountsBackgroundWorker: NIE otwiera żadnej sesji biura w tym procesie (brak Fazy 1/
    /// cennika do odczytania) - cała praca dzieje się w izolowanych procesach potomnych per klient,
    /// uruchamianych bezpośrednio przez ZusOwnerContributionService.ComputeAllAsync.</summary>
    public class ZusOwnerContributionBackgroundWorker : BackgroundService
    {
        private readonly ZusOwnerContributionJobQueue _jobQueue;
        private readonly ZusOwnerContributionResultStore _resultStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ZusOwnerContributionBackgroundWorker> _workerLogger;

        public ZusOwnerContributionBackgroundWorker(
            ZusOwnerContributionJobQueue jobQueue,
            ZusOwnerContributionResultStore resultStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<ZusOwnerContributionBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[ZUS OWNER WORKER] Gotowy do pracy. Czekam na zlecenia odczytu ZUS-u właściciela...");

            while (!stoppingToken.IsCancellationRequested)
            {
                ZusOwnerContributionBatchJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation(
                    "Rozpoczynam odczyt ZUS-u właściciela {JobId} (Okres: {Year}-{Month:00}, Klientów z bazą: {ClientCount})",
                    job.JobId, job.PeriodYear, job.PeriodMonth, job.ClientDatabases.Count);

                try
                {
                    var serviceLogger = _loggerFactory.CreateLogger<ZusOwnerContributionService>();
                    var service = new ZusOwnerContributionService(serviceLogger);

                    ZusOwnerContributionBatchReport report = await service.ComputeAllAsync(job, stoppingToken, async (procent, wiadomosc) =>
                    {
                        await WyslijPostep(job.JobId, procent, wiadomosc);
                    });

                    _resultStore.Store(report);
                    await WyslijRaport(job.JobId, report);
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[ZUS OWNER WORKER BŁĄD] Wystąpił błąd podczas odczytu ZUS-u właściciela {JobId}", job.JobId);

                    var report = new ZusOwnerContributionBatchReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = message
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

        private async Task WyslijRaport(string jobId, ZusOwnerContributionBatchReport report)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonReport = JsonSerializer.Serialize(report, jsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveZusOwnerContributionsReport", jsonReport);
        }
    }
}
