using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Hubs;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    public class NexoBackgroundWorker : BackgroundService
    {
        private readonly JobQueue _jobQueue;
        private readonly IHubContext<ProgressHub> _hubContext;

        // ZMIANA: Używamy Fabryki Loggerów, żeby nie wstrzykiwać 5 różnych loggerów do konstruktora
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<NexoBackgroundWorker> _workerLogger;

        public NexoBackgroundWorker(
            JobQueue jobQueue,
            IHubContext<ProgressHub> hubContext,
            ILoggerFactory loggerFactory)
        {
            _jobQueue = jobQueue;
            _hubContext = hubContext;
            _loggerFactory = loggerFactory;
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
                    await WylijPostep(job.JobId, 10, "Budzenie Sfery... Logowanie użytkownika: " + job.Username);

                    // Bezpieczne zamknięcie Sfery w bloku using! Licencja odblokuje się natychmiast po wykonaniu.
                    using (var silnik = new SferaEngine())
                    {
                        // 1. Zalogowanie poświadczeniami użytkownika z aplikacji
                        silnik.Uruchom(job.Username, job.Password, job.DatabaseName);

                        // 2. Tworzymy loggery dla naszych rozbitych serwisów
                        var parserLogger = _loggerFactory.CreateLogger<EppParserService>();
                        var accLogger = _loggerFactory.CreateLogger<AccountingService>();
                        var attLogger = _loggerFactory.CreateLogger<AttachmentService>();
                        var importLogger = _loggerFactory.CreateLogger<NexoImportService>();

                        // 3. Budujemy nasze serwisy i przekazujemy im świeżo uruchomiony uchwyt Sfery
                        var parserService = new EppParserService(silnik.Sfera, parserLogger);
                        var accService = new AccountingService(silnik.Sfera, accLogger);
                        var attService = new AttachmentService(silnik.Sfera, attLogger);

                        // 4. Budujemy "Dyrygenta", który zepnie to wszystko w całość
                        var serwis = new NexoImportService(parserService, accService, attService, importLogger);

                        // 5. Wywołanie ostatecznego procesu
                        await serwis.PrzetworzZadanieAsync(job, async (procent, wiadomosc) =>
                        {
                            await WylijPostep(job.JobId, procent, wiadomosc);
                        });
                    }
                }
                catch (Exception ex)
                {
                    _workerLogger.LogError(ex, "[WORKER BŁĄD] Wystąpił błąd podczas przetwarzania zlecenia {JobId}", job.JobId);
                    await WylijPostep(job.JobId, 100, $"BŁĄD: {ex.Message}");
                }
            }
        }

        private async Task WylijPostep(string jobId, int procent, string wiadomosc)
        {
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveProgress", procent, wiadomosc);
        }
    }
}