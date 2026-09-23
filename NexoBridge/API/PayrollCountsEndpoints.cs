using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;

namespace NexoBridge.API
{
    public static class PayrollCountsEndpoints
    {
        public static IEndpointRouteBuilder MapPayrollCountsEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/jobs/payroll-counts");

            group.MapPost("", async (
                PayrollCountsBatchJob job,
                PayrollCountsJobQueue queue,
                PayrollCountsResultStore resultStore) =>
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
                    "Zlecenie liczenia pozycji kadrowo-płacowych {JobId} dodane do kolejki (Baza biura: {Database}, Okres: {Year}-{Month:00}, Klientów: {ClientCount})",
                    job.JobId, job.OfficeDatabaseName, job.PeriodYear, job.PeriodMonth, job.ClientDatabases.Count);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie liczenia pozycji kadrowo-płacowych dodane do kolejki."
                });
            });

            group.MapGet("/{jobId}", (string jobId, PayrollCountsResultStore resultStore) =>
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

        private static IResult ValidateRequest(PayrollCountsBatchJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Username) ||
                string.IsNullOrWhiteSpace(job.Password) ||
                string.IsNullOrWhiteSpace(job.OfficeDatabaseName))
            {
                Log.Warning("Odrzucono zlecenie liczenia pozycji kadrowo-płacowych - brak danych logowania albo nazwy bazy biura.");
                return Results.BadRequest("Brak danych logowania albo nazwy bazy biura.");
            }

            if (job.PeriodYear <= 0 || job.PeriodMonth is < 1 or > 12)
            {
                Log.Warning("Odrzucono zlecenie liczenia pozycji kadrowo-płacowych - niepoprawny okres {Year}-{Month}.", job.PeriodYear, job.PeriodMonth);
                return Results.BadRequest("Niepoprawny rok/miesiąc okresu.");
            }

            return null;
        }

        private static void EnsureJobId(PayrollCountsBatchJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }
    }
}
