using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;

namespace NexoBridge.API
{
    public static class DuplicateScanEndpoints
    {
        public static IEndpointRouteBuilder MapDuplicateScanEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/jobs/duplicate-scan");

            group.MapPost("", async (
                YearlyDuplicateScanJob job,
                DuplicateScanJobQueue queue,
                DuplicateScanResultStore resultStore) =>
            {
                var validationError = ValidateRequest(job);
                if (validationError != null)
                {
                    return validationError;
                }

                EnsureJobId(job);
                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie rocznego skanu duplikatów {JobId} dodane do kolejki (Baza: {Database}, Rok: {Year})",
                    job.JobId, job.DatabaseName, job.Year);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie rocznego skanu duplikatów dodane do kolejki."
                });
            });

            group.MapGet("/{jobId}", (string jobId, DuplicateScanResultStore resultStore) =>
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

        private static IResult ValidateRequest(YearlyDuplicateScanJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Username) ||
                string.IsNullOrWhiteSpace(job.Password) ||
                string.IsNullOrWhiteSpace(job.DatabaseName))
            {
                Log.Warning("Odrzucono zlecenie rocznego skanu duplikatów - brak danych logowania albo nazwy bazy.");
                return Results.BadRequest("Brak danych logowania albo nazwy bazy.");
            }

            if (job.Year < 2000 || job.Year > 2100)
            {
                Log.Warning("Odrzucono zlecenie rocznego skanu duplikatów - niepoprawny rok {Year}.", job.Year);
                return Results.BadRequest("Niepoprawny rok.");
            }

            return null;
        }

        private static void EnsureJobId(YearlyDuplicateScanJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }
    }
}
