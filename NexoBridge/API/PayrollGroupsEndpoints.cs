using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;
using System.Threading;

namespace NexoBridge.API
{
    public static class PayrollGroupsEndpoints
    {
        public static IEndpointRouteBuilder MapPayrollGroupsEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/payroll-groups", async (
                PayrollGroupsRequest request,
                CancellationToken cancellationToken) =>
            {
                if (request == null || !GratyfikantReadOnlyAccess.IsValidDatabaseName(request.DatabaseName))
                {
                    return Results.BadRequest("Niepoprawna nazwa bazy Gratyfikanta.");
                }
                if (request.PeriodYear <= 0 || request.PeriodMonth is < 1 or > 12)
                {
                    return Results.BadRequest("Niepoprawny rok/miesiąc okresu.");
                }
                if (request.MonthsBack is < 1 or > 36)
                {
                    return Results.BadRequest("MonthsBack musi mieścić się w zakresie 1-36.");
                }

                try
                {
                    PayrollGroupsResponse response = await PayrollGroupCounter.DiscoverAsync(request, cancellationToken);
                    return Results.Ok(response);
                }
                catch (OfficeReportsConfigurationException ex)
                {
                    Log.Warning(ex, "Brak konfiguracji odczytu grup kadrowych dla bazy {Database}.", request.DatabaseName);
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                catch (SqlException ex)
                {
                    Log.Warning(ex, "Błąd SQL podczas odczytu grup kadrowych dla bazy {Database}.", request.DatabaseName);
                    return Results.Problem("Nie udało się odczytać grup z bazy Gratyfikanta.", statusCode: StatusCodes.Status502BadGateway);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Błąd odczytu grup kadrowych dla bazy {Database}.", request.DatabaseName);
                    return Results.Problem(ex.GetBaseException().Message, statusCode: StatusCodes.Status500InternalServerError);
                }
            });

            return app;
        }
    }
}
