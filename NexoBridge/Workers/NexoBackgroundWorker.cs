using Microsoft.Extensions.Hosting;
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

        // Wstrzykujemy naszą kolejkę
        public NexoBackgroundWorker(JobQueue jobQueue)
        {
            _jobQueue = jobQueue;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[WORKER] Proces w tle uruchomiony. Czekam na zadania...");

            while (!stoppingToken.IsCancellationRequested)
            {
                // Worker idzie spać, dopóki w kolejce nie pojawi się nowe zadanie
                ImportJob job = await _jobQueue.DequeueAsync(stoppingToken);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[WORKER] Wyciągnąłem z kolejki zadanie: {job.JobId}");
                Console.WriteLine($"[WORKER] Użytkownik: {job.Username}, Przesłanych plików EPP: {job.Files.Count}");

                // Symulacja ciężkiej pracy Sfery (czekamy 5 sekund)
                Console.WriteLine("[WORKER] Logowanie do Sfery i przetwarzanie...");
                await Task.Delay(3000, stoppingToken);

                Console.WriteLine($"[WORKER] Zakończono dekretację dla zadania {job.JobId}!");
                Console.ResetColor();
            }
        }
    }
}