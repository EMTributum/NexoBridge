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
    public class BillingClientsBackgroundWorker : BackgroundService
    {
        private readonly BillingClientsJobQueue _jobQueue;
        private readonly BillingClientsResultStore _resultStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<BillingClientsBackgroundWorker> _workerLogger;

        public BillingClientsBackgroundWorker(
            BillingClientsJobQueue jobQueue,
            BillingClientsResultStore resultStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<BillingClientsBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[BILLING CLIENTS WORKER] Gotowy do pracy. Czekam na zlecenia odczytu listy klientów do rozliczenia...");

            while (!stoppingToken.IsCancellationRequested)
            {
                BillingClientsJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation("Rozpoczynam odczyt listy klientów do rozliczenia: {JobId} (Baza: {Database})",
                    job.JobId, job.DatabaseName);

                try
                {
                    await WyslijPostep(job.JobId, 10, "Budzenie Sfery...");

                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(job.Username, job.Password, job.DatabaseName, ProductId.Subiekt);

                        var serviceLogger = _loggerFactory.CreateLogger<BillingConfigurationService>();
                        var service = new BillingConfigurationService(silnik.Sfera, serviceLogger);
                        var report = await service.PobierzListeKlientowAsync(job, async (procent, wiadomosc) =>
                        {
                            await WyslijPostep(job.JobId, procent, wiadomosc);
                        });

                        _resultStore.Store(report);
                        await WyslijRaport(job.JobId, report);
                    }
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[BILLING CLIENTS WORKER BŁĄD] Wystąpił błąd podczas odczytu listy klientów {JobId}", job.JobId);

                    var report = new BillingClientsReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = message,
                        DatabaseName = job.DatabaseName
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

        private async Task WyslijRaport(string jobId, BillingClientsReport report)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonReport = JsonSerializer.Serialize(report, jsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveBillingClientsReport", jsonReport);
        }
    }
}
