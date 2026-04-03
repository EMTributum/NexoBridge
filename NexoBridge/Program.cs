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

namespace NexoBridge
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Env.Load();
            var builder = WebApplication.CreateBuilder(args);

            // 1. Rejestrujemy aplikację jako oficjalną Usługę Windows
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "NexoBridgeService";
            });

            // 2. Twardy CORS - wpuszczamy TYLKO Twoją aplikację z VM 12
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

            // Nasz endpoint (kod zostaje ten sam co poprzednio!)
            app.MapPost("/api/jobs/import", async (HttpRequest request, JobQueue queue) =>
            {
                if (!request.HasFormContentType) return Results.BadRequest("Oczekiwano formularza multipart/form-data.");

                var form = await request.ReadFormAsync();
                var username = form["Username"].ToString();
                var password = form["Password"].ToString();
                var files = form.Files;

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || files.Count == 0)
                    return Results.BadRequest("Brak danych logowania lub plików.");

                var job = new ImportJob { JobId = Guid.NewGuid().ToString("N").Substring(0, 8), Username = username, Password = password };

                foreach (var file in files)
                {
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    job.Files.Add(new EppFilePayload { FileName = file.FileName, Content = ms.ToArray() });
                }

                await queue.QueueJobAsync(job);

                return Results.Accepted(value: new { JobId = job.JobId, Message = "Zlecenie dodane do kolejki." });
            });

            Console.WriteLine("Uruchamianie mikroserwisu NexoBridge...");
            app.Run("http://localhost:5000");
        }
    }
}