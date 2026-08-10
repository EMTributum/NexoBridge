using InsERT.Mox.Product;
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
    public sealed class RcpImportBackgroundWorker : BackgroundService
    {
        private readonly RcpImportJobQueue _jobQueue;
        private readonly RcpImportResultStore _resultStore;
        private readonly RcpImportStateStore _stateStore;
        private readonly RcpEmployeeMappingStore _mappingStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<RcpImportBackgroundWorker> _workerLogger;

        public RcpImportBackgroundWorker(
            RcpImportJobQueue jobQueue,
            RcpImportResultStore resultStore,
            RcpImportStateStore stateStore,
            RcpEmployeeMappingStore mappingStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _stateStore = stateStore;
            _mappingStore = mappingStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<RcpImportBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[RCP IMPORT WORKER] Gotowy do pracy. Czekam na zlecenia importu RCP...");

            while (!stoppingToken.IsCancellationRequested)
            {
                RcpImportJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation(
                    "Rozpoczynam import RCP {JobId} (Baza: {DatabaseName}, Okres: {PeriodYear}-{PeriodMonth:00}, Zrodlo: {SourceMode})",
                    job.JobId,
                    job.DatabaseName,
                    job.PeriodYear,
                    job.PeriodMonth,
                    job.SourceMode);

                try
                {
                    await WyslijPostep(job.JobId, 5, "Budzę Sferę Gratyfikanta...");

                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(job.Username, job.Password, job.DatabaseName, ProductId.Gratyfikant);

                        var serviceLogger = _loggerFactory.CreateLogger<RcpImportService>();
                        var service = new RcpImportService(_mappingStore, serviceLogger);
                        RcpImportReport report = await service.ImportAsync(
                            silnik.Sfera,
                            job,
                            async (procent, wiadomosc) => await WyslijPostep(job.JobId, procent, wiadomosc),
                            stoppingToken);

                        if (string.Equals(report.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                        {
                            await _stateStore.MarkSuccessfulImportAsync(report, stoppingToken);
                        }

                        _resultStore.Store(report);
                        await WyslijRaport(job.JobId, report);
                    }
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[RCP IMPORT WORKER BŁĄD] Wystąpił błąd podczas importu RCP {JobId}", job.JobId);

                    var report = new RcpImportReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = message,
                        DatabaseName = job.DatabaseName,
                        PeriodYear = job.PeriodYear,
                        PeriodMonth = job.PeriodMonth,
                        SourceMode = job.SourceMode,
                        SourceUrl = job.SourceUrl,
                        PayloadHash = job.PayloadHash,
                        StartedAtUtc = DateTimeOffset.UtcNow,
                        FinishedAtUtc = DateTimeOffset.UtcNow
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

        private async Task WyslijRaport(string jobId, RcpImportReport report)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonReport = JsonSerializer.Serialize(report, jsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveRcpImportReport", jsonReport);
        }
    }
}
