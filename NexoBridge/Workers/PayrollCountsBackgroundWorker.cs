using InsERT.Mox.Product;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Hubs;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    /// <summary>Osobny worker (nie BillingClientsBackgroundWorker) - inny kształt wykonania: jedna
    /// sesja biura + N osobnych sesji Gratyfikanta (per klient), z realnym postępem i częściowymi
    /// błędami, zamiast jednej sesji Subiekt zwracającej całość na raz. Patrz PayrollCountsService.</summary>
    public class PayrollCountsBackgroundWorker : BackgroundService
    {
        private readonly PayrollCountsJobQueue _jobQueue;
        private readonly PayrollCountsResultStore _resultStore;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<PayrollCountsBackgroundWorker> _workerLogger;

        public PayrollCountsBackgroundWorker(
            PayrollCountsJobQueue jobQueue,
            PayrollCountsResultStore resultStore,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _workerLogger = _loggerFactory.CreateLogger<PayrollCountsBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[PAYROLL COUNTS WORKER] Gotowy do pracy. Czekam na zlecenia liczenia pozycji kadrowo-płacowych...");

            while (!stoppingToken.IsCancellationRequested)
            {
                PayrollCountsBatchJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation(
                    "Rozpoczynam liczenie pozycji kadrowo-płacowych {JobId} (Baza biura: {Database}, Okres: {Year}-{Month:00}, Klientów z bazą: {ClientCount})",
                    job.JobId, job.OfficeDatabaseName, job.PeriodYear, job.PeriodMonth, job.ClientDatabases.Count);

                try
                {
                    await WyslijPostep(job.JobId, 2, "Budzenie Sfery biura...");

                    PayrollCountsService service;
                    List<ClientPayrollSpec> specs;
                    using (await SferaSessionGate.AcquireAsync(stoppingToken))
                    using (SferaEngine silnik = await OtworzSesjeBiuraZPonowieniem(job, stoppingToken))
                    {
                        var serviceLogger = _loggerFactory.CreateLogger<PayrollCountsService>();
                        service = new PayrollCountsService(silnik.Sfera, serviceLogger);
                        specs = service.ExtractEligibleSpecs();
                    }
                    // Sesja biura już zamknięta i bramka zwolniona TUTAJ - Faza 2 (per klient, patrz
                    // ComputeAllAsync) nie trzyma jej otwartej przez cały czas trwania batcha.

                    PayrollCountsBatchReport report = await service.ComputeAllAsync(specs, job, stoppingToken, async (procent, wiadomosc) =>
                    {
                        await WyslijPostep(job.JobId, procent, wiadomosc);
                    });

                    _resultStore.Store(report);
                    await WyslijRaport(job.JobId, report);
                }
                catch (Exception ex)
                {
                    string message = ex.GetBaseException().Message;
                    _workerLogger.LogError(ex, "[PAYROLL COUNTS WORKER BŁĄD] Wystąpił błąd podczas liczenia pozycji kadrowo-płacowych {JobId}", job.JobId);

                    var report = new PayrollCountsBatchReport
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

        /// <summary>Otwarcie sesji biura z JEDNYM ponowieniem - obserwowany na produkcji sporadyczny,
        /// niedeterministyczny wyjątek wewnątrz zamkniętego kodu silnika Sfery przy starcie sesji
        /// (System.IndexOutOfRangeException w RegulyAutomatyzacjiModule.ConfigureCore, poza naszą
        /// kontrolą) potrafił ubić CAŁY batch dla wszystkich klientów jedną nieudaną próbą otwarcia
        /// TEJ JEDNEJ, pierwszej sesji biura. Krótkie ponowienie po chwili kosztuje niewiele, a chroni
        /// całą resztę batcha przed pojedynczym, przejściowym zacięciem.</summary>
        private async Task<SferaEngine> OtworzSesjeBiuraZPonowieniem(PayrollCountsBatchJob job, CancellationToken stoppingToken)
        {
            const int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var silnik = new SferaEngine();
                try
                {
                    silnik.Uruchom(job.Username, job.Password, job.OfficeDatabaseName, ProductId.Subiekt);
                    return silnik;
                }
                catch (Exception ex)
                {
                    silnik.Dispose();
                    if (attempt >= maxAttempts)
                    {
                        throw;
                    }
                    _workerLogger.LogWarning(ex,
                        "Nie udało się otworzyć sesji biura Sfery dla zlecenia {JobId} (próba {Attempt}/{MaxAttempts}) - ponawiam za 5s.",
                        job.JobId, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            throw new InvalidOperationException("Nieosiągalne - pętla ponowień zawsze zwraca lub rzuca.");
        }

        private async Task WyslijPostep(string jobId, int procent, string wiadomosc)
        {
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveProgress", procent, wiadomosc, jobId);
        }

        private async Task WyslijRaport(string jobId, PayrollCountsBatchReport report)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonReport = JsonSerializer.Serialize(report, jsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceivePayrollCountsReport", jsonReport);
        }
    }
}
