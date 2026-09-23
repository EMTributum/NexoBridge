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
    /// <summary>Worker dla liczenia surowych danych kadrowo-płacowych (RawPayrollCountsService) -
    /// prostszy niż PayrollCountsBackgroundWorker: NIE otwiera żadnej sesji biura w tym procesie (nie
    /// ma Fazy 1/cennika do odczytania) - cała praca dzieje się w izolowanych procesach potomnych per
    /// klient, uruchamianych bezpośrednio przez RawPayrollCountsService.ComputeAllAsync.</summary>
    public class RawPayrollCountsBackgroundWorker : BackgroundService
    {
        private readonly RawPayrollCountsJobQueue _jobQueue;
        private readonly RawPayrollCountsResultStore _resultStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<RawPayrollCountsBackgroundWorker> _workerLogger;

        public RawPayrollCountsBackgroundWorker(
            RawPayrollCountsJobQueue jobQueue,
            RawPayrollCountsResultStore resultStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<RawPayrollCountsBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[RAW PAYROLL COUNTS WORKER] Gotowy do pracy. Czekam na zlecenia liczenia surowych danych kadrowych...");

            while (!stoppingToken.IsCancellationRequested)
            {
                RawPayrollCountsBatchJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation(
                    "Rozpoczynam liczenie surowych danych kadrowych {JobId} (Okres: {Year}-{Month:00}, Klientów z bazą: {ClientCount})",
                    job.JobId, job.PeriodYear, job.PeriodMonth, job.ClientDatabases.Count);

                try
                {
                    var serviceLogger = _loggerFactory.CreateLogger<RawPayrollCountsService>();
                    var service = new RawPayrollCountsService(serviceLogger);

                    RawPayrollCountsBatchReport report = await service.ComputeAllAsync(job, stoppingToken, async (procent, wiadomosc) =>
                    {
                        await WyslijPostep(job.JobId, procent, wiadomosc);
                    });

                    _resultStore.Store(report);
                    await WyslijRaport(job.JobId, report);
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[RAW PAYROLL COUNTS WORKER BŁĄD] Wystąpił błąd podczas liczenia surowych danych kadrowych {JobId}", job.JobId);

                    var report = new RawPayrollCountsBatchReport
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

        private async Task WyslijRaport(string jobId, RawPayrollCountsBatchReport report)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonReport = JsonSerializer.Serialize(report, jsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveRawPayrollCountsReport", jsonReport);
        }
    }
}
