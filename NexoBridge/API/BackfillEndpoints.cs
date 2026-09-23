using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;

namespace NexoBridge.API
{
    public static class BackfillEndpoints
    {
        public static IEndpointRouteBuilder MapBackfillEndpoints(this IEndpointRouteBuilder app)
        {
            var enumerateGroup = app.MapGroup("/api/jobs/backfill-enumerate");

            enumerateGroup.MapPost("", async (
                BackfillEnumerateJob job,
                BackfillEnumerateJobQueue queue,
                BackfillEnumerateResultStore resultStore) =>
            {
                if (string.IsNullOrWhiteSpace(job.Username) ||
                    string.IsNullOrWhiteSpace(job.Password) ||
                    job.Clients == null ||
                    job.Clients.Count == 0)
                {
                    Log.Warning("Odrzucono zlecenie enumeracji backfillu - brak danych logowania albo listy klientów.");
                    return Results.BadRequest("Brak danych logowania albo listy klientów.");
                }

                if (job.Year <= 0)
                {
                    Log.Warning("Odrzucono zlecenie enumeracji backfillu - nieprawidłowy rok {Year}.", job.Year);
                    return Results.BadRequest("Nieprawidłowy rok.");
                }

                EnsureJobId(job);
                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie enumeracji backfillu {JobId} dodane do kolejki (klienci={Count}, rok={Year})",
                    job.JobId, job.Clients.Count, job.Year);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie enumeracji backfillu dodane do kolejki."
                });
            });

            enumerateGroup.MapGet("/{jobId}", (string jobId, BackfillEnumerateResultStore resultStore) =>
            {
                if (resultStore.TryGet(jobId, out var report))
                {
                    return Results.Ok(report);
                }

                if (resultStore.IsPending(jobId))
                {
                    resultStore.TryGetProgress(jobId, out int percent, out string message);
                    return Results.Accepted(value: new
                    {
                        JobId = jobId,
                        Status = "RUNNING",
                        Percent = percent,
                        Message = message ?? "Zlecenie jest nadal przetwarzane."
                    });
                }

                return Results.NotFound(new { JobId = jobId, Message = "Nie znaleziono zlecenia." });
            });

            var writeCommentsGroup = app.MapGroup("/api/jobs/backfill-write-comments");

            writeCommentsGroup.MapPost("", async (
                BackfillWriteCommentsJob job,
                BackfillWriteCommentsJobQueue queue,
                BackfillWriteCommentsResultStore resultStore) =>
            {
                if (string.IsNullOrWhiteSpace(job.Username) ||
                    string.IsNullOrWhiteSpace(job.Password) ||
                    job.Rows == null ||
                    job.Rows.Count == 0)
                {
                    Log.Warning("Odrzucono zlecenie zapisu komentarzy backfillu - brak danych logowania albo wierszy.");
                    return Results.BadRequest("Brak danych logowania albo wierszy do zapisania.");
                }

                EnsureJobId(job);
                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie zapisu komentarzy backfillu {JobId} dodane do kolejki (wiersze={Count})",
                    job.JobId, job.Rows.Count);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie zapisu komentarzy backfillu dodane do kolejki."
                });
            });

            writeCommentsGroup.MapGet("/{jobId}", (string jobId, BackfillWriteCommentsResultStore resultStore) =>
            {
                if (resultStore.TryGet(jobId, out var report))
                {
                    return Results.Ok(report);
                }

                if (resultStore.IsPending(jobId))
                {
                    resultStore.TryGetProgress(jobId, out int percent, out string message);
                    return Results.Accepted(value: new
                    {
                        JobId = jobId,
                        Status = "RUNNING",
                        Percent = percent,
                        Message = message ?? "Zlecenie jest nadal przetwarzane."
                    });
                }

                return Results.NotFound(new { JobId = jobId, Message = "Nie znaleziono zlecenia." });
            });

            return app;
        }

        private static void EnsureJobId(BackfillEnumerateJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }

        private static void EnsureJobId(BackfillWriteCommentsJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }
    }
}
