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
    public class InvoiceCreationBackgroundWorker : BackgroundService
    {
        private readonly InvoiceCreationJobQueue _jobQueue;
        private readonly InvoiceCreationResultStore _resultStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<InvoiceCreationBackgroundWorker> _workerLogger;

        public InvoiceCreationBackgroundWorker(
            InvoiceCreationJobQueue jobQueue,
            InvoiceCreationResultStore resultStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<InvoiceCreationBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[INVOICE CREATION WORKER] Gotowy do pracy. Czekam na zlecenia tworzenia faktur...");

            while (!stoppingToken.IsCancellationRequested)
            {
                InvoiceCreationJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation("Rozpoczynam tworzenie faktury: {JobId} (Baza: {Database}, NIP: {Nip}, Okres: {Year}-{Month})",
                    job.JobId, job.DatabaseName, job.Nip, job.ServiceYear, job.ServiceMonth);

                try
                {
                    await WyslijPostep(job.JobId, 5, "Budzenie Sfery...");

                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(job.Username, job.Password, job.DatabaseName, ProductId.Subiekt);

                        var serviceLogger = _loggerFactory.CreateLogger<InvoiceCreationService>();
                        var service = new InvoiceCreationService(silnik.Sfera, serviceLogger);
                        var report = await service.UtworzFaktureAsync(job, async (procent, wiadomosc) =>
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
                    _workerLogger.LogError(ex, "[INVOICE CREATION WORKER BŁĄD] Wystąpił błąd podczas tworzenia faktury {JobId}", job.JobId);

                    var report = new InvoiceCreationReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = message,
                        DatabaseName = job.DatabaseName,
                        Nip = job.Nip
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

        private async Task WyslijRaport(string jobId, InvoiceCreationReport report)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonReport = JsonSerializer.Serialize(report, jsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveInvoiceCreationReport", jsonReport);
        }
    }
}
