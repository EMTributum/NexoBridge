using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Hubs;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Text.Json; // Wymagane do serializacji raportu
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    public class NexoBackgroundWorker : BackgroundService
    {
        private readonly JobQueue _jobQueue;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<NexoBackgroundWorker> _workerLogger;
        private readonly NexoBridgeErrorReporter _errorReporter;
        public NexoBackgroundWorker(
            JobQueue jobQueue,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory,
            NexoBridgeErrorReporter errorReporter)
        {
            _jobQueue = jobQueue;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _errorReporter = errorReporter;
            _workerLogger = _loggerFactory.CreateLogger<NexoBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[WORKER] Gotowy do pracy. Czekam na zlecenia z API...");

            while (!stoppingToken.IsCancellationRequested)
            {
                ImportJob job = await _jobQueue.DequeueAsync(stoppingToken);
                _workerLogger.LogInformation("Rozpoczynam przetwarzanie paczki: {JobId} ({Count} plików EPP)", job.JobId, job.Files.Count);

                try
                {
                    var progress = new ProgressTracker(
                        (procent, wiadomosc) => WylijPostep(job.JobId, procent, wiadomosc),
                        JobProgressPlan.CalculateTotalUnits(job));

                    // Bezpieczne zamknięcie Sfery w bloku using! Licencja odblokuje się natychmiast po wykonaniu.
                    using (var silnik = new SferaEngine())
                    {
                        var sferaProgress = progress.BeginSegment(JobProgressPlan.SferaStartupUnits);

                        // 1. Zalogowanie poświadczeniami użytkownika z aplikacji
                        silnik.Uruchom(job.Username, job.Password, job.DatabaseName, sferaProgress.ReportSync);
                        await sferaProgress.CompleteAsync("Połączono z bazą i zalogowano do Sfery.");

                        // 2. Tworzymy loggery dla WSZYSTKICH rozbitych serwisów (dodano 3 nowe)
                        var parserLogger = _loggerFactory.CreateLogger<EppParserService>();
                        var manifestLogger = _loggerFactory.CreateLogger<ImportManifestService>();
                        var amLogger = _loggerFactory.CreateLogger<AmortizationService>();
                        var accLogger = _loggerFactory.CreateLogger<AccountingService>();
                        var pitLogger = _loggerFactory.CreateLogger<PitCalculationService>();
                        var vatLogger = _loggerFactory.CreateLogger<VatCalculationService>();
                        var vatUeLogger = _loggerFactory.CreateLogger<VatUeCalculationService>();
                        var attLogger = _loggerFactory.CreateLogger<AttachmentService>();
                        var ksefLogger = _loggerFactory.CreateLogger<KsefNumberAssignmentService>();
                        var duplicateLogger = _loggerFactory.CreateLogger<InvoiceDuplicateDetectionService>();
                        var importLogger = _loggerFactory.CreateLogger<NexoImportService>();

                        // 3. Budujemy nasze serwisy i przekazujemy im świeżo uruchomiony uchwyt Sfery
                        var parserService = new EppParserService(silnik.Sfera, parserLogger);
                        var manifestService = new ImportManifestService(silnik.Sfera, manifestLogger);
                        var amService = new AmortizationService(silnik.Sfera, amLogger);
                        var accService = new AccountingService(silnik.Sfera, accLogger);
                        var pitService = new PitCalculationService(silnik.Sfera, pitLogger);
                        var vatService = new VatCalculationService(silnik.Sfera, vatLogger);
                        var vatUeService = new VatUeCalculationService(silnik.Sfera, vatUeLogger);
                        var attService = new AttachmentService(
                            silnik.Sfera,
                            attLogger,
                            (auditJob, auditProgress) =>
                            {
                                var auditEngine = new SferaEngine();
                                try
                                {
                                    auditEngine.Uruchom(auditJob.Username, auditJob.Password, auditJob.DatabaseName, auditProgress);
                                    return auditEngine;
                                }
                                catch
                                {
                                    auditEngine.Dispose();
                                    throw;
                                }
                            });
                        var ksefService = new KsefNumberAssignmentService(silnik.Sfera, ksefLogger);
                        var duplicateService = new InvoiceDuplicateDetectionService(silnik.Sfera, duplicateLogger);

                        // 4. Budujemy "Dyrygenta", z pełnym, nowym składem orkiestry
                        var serwis = new NexoImportService(
                            parserService,
                            manifestService,
                            amService,
                            accService,
                            pitService,
                            vatService,
                            vatUeService,
                            attService,
                            ksefService,
                            duplicateService,
                            importLogger
                        );

                        // 5. Wywołanie ostatecznego procesu (zapisujemy wynik do zmiennej!)
                        var raportKoncowy = await serwis.PrzetworzZadanieAsync(job, progress);

                        // 6. Wysyłamy gotowy raport JSON na Front-end przez SignalR
                        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                        string jsonReport = JsonSerializer.Serialize(raportKoncowy, jsonOptions);

                        await _hubContext.Clients.Group(job.JobId).SendAsync("ReceiveTaxReport", jsonReport);
                        if (string.Equals(raportKoncowy.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
                        {
                            await _errorReporter.ReportJobFailureAsync(
                                job,
                                "NexoImportService",
                                "Dekretacja / eksport do Nexo",
                                OkreslOperacje(job),
                                raportKoncowy.Message,
                                null,
                                stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _workerLogger.LogError(ex, "[WORKER BŁĄD] Wystąpił błąd podczas przetwarzania zlecenia {JobId}", job.JobId);
                    await WylijPostep(job.JobId, 100, $"BŁĄD: {ex.Message}");
                    await _errorReporter.ReportJobFailureAsync(
                        job,
                        "ImportWorker",
                        "Dekretacja / eksport do Nexo",
                        OkreslOperacje(job),
                        "Błąd krytyczny procesu",
                        ex,
                        stoppingToken);
                }
            }
        }

        private string OkreslOperacje(ImportJob job)
        {
            if (job == null)
            {
                return "unknown";
            }

            if (job.ImportInvoices)
            {
                return "import";
            }

            return "calculation";
        }

        private async Task WylijPostep(string jobId, int procent, string wiadomosc)
        {
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveProgress", procent, wiadomosc, jobId);
        }
    }
}

