using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    public class BackfillEnumerateBackgroundWorker : BackgroundService
    {
        private readonly BackfillEnumerateJobQueue _jobQueue;
        private readonly BackfillEnumerateResultStore _resultStore;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<BackfillEnumerateBackgroundWorker> _workerLogger;

        public BackfillEnumerateBackgroundWorker(
            BackfillEnumerateJobQueue jobQueue,
            BackfillEnumerateResultStore resultStore,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _loggerFactory = loggerFactory;
            _workerLogger = loggerFactory.CreateLogger<BackfillEnumerateBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[BACKFILL ENUMERATE WORKER] Gotowy do pracy. Czekam na zlecenia backfillu 2026.");
            var service = new BackfillService(_loggerFactory);

            while (!stoppingToken.IsCancellationRequested)
            {
                BackfillEnumerateJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation("Rozpoczynam enumerację backfillu {JobId} (klienci={Count}, rok={Year})",
                    job.JobId, job.Clients.Count, job.Year);

                try
                {
                    BackfillEnumerateReport report = await service.EnumerateAsync(
                        job,
                        (procent, wiadomosc) => _resultStore.UpdateProgress(job.JobId, procent, wiadomosc),
                        stoppingToken);

                    _resultStore.Store(report);
                    _workerLogger.LogInformation("[BACKFILL ENUMERATE ZAKOŃCZONE] {JobId}: {Message}", job.JobId, report.Message);
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[BACKFILL ENUMERATE WORKER BŁĄD] {JobId}: {Message}", job.JobId, message);
                    _resultStore.Store(new BackfillEnumerateReport
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
