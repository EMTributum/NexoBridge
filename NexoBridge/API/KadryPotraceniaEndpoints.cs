using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.API
{
    /// <summary>
    /// Synchroniczne (bez Sfery, bez kolejki) odczyty potrąceń komorniczych z baz Gratyfikanta:
    ///   POST /api/kadry/potracenia/lista     - wszystkie potrącenia podanych baz klientów (bez PESEL i adresów),
    ///   POST /api/kadry/potracenia/szczegoly - pełne dane jednego potrącenia do pisma (z PESEL i adresami).
    /// Tylko odczyt na koncie DB_READ_USER (ApplicationIntent=ReadOnly) - patrz KadryPotraceniaSqlService.
    /// Do logów nie trafiają dane osobowe, tylko baza, id, liczby i czasy.
    /// </summary>
    public static class KadryPotraceniaEndpoints
    {
        private const int MinYear = 2000;
        private const int MaxYear = 2100;

        public static IEndpointRouteBuilder MapKadryPotraceniaEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/kadry/potracenia");

            group.MapPost("/lista", async (
                KadryPotraceniaListRequest request,
                KadryPotraceniaSqlService service,
                CancellationToken cancellationToken) =>
            {
                if (request == null || !IsValidPeriod(request.PeriodYear, request.PeriodMonth))
                {
                    return Results.BadRequest("Niepoprawny rok/miesiąc okresu (periodYear / periodMonth).");
                }

                if (request.ClientDatabases == null || request.ClientDatabases.Count > KadryPotraceniaSqlService.MaxClientDatabases)
                {
                    return Results.BadRequest($"Brak listy baz klientów (clientDatabases) albo więcej niż {KadryPotraceniaSqlService.MaxClientDatabases} pozycji.");
                }

                // Duplikaty nazw baz (bez rozróżniania wielkości liter) odczytujemy raz.
                var databases = request.ClientDatabases
                    .GroupBy(c => c?.DatabaseName?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                return await ExecuteAsync("listy potrąceń komorniczych", $"baz: {databases.Count}", async () =>
                    await service.ReadListAsync(request.PeriodYear, request.PeriodMonth, databases, cancellationToken));
            });

            group.MapPost("/szczegoly", async (
                KadryPotraceniaDetailsRequest request,
                KadryPotraceniaSqlService service,
                CancellationToken cancellationToken) =>
            {
                if (request == null || !KadryPotraceniaSqlService.IsValidDatabaseName(request.DatabaseName))
                {
                    return Results.BadRequest("Niepoprawna nazwa bazy (databaseName) - oczekiwano technicznej nazwy bazy nexo zaczynającej się od 'Nexo_'.");
                }

                if (!IsValidPeriod(request.PeriodYear, request.PeriodMonth))
                {
                    return Results.BadRequest("Niepoprawny rok/miesiąc okresu (periodYear / periodMonth).");
                }

                if (request.PotracenieId <= 0)
                {
                    return Results.BadRequest("Niepoprawne id potrącenia (potracenieId).");
                }

                return await ExecuteAsync("szczegółów potrącenia komorniczego", $"baza: {request.DatabaseName.Trim()}, id: {request.PotracenieId}", async () =>
                    await service.ReadDetailsAsync(request.DatabaseName, request.PotracenieId, request.PeriodYear, request.PeriodMonth, cancellationToken));
            });

            return app;
        }

        private static bool IsValidPeriod(int year, int month)
        {
            return year >= MinYear && year <= MaxYear && month >= 1 && month <= 12;
        }

        private static async Task<IResult> ExecuteAsync<T>(string what, string context, Func<Task<T>> read)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var response = await read();
                Log.Information("Odczyt {What} zakończony ({Context}, {ElapsedMs} ms)", what, context, watch.ElapsedMilliseconds);
                return Results.Ok(response);
            }
            catch (KadryPotraceniaNotFoundException ex)
            {
                Log.Warning("Odczyt {What}: nie znaleziono ({Context}): {Message}", what, context, ex.Message);
                return Results.NotFound(new { Message = ex.Message });
            }
            catch (KadryPotraceniaConfigurationException ex)
            {
                Log.Error("Odczyt {What} niemożliwy: {Message}", what, ex.Message);
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Odczyt {What} anulowany przez klienta ({Context})", what, context);
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (SqlException ex)
            {
                Log.Error(ex, "Błąd SQL podczas odczytu {What} ({Context}, SQL {Number}).", what, context, ex.Number);
                return Results.Problem($"Błąd SQL podczas odczytu {what} (SQL {ex.Number}).", statusCode: StatusCodes.Status500InternalServerError);
            }
        }
    }
}
