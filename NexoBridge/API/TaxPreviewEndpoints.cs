using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;
using System.Linq;

namespace NexoBridge.API
{
    public static class TaxPreviewEndpoints
    {
        public static IEndpointRouteBuilder MapTaxPreviewEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/jobs/tax-preview");

            group.MapPost("", async (
                TaxPreviewBatchJob job,
                TaxPreviewJobQueue queue,
                TaxPreviewResultStore resultStore) =>
            {
                var validationError = ValidateRequest(job);
                if (validationError != null)
                {
                    return validationError;
                }

                EnsureJobId(job);
                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information(
                    "Zlecenie podglądu podatków {JobId} dodane do kolejki (Klientów: {ClientCount}, okresów: {PeriodCount}, NotAfterUtc: {NotAfterUtc})",
                    job.JobId, job.Clients.Count, job.Clients.Sum(c => c.Periods.Count), job.NotAfterUtc);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie podglądu podatków dodane do kolejki."
                });
            });

            group.MapGet("/{jobId}", (string jobId, TaxPreviewResultStore resultStore) =>
            {
                if (resultStore.TryGet(jobId, out var report))
                {
                    return Results.Ok(report);
                }

                if (resultStore.IsPending(jobId))
                {
                    return Results.Accepted(value: new { JobId = jobId, Message = "Zlecenie jest nadal przetwarzane." });
                }

                return Results.NotFound(new { JobId = jobId, Message = "Nie znaleziono zlecenia." });
            });

            return app;
        }

        private static IResult ValidateRequest(TaxPreviewBatchJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Username) ||
                string.IsNullOrWhiteSpace(job.Password))
            {
                Log.Warning("Odrzucono zlecenie podglądu podatków - brak danych logowania.");
                return Results.BadRequest("Brak danych logowania.");
            }

            if (job.Clients == null || job.Clients.Count == 0)
            {
                Log.Warning("Odrzucono zlecenie podglądu podatków - brak klientów.");
                return Results.BadRequest("Brak klientów do policzenia.");
            }

            bool niepoprawnyOkres = job.Clients
                .SelectMany(c => c.Periods ?? new System.Collections.Generic.List<TaxPreviewPeriodRequest>())
                .Any(p => p.Year < 2000 || p.Month is < 1 or > 12);
            if (niepoprawnyOkres)
            {
                Log.Warning("Odrzucono zlecenie podglądu podatków - niepoprawny okres.");
                return Results.BadRequest("Niepoprawny rok/miesiąc okresu.");
            }

            return null;
        }

        private static void EnsureJobId(TaxPreviewBatchJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }
    }
}
