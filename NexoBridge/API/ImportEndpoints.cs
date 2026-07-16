using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NexoBridge.API
{
    public static class ImportEndpoints
    {
        public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/jobs/import", async (ImportJob job, JobQueue queue) =>
            {
                if (string.IsNullOrEmpty(job.Username) ||
                    string.IsNullOrEmpty(job.Password) ||
                    string.IsNullOrEmpty(job.DatabaseName))
                {
                    Log.Warning("Odrzucono żądanie JSON - brak wymaganych danych logowania lub nazwy bazy.");
                    return Results.BadRequest("Brak danych logowania lub nazwy bazy.");
                }

                if (job.ImportInvoices && (job.Files == null || job.Files.Count == 0))
                {
                    Log.Warning("Odrzucono żądanie JSON - brak plików EPP przy aktywnej fladze importu.");
                    return Results.BadRequest("Brak plików EPP.");
                }

                if (string.IsNullOrEmpty(job.JobId))
                {
                    job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
                }

                await queue.QueueJobAsync(job);

                string typZlecenia = job.ImportInvoices ? "Import + Kalkulacje" : "Tylko Kalkulacje";
                int ksefCount = job.InvoicesMetadata?.Count(m => !string.IsNullOrWhiteSpace(m.KsefNumber)) ?? 0;
                int ksefCodeCount = job.InvoicesMetadata?.Count(m => !string.IsNullOrWhiteSpace(m.KsefCode)) ?? 0;
                Log.Information("Zlecenie {JobId} dodane do kolejki (Baza: {Database}, Typ: {Typ}, Pliki EPP: {FileCount}, Załączniki PDF: {AttachmentCount}, Numery KSeF: {KsefCount}, Kody KSeF: {KsefCodeCount})",
                    job.JobId, job.DatabaseName, typZlecenia, job.Files?.Count ?? 0, job.Attachments?.Count ?? 0, ksefCount, ksefCodeCount);

                return Results.Accepted(value: new { JobId = job.JobId, Message = "Zlecenie dodane do kolejki." });
            });

            return app;
        }
    }
}
