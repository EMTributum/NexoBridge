using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.API
{
    public static class RcpImportEndpoints
    {
        public static IEndpointRouteBuilder MapRcpImportEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/rcp/jobs/import", async (
                RcpImportRequest request,
                RcpImportJobQueue queue,
                RcpImportResultStore resultStore,
                RcpEmployeeMappingStore mappingStore,
                RcpRuntimeSettings runtimeSettings,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request?.DatabaseName))
                {
                    return Results.BadRequest("Brak nazwy bazy.");
                }

                if (request.Payload == null)
                {
                    return Results.BadRequest("Brak payloadu RCP.");
                }

                NormalizePayloadPeriod(request.Payload);
                string username = runtimeSettings.GetResolvedUsername(request.Username);
                string password = runtimeSettings.GetResolvedPassword(request.Password);
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    return Results.BadRequest("Brak danych logowania nexo w żądaniu oraz w zmiennych środowiskowych.");
                }

                RcpTimesheetEmployeeSyncResult syncResult =
                    await mappingStore.EnsureEmployeesFromTimesheetAsync(request.DatabaseName, request.Payload, cancellationToken);

                var job = new RcpImportJob
                {
                    JobId = EnsureJobId(request.JobId),
                    Username = username,
                    Password = password,
                    DatabaseName = request.DatabaseName.Trim(),
                    PeriodYear = request.Payload.PeriodYear,
                    PeriodMonth = request.Payload.PeriodMonth,
                    SourceMode = "manual-payload",
                    PayloadHash = RcpSourceClient.ComputePayloadHash(request.Payload),
                    Payload = request.Payload
                };

                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information(
                    "Dodano do kolejki ręczny import RCP {JobId} dla bazy {DatabaseName} i okresu {PeriodYear}-{PeriodMonth:00}.",
                    job.JobId,
                    job.DatabaseName,
                    job.PeriodYear,
                    job.PeriodMonth);

                return Results.Accepted(value: new
                {
                    jobId = job.JobId,
                    message = "Zlecenie importu RCP dodane do kolejki.",
                    syncResult
                });
            });

            app.MapPost("/api/rcp/jobs/import-from-source", async (
                RcpImportFromSourceRequest request,
                RcpImportJobQueue queue,
                RcpImportResultStore resultStore,
                RcpImportStateStore stateStore,
                RcpEmployeeMappingStore mappingStore,
                RcpSourceClient sourceClient,
                RcpRuntimeSettings runtimeSettings,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request?.DatabaseName))
                {
                    return Results.BadRequest("Brak nazwy bazy.");
                }

                string sourceUrl = runtimeSettings.GetResolvedSourceUrl(request.SourceUrl);
                if (string.IsNullOrWhiteSpace(sourceUrl))
                {
                    return Results.BadRequest("Brak adresu źródła RCP. Ustaw RCP_SOURCE_URL albo podaj sourceUrl w żądaniu.");
                }

                string username = runtimeSettings.GetResolvedUsername(request.Username);
                string password = runtimeSettings.GetResolvedPassword(request.Password);
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    return Results.BadRequest("Brak danych logowania nexo w żądaniu oraz w zmiennych środowiskowych.");
                }

                ResolvePeriod(request, out int periodYear, out int periodMonth);

                RcpSourceFetchResult fetchResult = await sourceClient.FetchTimesheetAsync(
                    sourceUrl,
                    request.DatabaseName.Trim(),
                    periodYear,
                    periodMonth,
                    cancellationToken);

                if (!fetchResult.IsReady)
                {
                    return Results.Ok(new
                    {
                        status = "NOT_READY",
                        databaseName = request.DatabaseName.Trim(),
                        periodYear,
                        periodMonth,
                        message = fetchResult.Message
                    });
                }

                if (fetchResult.Payload.PeriodYear != periodYear || fetchResult.Payload.PeriodMonth != periodMonth)
                {
                    return Results.Problem(
                        detail: $"Źródło zwróciło okres {fetchResult.Payload.PeriodYear}-{fetchResult.Payload.PeriodMonth:00}, a oczekiwano {periodYear}-{periodMonth:00}.",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                RcpTimesheetEmployeeSyncResult syncResult =
                    await mappingStore.EnsureEmployeesFromTimesheetAsync(request.DatabaseName, fetchResult.Payload, cancellationToken);

                if (!request.Force)
                {
                    bool alreadyImported = await stateStore.HasSuccessfulImportAsync(
                        request.DatabaseName.Trim(),
                        periodYear,
                        periodMonth,
                        fetchResult.PayloadHash,
                        cancellationToken);

                    if (alreadyImported)
                    {
                        return Results.Ok(new
                        {
                            status = "ALREADY_IMPORTED",
                            databaseName = request.DatabaseName.Trim(),
                            periodYear,
                            periodMonth,
                            message = "Identyczny payload został już wcześniej zaimportowany.",
                            syncResult
                        });
                    }
                }

                var job = new RcpImportJob
                {
                    JobId = EnsureJobId(request.JobId),
                    Username = username,
                    Password = password,
                    DatabaseName = request.DatabaseName.Trim(),
                    PeriodYear = periodYear,
                    PeriodMonth = periodMonth,
                    SourceMode = "manual-source",
                    SourceUrl = fetchResult.EffectiveUrl,
                    PayloadHash = fetchResult.PayloadHash,
                    Payload = fetchResult.Payload
                };

                resultStore.MarkPending(job.JobId);
                await queue.QueueJobAsync(job);

                Log.Information(
                    "Dodano do kolejki ręczny import RCP ze źródła {JobId} dla bazy {DatabaseName} i okresu {PeriodYear}-{PeriodMonth:00}.",
                    job.JobId,
                    job.DatabaseName,
                    job.PeriodYear,
                    job.PeriodMonth);

                return Results.Accepted(value: new
                {
                    status = "QUEUED",
                    jobId = job.JobId,
                    databaseName = job.DatabaseName,
                    periodYear = job.PeriodYear,
                    periodMonth = job.PeriodMonth,
                    message = "Zlecenie importu RCP ze źródła dodane do kolejki.",
                    syncResult
                });
            });

            app.MapGet("/api/rcp/jobs/{jobId}", (string jobId, RcpImportResultStore resultStore) =>
            {
                if (resultStore.TryGet(jobId, out RcpImportReport report))
                {
                    return Results.Ok(report);
                }

                if (resultStore.IsPending(jobId))
                {
                    return Results.Accepted(value: new { jobId, message = "Zlecenie importu RCP jest nadal przetwarzane." });
                }

                return Results.NotFound(new { jobId, message = "Nie znaleziono zlecenia importu RCP." });
            });

            return app;
        }

        private static string EnsureJobId(string jobId)
        {
            return string.IsNullOrWhiteSpace(jobId)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : jobId.Trim();
        }

        private static void ResolvePeriod(RcpImportFromSourceRequest request, out int periodYear, out int periodMonth)
        {
            if (request.PeriodYear > 0 && request.PeriodMonth is >= 1 and <= 12)
            {
                periodYear = request.PeriodYear;
                periodMonth = request.PeriodMonth;
                return;
            }

            DateTime previousMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
            periodYear = previousMonth.Year;
            periodMonth = previousMonth.Month;
        }

        private static void NormalizePayloadPeriod(RcpTimesheetPayload payload)
        {
            if (payload.PeriodYear > 0 && payload.PeriodMonth is >= 1 and <= 12)
            {
                return;
            }

            var dates = payload.EmployeesTimesheets
                ?.Where(employee => employee?.Shifts != null)
                .SelectMany(employee => employee.Shifts)
                .Where(shift => shift != null && !string.IsNullOrWhiteSpace(shift.Date))
                .Select(shift => DateTime.ParseExact(shift.Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList()
                ?? new System.Collections.Generic.List<DateTime>();

            var distinctPeriods = dates
                .Select(date => (date.Year, date.Month))
                .Distinct()
                .ToList();

            if (distinctPeriods.Count == 1)
            {
                payload.PeriodYear = distinctPeriods[0].Year;
                payload.PeriodMonth = distinctPeriods[0].Month;
                return;
            }

            throw new InvalidOperationException("Payload RCP musi zawierać periodYear i periodMonth albo wszystkie zmiany muszą dotyczyć jednego miesiąca.");
        }
    }
}
