using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;
using System.Threading.Tasks;

namespace NexoBridge.API
{
    public static class OfficeVatFlagsEndpoints
    {
        public static IEndpointRouteBuilder MapOfficeVatFlagsEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/jobs/office-vat-flags", async (
                OfficeVatFlagsJob job,
                OfficeVatFlagsJobQueue queue,
                OfficeVatFlagsResultStore resultStore) =>
            {
                var validationError = ValidateBaseRequest(job);
                if (validationError != null)
                {
                    return validationError;
                }

                job.Nip = null;
                EnsureJobId(job);

                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie odczytu flag VAT/VAT-UE dla calego Biura {JobId} dodane do kolejki (Baza: {Database})",
                    job.JobId, job.OfficeDatabaseName);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie odczytu flag VAT/VAT-UE dla klientow Biura dodane do kolejki."
                });
            });

            app.MapPost("/api/jobs/office-vat-flags/by-nip", async (
                OfficeVatFlagsJob job,
                OfficeVatFlagsJobQueue queue,
                OfficeVatFlagsResultStore resultStore) =>
            {
                var validationError = ValidateBaseRequest(job);
                if (validationError != null)
                {
                    return validationError;
                }

                if (string.IsNullOrWhiteSpace(job.Nip))
                {
                    Log.Warning("Odrzucono zlecenie odczytu flag VAT/VAT-UE po NIP - brak NIP.");
                    return Results.BadRequest("Brak NIP.");
                }

                EnsureJobId(job);

                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie odczytu flag VAT/VAT-UE po NIP {JobId} dodane do kolejki (Baza: {Database}, NIP: {Nip})",
                    job.JobId, job.OfficeDatabaseName, job.Nip);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie odczytu flag VAT/VAT-UE po NIP dodane do kolejki."
                });
            });

            app.MapPost("/api/jobs/office-database-names", async (
                OfficeVatFlagsJob job,
                OfficeVatFlagsJobQueue queue,
                OfficeVatFlagsResultStore resultStore) =>
            {
                var validationError = ValidateBaseRequest(job);
                if (validationError != null)
                {
                    return validationError;
                }

                job.Nip = null;
                job.SyncDatabaseNamesOnly = true;
                EnsureJobId(job);

                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie synchronizacji nazw baz danych klientow Biura {JobId} dodane do kolejki (Baza: {Database})",
                    job.JobId, job.OfficeDatabaseName);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie synchronizacji nazw baz danych klientow Biura dodane do kolejki."
                });
            });

            app.MapPost("/api/jobs/office-jdg-list", async (
                OfficeVatFlagsJob job,
                OfficeVatFlagsJobQueue queue,
                OfficeVatFlagsResultStore resultStore) =>
            {
                var validationError = ValidateBaseRequest(job);
                if (validationError != null)
                {
                    return validationError;
                }

                job.Nip = null;
                job.SyncDatabaseNamesOnly = false;
                job.JdgListOnly = true;
                EnsureJobId(job);

                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie odczytu listy JDG klientow Biura {JobId} dodane do kolejki (Baza: {Database})",
                    job.JobId, job.OfficeDatabaseName);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie odczytu listy JDG klientow Biura dodane do kolejki."
                });
            });

            app.MapGet("/api/jobs/office-vat-flags/{jobId}", (string jobId, OfficeVatFlagsResultStore resultStore) =>
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

            app.MapGet("/api/jobs/office-jdg-list/{jobId}", (string jobId, OfficeVatFlagsResultStore resultStore) =>
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

            app.MapGet("/api/jobs/office-database-names/{jobId}", (string jobId, OfficeVatFlagsResultStore resultStore) =>
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

        private static IResult ValidateBaseRequest(OfficeVatFlagsJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Username) ||
                string.IsNullOrWhiteSpace(job.Password) ||
                string.IsNullOrWhiteSpace(job.OfficeDatabaseName))
            {
                Log.Warning("Odrzucono zlecenie odczytu flag VAT/VAT-UE z Biura - brak wymaganych danych.");
                return Results.BadRequest("Brak danych logowania lub nazwy bazy Biura.");
            }

            return null;
        }

        private static void EnsureJobId(OfficeVatFlagsJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }
    }
}
