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
    public static class BillingEndpoints
    {
        public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
        {
            var billingGroup = app.MapGroup("/api/jobs/billing-snapshot");

            billingGroup.MapPost("", async (
                BillingSnapshotJob job,
                BillingJobQueue queue,
                BillingResultStore resultStore) =>
            {
                var validationError = ValidateBillingRequest(job);
                if (validationError != null)
                {
                    return validationError;
                }

                EnsureJobId(job);
                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie odczytu konfiguracji billingowej {JobId} dodane do kolejki (Baza: {Database}, NIP: {Nip})",
                    job.JobId, job.DatabaseName, job.Nip);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie odczytu konfiguracji billingowej dodane do kolejki."
                });
            });

            billingGroup.MapGet("/{jobId}", (string jobId, BillingResultStore resultStore) =>
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

            var invoiceGroup = app.MapGroup("/api/jobs/invoices");

            invoiceGroup.MapPost("", async (
                InvoiceCreationJob job,
                InvoiceCreationJobQueue queue,
                InvoiceCreationResultStore resultStore) =>
            {
                var validationError = ValidateInvoiceRequest(job);
                if (validationError != null)
                {
                    return validationError;
                }

                EnsureJobId(job);

                if (!resultStore.TryReserveIdempotencyKey(job.IdempotencyKey, job.JobId, out string existingJobId))
                {
                    Log.Warning("Odrzucono zduplikowane zlecenie tworzenia faktury (IdempotencyKey={IdempotencyKey}). Istniejące zlecenie: {ExistingJobId}",
                        job.IdempotencyKey, existingJobId);

                    return Results.Conflict(new
                    {
                        JobId = existingJobId,
                        Status = "DUPLICATE",
                        Message = $"Zlecenie z kluczem idempotencji '{job.IdempotencyKey}' zostało już złożone jako {existingJobId}."
                    });
                }

                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie utworzenia faktury {JobId} dodane do kolejki (Baza: {Database}, NIP: {Nip}, Okres: {Year}-{Month})",
                    job.JobId, job.DatabaseName, job.Nip, job.ServiceYear, job.ServiceMonth);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie utworzenia faktury dodane do kolejki."
                });
            });

            invoiceGroup.MapGet("/{jobId}", (string jobId, InvoiceCreationResultStore resultStore) =>
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

            var billingClientsGroup = app.MapGroup("/api/jobs/billing-clients");

            billingClientsGroup.MapPost("", async (
                BillingClientsJob job,
                BillingClientsJobQueue queue,
                BillingClientsResultStore resultStore) =>
            {
                if (string.IsNullOrWhiteSpace(job.Username) ||
                    string.IsNullOrWhiteSpace(job.Password) ||
                    string.IsNullOrWhiteSpace(job.DatabaseName))
                {
                    Log.Warning("Odrzucono zlecenie odczytu listy klientów do rozliczenia - brak danych logowania albo nazwy bazy.");
                    return Results.BadRequest("Brak danych logowania albo nazwy bazy.");
                }

                EnsureJobId(job);
                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information("Zlecenie odczytu listy klientów do rozliczenia {JobId} dodane do kolejki (Baza: {Database})",
                    job.JobId, job.DatabaseName);

                return Results.Accepted(value: new
                {
                    JobId = job.JobId,
                    Message = "Zlecenie odczytu listy klientów do rozliczenia dodane do kolejki."
                });
            });

            billingClientsGroup.MapGet("/{jobId}", (string jobId, BillingClientsResultStore resultStore) =>
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

        private static IResult ValidateBillingRequest(BillingSnapshotJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Username) ||
                string.IsNullOrWhiteSpace(job.Password) ||
                string.IsNullOrWhiteSpace(job.DatabaseName) ||
                string.IsNullOrWhiteSpace(job.Nip))
            {
                Log.Warning("Odrzucono zlecenie odczytu konfiguracji billingowej - brak wymaganych danych.");
                return Results.BadRequest("Brak danych logowania, nazwy bazy albo NIP.");
            }

            return null;
        }

        private static IResult ValidateInvoiceRequest(InvoiceCreationJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Username) ||
                string.IsNullOrWhiteSpace(job.Password) ||
                string.IsNullOrWhiteSpace(job.DatabaseName) ||
                string.IsNullOrWhiteSpace(job.Nip))
            {
                Log.Warning("Odrzucono zlecenie utworzenia faktury - brak danych logowania, nazwy bazy albo NIP.");
                return Results.BadRequest("Brak danych logowania, nazwy bazy albo NIP.");
            }

            if (job.ServiceYear <= 0 || job.ServiceMonth is < 1 or > 12)
            {
                Log.Warning("Odrzucono zlecenie utworzenia faktury - niepoprawny okres {Year}-{Month}.", job.ServiceYear, job.ServiceMonth);
                return Results.BadRequest("Niepoprawny rok/miesiąc usługi.");
            }

            if (job.Lines == null || job.Lines.Count == 0)
            {
                Log.Warning("Odrzucono zlecenie utworzenia faktury - brak pozycji.");
                return Results.BadRequest("Zlecenie musi zawierać co najmniej jedną pozycję faktury.");
            }

            if (string.IsNullOrWhiteSpace(job.IdempotencyKey))
            {
                Log.Warning("Odrzucono zlecenie utworzenia faktury - brak klucza idempotencji.");
                return Results.BadRequest("Brak klucza idempotencji (IdempotencyKey).");
            }

            if (!string.Equals(job.PaymentMethod, "Card", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(job.PaymentMethod, "Transfer", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Odrzucono zlecenie utworzenia faktury - nieprawidłowa metoda płatności '{PaymentMethod}'.", job.PaymentMethod);
                return Results.BadRequest("Nieprawidłowa lub brakująca metoda płatności (PaymentMethod) - oczekiwano 'Card' albo 'Transfer'.");
            }

            return null;
        }

        private static void EnsureJobId(BillingSnapshotJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }

        private static void EnsureJobId(InvoiceCreationJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }

        private static void EnsureJobId(BillingClientsJob job)
        {
            if (string.IsNullOrEmpty(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }
    }
}
