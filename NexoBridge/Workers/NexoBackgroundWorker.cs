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
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
        private const string JobTimeoutEnvName = "NEXO_IMPORT_JOB_TIMEOUT_MINUTES";
        private const string ExitOnTimeoutEnvName = "NEXO_IMPORT_JOB_TIMEOUT_EXIT";
        private const int DefaultJobTimeoutMinutes = 120;
        private readonly TimeSpan _jobTimeout;
        private readonly bool _exitOnTimeout;
        private static readonly JsonSerializerOptions ReportJsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly JobQueue _jobQueue;
        private readonly IHubContext<ProgressHub> _hubContext;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<NexoBackgroundWorker> _workerLogger;
        private readonly NexoBridgeErrorReporter _errorReporter;
        private readonly PoczekalniaBaselineStore _baselineStore;
        public NexoBackgroundWorker(
            JobQueue jobQueue,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory,
            NexoBridgeErrorReporter errorReporter,
            PoczekalniaBaselineStore baselineStore)
        {
            _jobQueue = jobQueue;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
            _errorReporter = errorReporter;
            _baselineStore = baselineStore;
            _workerLogger = _loggerFactory.CreateLogger<NexoBackgroundWorker>();
            _jobTimeout = TimeSpan.FromMinutes(OdczytajLiczbeZEnv(JobTimeoutEnvName, DefaultJobTimeoutMinutes));
            _exitOnTimeout = string.Equals(Environment.GetEnvironmentVariable(ExitOnTimeoutEnvName), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int OdczytajLiczbeZEnv(string nazwa, int domyslna)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(nazwa), out int wartosc) && wartosc > 0 ? wartosc : domyslna;
        }

        // Wywołań Sfery nie da się bezpiecznie przerwać ani zwolnić bramki pod żywym wywołaniem (statyczny stan
        // SDK), więc po przekroczeniu limitu: raport FAILED do klienta + alert do Klasyfikatora, a opcjonalnie
        // (NEXO_IMPORT_JOB_TIMEOUT_EXIT=true) zakończenie procesu z kodem 1, żeby usługa Windows z ustawionym
        // "restart on failure" wystartowała od nowa i zwolniła zablokowaną bramkę dla pozostałych workerów.
        private async Task ObsluzPrzekroczenieCzasuAsync(ImportJob job, ProgressTracker progress, CancellationToken stoppingToken)
        {
            string komunikat = $"Przekroczono limit czasu zadania ({_jobTimeout.TotalMinutes:0} min). Ostatni etap: {progress.LastMessage ?? "brak"} ({progress.LastPercent}%).";
            _workerLogger.LogError("[WORKER TIMEOUT] Zadanie {JobId}: {Komunikat}", job.JobId, komunikat);

            await BezpiecznieAsync(() => WylijPostep(job.JobId, 100, $"BŁĄD: {komunikat}"));
            await BezpiecznieAsync(() => WyslijRaportAsync(job.JobId, new TaxSummaryReport
            {
                JobId = job.JobId,
                Status = "FAILED",
                Message = $"{komunikat} Sprawdź stan dokumentów w Nexo przed ponowieniem eksportu."
            }));
            await BezpiecznieAsync(() => _errorReporter.ReportJobFailureAsync(
                job,
                "ImportWorker",
                "Dekretacja / eksport do Nexo",
                OkreslOperacje(job),
                komunikat,
                null,
                stoppingToken));

            if (_exitOnTimeout)
            {
                _workerLogger.LogCritical("[WORKER TIMEOUT] Zadanie {JobId} zablokowało Sferę - kończę proces (kod 1), żeby usługa uruchomiła się ponownie.", job.JobId);
                Serilog.Log.CloseAndFlush();
                Environment.Exit(1);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerLogger.LogInformation("[WORKER] Gotowy do pracy. Czekam na zlecenia z API...");

            while (!stoppingToken.IsCancellationRequested)
            {
                ImportJob job = await _jobQueue.DequeueAsync(stoppingToken);
                JobHeartbeat heartbeat = null;

                try
                {
                    _workerLogger.LogInformation("Rozpoczynam przetwarzanie paczki: {JobId} ({Count} plików EPP)", job.JobId, job.Files?.Count ?? 0);

                    var progress = new ProgressTracker(
                        (procent, wiadomosc) => WylijPostep(job.JobId, procent, wiadomosc),
                        JobProgressPlan.CalculateTotalUnits(job));

                    // Heartbeat: klient (nexo_bridge_listener) traktuje zadanie bez zdarzeń przez kilka minut
                    // jako martwe. Import EPP i dekretacja to pojedyncze, długie wywołania Sfery, a bramkę może
                    // trzymać inny worker (billing, RCP) - bez heartbeatu żywe zadanie wyglądałoby na zawieszone.
                    heartbeat = new JobHeartbeat(
                        () => WylijPostep(
                            job.JobId,
                            progress.LastPercent,
                            progress.LastMessage ?? "Czekam na dostęp do Sfery (inne zadanie jest w trakcie)..."),
                        HeartbeatInterval,
                        _workerLogger,
                        stoppingToken);

                    // Bezpieczne zamknięcie Sfery w bloku using! Licencja odblokuje się natychmiast po wykonaniu.
                    // SferaSessionGate: bez tego dwie sesje logujące się w tym samym momencie z różnych
                    // workerów (np. import faktury + enumeracja backfillu) potrafią zderzyć się wewnątrz
                    // statycznego stanu SDK Sfery (MenedzerPolaczen.Polacz -> LicenceController) i rzucić
                    // różne generyczne wyjątki (InvalidOperationException, IndexOutOfRangeException, "Brak
                    // zalogowanego operatora") - patrz SferaSessionGate.cs. Pozostałe workery (BillingClients,
                    // PayrollCounts, DuplicateScan, ...) już to robią; ten, najstarszy, dotąd nie trzymał
                    // bramki wcale.
                    using (await SferaSessionGate.AcquireAsync(stoppingToken))
                    using (var silnik = new SferaEngine())
                    using (new JobWatchdog(_jobTimeout, () => ObsluzPrzekroczenieCzasuAsync(job, progress, stoppingToken), _workerLogger))
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
                        var vatStatusLogger = _loggerFactory.CreateLogger<VatStatusVerificationService>();
                        var importLogger = _loggerFactory.CreateLogger<NexoImportService>();

                        // 3. Budujemy nasze serwisy i przekazujemy im świeżo uruchomiony uchwyt Sfery
                        var parserService = new EppParserService(silnik.Sfera, parserLogger);
                        var manifestService = new ImportManifestService(silnik.Sfera, _baselineStore, manifestLogger);
                        var amService = new AmortizationService(silnik.Sfera, amLogger);
                        var accService = new AccountingService(silnik.Sfera, _baselineStore, _errorReporter, accLogger);
                        var pitService = new PitCalculationService(silnik.Sfera, pitLogger);
                        var vatService = new VatCalculationService(silnik.Sfera, vatLogger);
                        var vatUeService = new VatUeCalculationService(silnik.Sfera, vatUeLogger);
                        var attService = new AttachmentService(
                            silnik.Sfera,
                            attLogger,
                            // CELOWO bez SferaSessionGate: ta fabryka jest wołana z WNĘTRZA bloku `using`
                            // powyżej, który już trzyma bramkę (SemaphoreSlim(1,1), niereentrantny) - druga
                            // próba jej zajęcia tutaj zablokowałaby się na zawsze (bramka zwalnia się dopiero
                            // po zamknięciu zewnętrznego `silnik`, a to nigdy nie nastąpi, bo czekamy właśnie
                            // na tę fabrykę). Ryzyko kolizji jest tu mniejsze niż przy otwieraniu głównej sesji:
                            // zewnętrzny `silnik` w tym momencie żyje, ale nic nim aktywnie nie woła, więc nie
                            // toczy się jego własny Polacz() - kolizje obserwowaliśmy dotąd wyłącznie między
                            // dwoma RÓWNOLEGŁYMI wywołaniami Polacz() (logowaniami), nie między żywą-ale-bezczynną
                            // sesją a nową.
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
                        var vatStatusService = new VatStatusVerificationService(silnik.Sfera, vatStatusLogger);

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
                            vatStatusService,
                            importLogger
                        );

                        // 5. Wywołanie ostatecznego procesu (zapisujemy wynik do zmiennej!)
                        var raportKoncowy = await serwis.PrzetworzZadanieAsync(job, progress);

                        // 6. Wysyłamy gotowy raport JSON na Front-end przez SignalR
                        await heartbeat.DisposeAsync();
                        await WyslijRaportAsync(job.JobId, raportKoncowy);
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
                    if (heartbeat != null)
                    {
                        await heartbeat.DisposeAsync();
                    }

                    // Zawsze wysyłamy raport końcowy - bez niego klient nie wie, że zadanie się skończyło
                    // i czeka do timeoutu. Sam ReceiveProgress("BŁĄD") nie zamyka zadania po stronie klienta.
                    await BezpiecznieAsync(() => WylijPostep(job.JobId, 100, $"BŁĄD: {ex.Message}"));
                    await BezpiecznieAsync(() => WyslijRaportAsync(job.JobId, new TaxSummaryReport
                    {
                        JobId = job.JobId,
                        Status = "FAILED",
                        Message = $"Błąd krytyczny procesu: {ex.Message}"
                    }));
                    await _errorReporter.ReportJobFailureAsync(
                        job,
                        "ImportWorker",
                        "Dekretacja / eksport do Nexo",
                        OkreslOperacje(job),
                        "Błąd krytyczny procesu",
                        ex,
                        stoppingToken);
                }
                finally
                {
                    if (heartbeat != null)
                    {
                        await heartbeat.DisposeAsync();
                    }
                }
            }
        }

        private async Task WyslijRaportAsync(string jobId, TaxSummaryReport raport)
        {
            string jsonReport = JsonSerializer.Serialize(raport, ReportJsonOptions);
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveTaxReport", jsonReport);
        }

        private async Task BezpiecznieAsync(Func<Task> akcja)
        {
            try
            {
                await akcja();
            }
            catch (Exception ex)
            {
                _workerLogger.LogWarning(ex, "[WORKER] Nie udało się wysłać zdarzenia SignalR.");
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

        /// <summary>
        /// Jednorazowo wywołuje akcję, jeśli zadanie nie skończy się w zadanym czasie (Dispose anuluje).
        /// </summary>
        private sealed class JobWatchdog : IDisposable
        {
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();

            public JobWatchdog(TimeSpan timeout, Func<Task> onTimeout, ILogger logger)
            {
                CancellationToken token = _cts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(timeout, token);
                        await onTimeout();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[WORKER TIMEOUT] Błąd obsługi przekroczenia czasu zadania.");
                    }
                });
            }

            public void Dispose()
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }

        /// <summary>
        /// Co zadany interwał ponawia ostatni postęp zadania, dopóki nie zostanie zatrzymany.
        /// Nic nie zapisuje - to tylko sygnał "żyję" dla klienta.
        /// </summary>
        private sealed class JobHeartbeat : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cts;
            private readonly Task _loop;
            private int _disposed;

            public JobHeartbeat(Func<Task> beat, TimeSpan interval, ILogger logger, CancellationToken stoppingToken)
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _loop = RunAsync(beat, interval, logger, _cts.Token);
            }

            private static async Task RunAsync(Func<Task> beat, TimeSpan interval, ILogger logger, CancellationToken token)
            {
                using var timer = new PeriodicTimer(interval);
                try
                {
                    while (await timer.WaitForNextTickAsync(token))
                    {
                        try
                        {
                            await beat();
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "[WORKER] Nie udało się wysłać heartbeatu zadania.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                _cts.Cancel();
                await _loop;
                _cts.Dispose();
            }
        }
    }
}

