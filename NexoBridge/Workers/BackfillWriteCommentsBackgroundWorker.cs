using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    public class BackfillWriteCommentsBackgroundWorker : BackgroundService
    {
        private readonly BackfillWriteCommentsJobQueue _jobQueue;
        private readonly BackfillWriteCommentsResultStore _resultStore;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<BackfillWriteCommentsBackgroundWorker> _workerLogger;

        public BackfillWriteCommentsBackgroundWorker(
            BackfillWriteCommentsJobQueue jobQueue,
            BackfillWriteCommentsResultStore resultStore,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _loggerFactory = loggerFactory;
            _workerLogger = loggerFactory.CreateLogger<BackfillWriteCommentsBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[BACKFILL KOMENTARZE WORKER] Gotowy do pracy. Czekam na zlecenia zapisu linków backfillu 2026.");
            var service = new BackfillService(_loggerFactory);

            while (!stoppingToken.IsCancellationRequested)
            {
                BackfillWriteCommentsJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation("Rozpoczynam zapis komentarzy backfillu {JobId} (wiersze={Count})",
                    job.JobId, job.Rows.Count);

                try
                {
                    BackfillWriteCommentsReport report = await service.WriteCommentsAsync(
                        job,
                        (procent, wiadomosc) => _resultStore.UpdateProgress(job.JobId, procent, wiadomosc),
                        stoppingToken);

                    _resultStore.Store(report);
                    _workerLogger.LogInformation("[BACKFILL KOMENTARZE ZAKOŃCZONE] {JobId}: {Message}", job.JobId, report.Message);
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[BACKFILL KOMENTARZE WORKER BŁĄD] {JobId}: {Message}", job.JobId, message);
                    _resultStore.Store(new BackfillWriteCommentsReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = message
                    });
                }
            }
        }
    }
}
