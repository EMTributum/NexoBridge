using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;

namespace NexoBridge.API
{
    public static class ZusOwnerContributionEndpoints
    {
        public static IEndpointRouteBuilder MapZusOwnerContributionEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/jobs/zus-owner-contributions");

            group.MapPost("", async (
                ZusOwnerContributionBatchJob job,
                ZusOwnerContributionJobQueue queue,
                ZusOwnerContributionResultStore resultStore) =>
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
                    "Zlecenie odczytu ZUS-u właściciela {JobId} dodane do kolejki (Okres: {Year}-{Month:00}, Klientów: {ClientCount})",
                    job.JobId, job.PeriodYear, job.PeriodMonth, job.ClientDatabases.Count);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie odczytu ZUS-u właściciela dodane do kolejki."
                });
            });

            group.MapGet("/{jobId}", (string jobId, ZusOwnerContributionResultStore resultStore) =>
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

        private static IResult ValidateRequest(ZusOwnerContributionBatchJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Username) ||
                string.IsNullOrWhiteSpace(job.Password))
            {
                Log.Warning("Odrzucono zlecenie odczytu ZUS-u właściciela - brak danych logowania.");
                return Results.BadRequest("Brak danych logowania.");
            }

            if (job.PeriodYear <= 0 || job.PeriodMonth is < 1 or > 12)
            {
                Log.Warning("Odrzucono zlecenie odczytu ZUS-u właściciela - niepoprawny okres {Year}-{Month}.", job.PeriodYear, job.PeriodMonth);
                return Results.BadRequest("Niepoprawny rok/miesiąc okresu.");
            }

            return null;
        }

        private static void EnsureJobId(ZusOwnerContributionBatchJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }
    }
}
