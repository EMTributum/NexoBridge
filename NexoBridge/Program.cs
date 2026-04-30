using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using DotNetEnv;
using NexoBridge.Models;
using NexoBridge.Services;
using NexoBridge.Workers;
using NexoBridge.Hubs;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace NexoBridge
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Env.Load();

            // =====================================================================
            // 1. INICJALIZACJA SERILOGA NA SAMYM POCZĄTKU
            // =====================================================================
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Wycisza spam z ASP.NET
                .Enrich.FromLogContext()
                // Format dla konsoli (kolorowy, czytelny)
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                // Format dla pliku tekstowego (nowy plik codziennie)
                .WriteTo.File(
                    path: "Logs/nexobridge-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();

            try
            {
                Log.Information("Uruchamianie mikroserwisu NexoBridge...");

                var builder = WebApplication.CreateBuilder(args);

                // =====================================================================
                // 2. PODPIĘCIE SERILOGA DO HOSTA APLIKACJI
                // =====================================================================
                builder.Host.UseSerilog();

                // 3. Rejestrujemy aplikację jako oficjalną Usługę Windows
                builder.Services.AddWindowsService(options =>
                {
                    options.ServiceName = "NexoBridgeService";
                });

                // 4. Twardy CORS - wpuszczamy TYLKO Twoją aplikację z VM 12
                string allowedOrigin = Environment.GetEnvironmentVariable("ALLOWED_ORIGIN");
                builder.Services.AddCors(options =>
                {
                    options.AddPolicy("StrictPolicy", policy => {
                        policy.WithOrigins(allowedOrigin)
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials(); // Wymagane dla SignalR
                    });
                });

                builder.Services.AddSignalR();
                builder.Services.AddSingleton<JobQueue>();
                builder.Services.AddHostedService<NexoBackgroundWorker>();

                var app = builder.Build();

                // Używamy nowej, rygorystycznej polityki
                app.UseCors("StrictPolicy");

                // Rejestrujemy trasę dla naszego Huba
                app.MapHub<ProgressHub>("/progressHub");

                app.MapGet("/ping", () => Results.Ok(new { Status = "Online" }));

                // =====================================================================
                // NASZ ENDPOINT
                // =====================================================================
                app.MapPost("/api/jobs/import", async (ImportJob job, JobQueue queue) =>
                {
                    // 1. Walidacja
                    if (string.IsNullOrEmpty(job.Username) ||
                        string.IsNullOrEmpty(job.Password) ||
                        string.IsNullOrEmpty(job.DatabaseName))
                    {
                        Log.Warning("Odrzucono żądanie JSON - brak wymaganych danych logowania lub nazwy bazy.");
                        return Results.BadRequest("Brak danych logowania lub nazwy bazy.");
                    }

                    if (job.Files == null || job.Files.Count == 0)
                    {
                        Log.Warning("Odrzucono żądanie JSON - brak plików EPP w paczce.");
                        return Results.BadRequest("Brak plików EPP.");
                    }

                    // 2. Generujemy unikalne ID zadania (jeśli Python nam nie przysłał)
                    if (string.IsNullOrEmpty(job.JobId))
                    {
                        job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    }

                    // 3. Wrzucamy do kolejki
                    await queue.QueueJobAsync(job);

                    Log.Information("Zlecenie {JobId} dodane do kolejki (Baza: {Database}, Pliki EPP: {FileCount}, Załączniki PDF: {AttachmentCount})",
                        job.JobId, job.DatabaseName, job.Files.Count, job.Attachments?.Count ?? 0);

                    return Results.Accepted(value: new { JobId = job.JobId, Message = "Zlecenie dodane do kolejki." });
                });

                Log.Information("NexoBridge nasłuchuje na porcie 5000...");
                app.Run("http://0.0.0.0:5000");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Aplikacja zakończyła działanie z powodu krytycznego błędu");
            }
            finally
            {
                // Zapewnia zrzucenie ostatnich logów z pamięci do pliku przed zamknięciem
                Log.CloseAndFlush();
            }
        }
    }
}