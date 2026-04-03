using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using DotNetEnv;
using NexoBridge.Models;
using NexoBridge.Services;
using NexoBridge.Workers;

namespace NexoBridge
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Env.Load();
            var builder = WebApplication.CreateBuilder(args);

            // REJESTRACJA SERWISÓW (Dependency Injection)
            builder.Services.AddSingleton<JobQueue>();             // Kolejka jest jedna (Singleton) dla całego serwera
            builder.Services.AddHostedService<NexoBackgroundWorker>(); // Uruchamiamy Workera jako proces w tle

            var app = builder.Build();

            app.MapGet("/ping", () => Results.Ok(new { Status = "Online" }));

            // Nasz zaktualizowany Endpoint API
            app.MapPost("/api/jobs/import", async (HttpRequest request, JobQueue queue) =>
            {
                if (!request.HasFormContentType) return Results.BadRequest("Oczekiwano formularza multipart/form-data.");

                var form = await request.ReadFormAsync();
                var username = form["Username"].ToString();
                var password = form["Password"].ToString();
                var files = form.Files;

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || files.Count == 0)
                    return Results.BadRequest("Brak danych logowania lub plików EPP.");

                // Przygotowujemy "pudełko" na zadanie
                var job = new ImportJob
                {
                    JobId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Username = username,
                    Password = password
                };

                long totalSize = 0;

                // PRZEPISANIE PLIKÓW DO RAM: Pliki z requestu wyparują za ułamek sekundy. 
                // Musimy je skopiować do stałej pamięci (byte[]), żeby Worker mógł je potem odczytać.
                foreach (var file in files)
                {
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    job.Files.Add(new EppFilePayload { FileName = file.FileName, Content = ms.ToArray() });
                    totalSize += file.Length;
                }

                // Wrzucamy gotowe pudło do Kolejki
                await queue.QueueJobAsync(job);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = $"Zakolejkowano do przetworzenia {files.Count} plików EPP.",
                    TotalBytes = totalSize
                });
            });

            Console.WriteLine("Uruchamianie mikroserwisu NexoBridge...");
            app.Run("http://localhost:5000");
        }
    }
}