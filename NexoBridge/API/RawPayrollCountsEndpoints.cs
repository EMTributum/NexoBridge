using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;

namespace NexoBridge.API
{
    public static class RawPayrollCountsEndpoints
    {
        public static IEndpointRouteBuilder MapRawPayrollCountsEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/jobs/raw-payroll-counts");

            group.MapPost("", async (
                RawPayrollCountsBatchJob job,
                RawPayrollCountsJobQueue queue,
                RawPayrollCountsResultStore resultStore) =>
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
                    "Zlecenie liczenia surowych danych kadrowych {JobId} dodane do kolejki (Okres: {Year}-{Month:00}, Klientów: {ClientCount})",
                    job.JobId, job.PeriodYear, job.PeriodMonth, job.ClientDatabases.Count);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie liczenia surowych danych kadrowych dodane do kolejki."
                });
            });

            group.MapGet("/{jobId}", (string jobId, RawPayrollCountsResultStore resultStore) =>
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

        private static IResult ValidateRequest(RawPayrollCountsBatchJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Username) ||
                string.IsNullOrWhiteSpace(job.Password))
            {
                Log.Warning("Odrzucono zlecenie liczenia surowych danych kadrowych - brak danych logowania.");
                return Results.BadRequest("Brak danych logowania.");
            }

            if (job.PeriodYear <= 0 || job.PeriodMonth is < 1 or > 12)
            {
                Log.Warning("Odrzucono zlecenie liczenia surowych danych kadrowych - niepoprawny okres {Year}-{Month}.", job.PeriodYear, job.PeriodMonth);
                return Results.BadRequest("Niepoprawny rok/miesiąc okresu.");
            }

            return null;
        }

        private static void EnsureJobId(RawPayrollCountsBatchJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }
    }
}
