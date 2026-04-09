using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
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

        public NexoBackgroundWorker(JobQueue jobQueue, IHubContext<ProgressHub> hubContext)
        {
            _jobQueue = jobQueue;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[WORKER] Gotowy do pracy. Czekam na uderzenie z API...");

            while (!stoppingToken.IsCancellationRequested)
            {
                ImportJob job = await _jobQueue.DequeueAsync(stoppingToken);
                Console.WriteLine($"\n[WORKER] Rozpoczynam paczkę {job.JobId} ({job.Files.Count} plików EPP)");

                try
                {
                    await WylijPostep(job.JobId, 10, "Budzenie Sfery... Logowanie użytkownika: " + job.Username);

                    // Bezpieczne zamknięcie Sfery w bloku using! Licencja odblokuje się natychmiast po wykonaniu.
                    using (var silnik = new SferaEngine())
                    {
                        // 1. Zalogowanie poświadczeniami użtykownika z aplikacji
                        silnik.Uruchom(job.Username, job.Password, job.DatabaseName);

                        // 2. Podpięcie usługi importu
                        var serwis = new NexoImportService(silnik.Sfera);

                        // 3. Wywołanie procesu i asynchroniczne wysyłanie komunikatów do SignalR
                        await serwis.PrzetworzZadanieAsync(job, async (procent, wiadomosc) =>
                        {
                            await WylijPostep(job.JobId, procent, wiadomosc);
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[WORKER BŁĄD]: {ex.Message}");
                    Console.ResetColor();
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