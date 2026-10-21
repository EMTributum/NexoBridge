using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.API
{
    /// <summary>
    /// Synchroniczne (bez Sfery, bez kolejki) odczyty z WŁASNEJ bazy biura dla raportów biura:
    ///   POST /api/office-reports/payroll-costs  - koszt pracodawcy z list płac / rachunków za miesiąc listy,
    ///   POST /api/office-reports/sales-invoices - faktury FS z pozycjami wystawione w danym miesiącu.
    /// Tylko odczyt (ApplicationIntent=ReadOnly, sparametryzowane SELECT-y) - patrz OfficeReportsSqlService.
    /// </summary>
    public static class OfficeReportsEndpoints
    {
        private const int MinYear = 2000;
        private const int MaxYear = 2100;
        private const int MaxDatabaseNameLength = 128;
        private const string NexoDatabasePrefix = "Nexo_";

        public static IEndpointRouteBuilder MapOfficeReportsEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/office-reports");

            group.MapPost("/payroll-costs", async (
                OfficePayrollCostsRequest request,
                OfficeReportsSqlService service,
                CancellationToken cancellationToken) =>
            {
                var validationError = ValidateRequest("kosztów list płac", request?.DatabaseName, request?.Year ?? 0, request?.Month ?? 0);
                if (validationError != null)
                {
                    return validationError;
                }

                string databaseName = request.DatabaseName.Trim();
                return await ExecuteAsync(
                    "kosztów list płac",
                    databaseName,
                    request.Year,
                    request.Month,
                    async () =>
                    {
                        var response = await service.ReadPayrollCostsAsync(databaseName, request.Year, request.Month, cancellationToken);
                        if (response.Company == null)
                        {
                            Log.Warning(
                                "Odczyt kosztów list płac (Baza: {Database}): w bazie {CompanyCount} rekordów własnej firmy (MojaFirma) - zwracam company = null.",
                                databaseName, response.CompanyCount);
                        }
                        return (response, $"dokumentów płacowych: {response.DocumentCount}");
                    });
            });

            group.MapPost("/sales-invoices", async (
                OfficeSalesInvoicesRequest request,
                OfficeReportsSqlService service,
                CancellationToken cancellationToken) =>
            {
                var validationError = ValidateRequest("faktur sprzedaży FS", request?.DatabaseName, request?.IssueYear ?? 0, request?.IssueMonth ?? 0);
                if (validationError != null)
                {
                    return validationError;
                }

                string databaseName = request.DatabaseName.Trim();
                return await ExecuteAsync(
                    "faktur sprzedaży FS",
                    databaseName,
                    request.IssueYear,
                    request.IssueMonth,
                    async () =>
                    {
                        var response = await service.ReadSalesInvoicesAsync(databaseName, request.IssueYear, request.IssueMonth, cancellationToken);
                        return (response, $"faktur: {response.DocumentCount}, pozycji: {response.PositionCount}");
                    });
            });

            return app;
        }

        private static async Task<IResult> ExecuteAsync<T>(
            string what,
            string databaseName,
            int year,
            int month,
            Func<Task<(T Response, string Summary)>> read)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var (response, summary) = await read();
                Log.Information(
                    "Odczyt {What} z bazy biura zakończony (Baza: {Database}, Okres: {Year}-{Month:00}, {Summary}, {ElapsedMs} ms)",
                    what, databaseName, year, month, summary, watch.ElapsedMilliseconds);
                return Results.Ok(response);
            }
            catch (OfficeReportsException ex)
            {
                Log.Warning(
                    "Odczyt {What} z bazy biura przerwany (Baza: {Database}, Okres: {Year}-{Month:00}): {Message}",
                    what, databaseName, year, month, ex.Message);
                return Results.UnprocessableEntity(new { Message = ex.Message });
            }
            catch (OfficeReportsConfigurationException ex)
            {
                Log.Error("Odczyt {What} z bazy biura niemożliwy: {Message}", what, ex.Message);
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (SqlException ex) when (ex.Number == 4060)
            {
                Log.Warning(
                    "Odczyt {What} z bazy biura: nie można otworzyć bazy {Database} (SQL {Number}).",
                    what, databaseName, ex.Number);
                return Results.NotFound(new { Message = $"Nie można otworzyć bazy '{databaseName}'. Sprawdź techniczną nazwę bazy nexo." });
            }
            catch (SqlException ex)
            {
                Log.Error(ex,
                    "Błąd SQL podczas odczytu {What} z bazy biura (Baza: {Database}, Okres: {Year}-{Month:00}, SQL {Number}).",
                    what, databaseName, year, month, ex.Number);
                return Results.Problem(
                    $"Błąd SQL podczas odczytu {what} z bazy biura (SQL {ex.Number}).",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        private static IResult ValidateRequest(string what, string databaseName, int year, int month)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                Log.Warning("Odrzucono odczyt {What} z bazy biura - brak nazwy bazy.", what);
                return Results.BadRequest("Brak nazwy bazy biura (databaseName).");
            }

            string trimmed = databaseName.Trim();
            if (trimmed.Length > MaxDatabaseNameLength ||
                !trimmed.StartsWith(NexoDatabasePrefix, StringComparison.OrdinalIgnoreCase) ||
                HasControlCharacters(trimmed))
            {
                Log.Warning("Odrzucono odczyt {What} z bazy biura - niepoprawna nazwa bazy {Database}.", what, databaseName);
                return Results.BadRequest($"Niepoprawna nazwa bazy biura (oczekiwano technicznej nazwy bazy nexo zaczynającej się od '{NexoDatabasePrefix}').");
            }

            if (year < MinYear || year > MaxYear || month < 1 || month > 12)
            {
                Log.Warning("Odrzucono odczyt {What} z bazy biura - niepoprawny okres {Year}-{Month}.", what, year, month);
                return Results.BadRequest("Niepoprawny rok/miesiąc okresu.");
            }

            return null;
        }

        private static bool HasControlCharacters(string value)
        {
            foreach (char c in value)
            {
                if (char.IsControl(c))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
