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

namespace NexoBridge.API
{
    /// <summary>
    /// Synchroniczny (bez Sfery, bez kolejki) odczyt podstaw składek ZUS pracowników jednej bazy Gratyfikanta za miesiąc:
    ///   POST /api/kadry/podstawy-zus - {databaseName, periodYear, periodMonth}.
    /// Tylko odczyt na koncie DB_READ_USER (ApplicationIntent=ReadOnly) - patrz KadryPodstawyZusSqlService.
    /// Do logów nie trafiają dane osobowe, tylko baza, liczby i czasy.
    /// </summary>
    public static class KadryPodstawyZusEndpoints
    {
        private const int MinYear = 2000;
        private const int MaxYear = 2100;

        public static IEndpointRouteBuilder MapKadryPodstawyZusEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/kadry/podstawy-zus", async (
                KadryPodstawyZusRequest request,
                KadryPodstawyZusSqlService service,
                CancellationToken cancellationToken) =>
            {
                if (request == null || !KadryPodstawyZusSqlService.IsValidDatabaseName(request.DatabaseName))
                {
                    return Results.BadRequest("Niepoprawna nazwa bazy (databaseName) - oczekiwano technicznej nazwy bazy nexo zaczynającej się od 'Nexo_'.");
                }

                if (request.PeriodYear < MinYear || request.PeriodYear > MaxYear || request.PeriodMonth < 1 || request.PeriodMonth > 12)
                {
                    return Results.BadRequest("Niepoprawny rok/miesiąc okresu (periodYear / periodMonth).");
                }

                string context = $"baza: {request.DatabaseName.Trim()}, okres: {request.PeriodYear}-{request.PeriodMonth:00}";
                var watch = Stopwatch.StartNew();
                try
                {
                    var response = await service.ReadAsync(request.DatabaseName, request.PeriodYear, request.PeriodMonth, cancellationToken);
                    Log.Information("Odczyt podstaw składek ZUS zakończony ({Context}): osób {Count}, {ElapsedMs} ms",
                        context, response.Employees.Count, watch.ElapsedMilliseconds);
                    return Results.Ok(response);
                }
                catch (KadryPodstawyZusNotFoundException ex)
                {
                    Log.Warning("Odczyt podstaw składek ZUS: nie znaleziono ({Context}): {Message}", context, ex.Message);
                    return Results.NotFound(new { Message = ex.Message });
                }
                catch (KadryPodstawyZusConfigurationException ex)
                {
                    Log.Error("Odczyt podstaw składek ZUS niemożliwy: {Message}", ex.Message);
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("Odczyt podstaw składek ZUS anulowany przez klienta ({Context})", context);
                    return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
                }
                catch (SqlException ex)
                {
                    Log.Error(ex, "Błąd SQL podczas odczytu podstaw składek ZUS ({Context}, SQL {Number}).", context, ex.Number);
                    return Results.Problem($"Błąd SQL podczas odczytu podstaw składek ZUS (SQL {ex.Number}).", statusCode: StatusCodes.Status500InternalServerError);
                }
            });

            return app;
        }
    }
}
