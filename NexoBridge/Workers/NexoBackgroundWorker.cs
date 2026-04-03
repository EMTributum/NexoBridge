using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using NexoBridge.Hubs;
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
        private readonly IHubContext<ProgressHub> _hubContext; // Dodajemy Huba

        public NexoBackgroundWorker(JobQueue jobQueue, IHubContext<ProgressHub> hubContext)
        {
            _jobQueue = jobQueue;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[WORKER] Proces w tle uruchomiony. Czekam na zadania...");

            while (!stoppingToken.IsCancellationRequested)
            {
                ImportJob job = await _jobQueue.DequeueAsync(stoppingToken);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[WORKER] Przetwarzam zadanie: {job.JobId}");
                Console.ResetColor();

                // Raport 1: Start
                await WylijPostep(job.JobId, 10, "Uruchamianie silnika Sfery i weryfikacja licencji...");
                await Task.Delay(2000, stoppingToken); // Symulacja

                // Raport 2: Przetwarzanie EPP
                await WylijPostep(job.JobId, 40, $"Łączenie plików EPP i synchronizacja słowników...");
                await Task.Delay(2000, stoppingToken); // Symulacja

                // Raport 3: Dekretacja
                await WylijPostep(job.JobId, 80, "Sędzia analizuje schematy dekretacji...");
                await Task.Delay(2000, stoppingToken); // Symulacja

                // Raport 4: Koniec
                await WylijPostep(job.JobId, 100, $"Zakończono sukcesem! Przetworzono dokumentów: {job.Files.Count}.");
            }
        }

        private async Task WylijPostep(string jobId, int procent, string wiadomosc)
        {
            // Wysyłamy wiadomość TYLKO do grupy o nazwie JobId
            await _hubContext.Clients.Group(jobId).SendAsync("ReceiveProgress", procent, wiadomosc);
        }
    }
}