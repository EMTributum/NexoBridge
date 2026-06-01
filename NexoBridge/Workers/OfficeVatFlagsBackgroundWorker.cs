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
    public class OfficeVatFlagsBackgroundWorker : BackgroundService
    {
        private readonly OfficeVatFlagsJobQueue _jobQueue;
        private readonly OfficeVatFlagsResultStore _resultStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<OfficeVatFlagsBackgroundWorker> _workerLogger;

        public OfficeVatFlagsBackgroundWorker(
            OfficeVatFlagsJobQueue jobQueue,
            OfficeVatFlagsResultStore resultStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<OfficeVatFlagsBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[OFFICE VAT-FLAGS WORKER] Gotowy do pracy. Czekam na zlecenia odczytu flag VAT/VAT-UE z Biura...");

            while (!stoppingToken.IsCancellationRequested)
            {
                OfficeVatFlagsJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation("Rozpoczynam odczyt flag VAT/VAT-UE z Biura: {JobId} (Baza: {Database}, NIP: {Nip})",
                    job.JobId, job.OfficeDatabaseName, job.Nip);

                try
                {
                    await WyslijPostep(job.JobId, 10, "Budzenie Sfery Biura...");

                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(job.Username, job.Password, job.OfficeDatabaseName, ProductId.Biuro);

                        var serviceLogger = _loggerFactory.CreateLogger<OfficeVatFlagsService>();
                        var service = new OfficeVatFlagsService(silnik.Sfera, serviceLogger);
                        var report = await service.PobierzFlagiAsync(job, async (procent, wiadomosc) =>
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
                    _workerLogger.LogError(ex, "[OFFICE VAT-FLAGS WORKER BŁĄD] Wystąpił błąd podczas odczytu flag VAT/VAT-UE z Biura {JobId}", job.JobId);

                    var report = new OfficeVatFlagsReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = message,
                        OfficeDatabaseName = job.OfficeDatabaseName,
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
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveProgress", procent, wiadomosc);
        }

        private async Task WyslijRaport(string jobId, OfficeVatFlagsReport report)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonReport = JsonSerializer.Serialize(report, jsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveOfficeVatFlagsReport", jsonReport);
        }
    }
}
