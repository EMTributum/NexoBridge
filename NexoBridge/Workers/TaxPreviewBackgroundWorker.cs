using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Hubs;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    /// <summary>Worker podglądu podatków (TaxPreviewService) - jak ZusOwnerContributionBackgroundWorker:
    /// NIE otwiera sesji Sfery w tym procesie (bez SferaSessionGate), cała praca dzieje się w procesach
    /// potomnych per klient. Wynik odbierany przez REST (GET /api/jobs/tax-preview/{jobId}); przez
    /// SignalR idzie tylko postęp.</summary>
    public class TaxPreviewBackgroundWorker : BackgroundService
    {
        private readonly TaxPreviewJobQueue _jobQueue;
        private readonly TaxPreviewResultStore _resultStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<TaxPreviewBackgroundWorker> _workerLogger;

        public TaxPreviewBackgroundWorker(
            TaxPreviewJobQueue jobQueue,
            TaxPreviewResultStore resultStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<TaxPreviewBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[TAX PREVIEW WORKER] Gotowy do pracy. Czekam na zlecenia podglądu podatków...");

            while (!stoppingToken.IsCancellationRequested)
            {
                TaxPreviewBatchJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation(
                    "Rozpoczynam podgląd podatków {JobId} (Klientów: {ClientCount}, NotAfterUtc: {NotAfterUtc})",
                    job.JobId, job.Clients.Count, job.NotAfterUtc);

                try
                {
                    var service = new TaxPreviewService(_loggerFactory.CreateLogger<TaxPreviewService>());
                    TaxPreviewBatchReport report = await service.ComputeAllAsync(job, stoppingToken, async (procent, wiadomosc) =>
                    {
                        await WyslijPostep(job.JobId, procent, wiadomosc);
                    });

                    _resultStore.Store(report);
                    _workerLogger.LogInformation("[TAX PREVIEW WORKER] {JobId}: {Status} - {Message}", job.JobId, report.Status, report.Message);
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[TAX PREVIEW WORKER BŁĄD] Wystąpił błąd podczas podglądu podatków {JobId}", job.JobId);

                    var report = new TaxPreviewBatchReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = message
                    };

                    report.Warnings.Add(message);
                    _resultStore.Store(report);
                    await WyslijPostep(job.JobId, 100, $"BŁĄD: {message}");
                }
            }
        }

        private async Task WyslijPostep(string jobId, int procent, string wiadomosc)
        {
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveProgress", procent, wiadomosc, jobId);
        }
    }
}
