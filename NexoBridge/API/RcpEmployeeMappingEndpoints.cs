using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Models;
using NexoBridge.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.API
{
    public static class RcpEmployeeMappingEndpoints
    {
        public static IEndpointRouteBuilder MapRcpEmployeeMappingEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/rcp/employee-mappings", async (
                RcpEmployeeMappingStore store,
                CancellationToken cancellationToken) =>
            {
                RcpEmployeeMappingsDocument document = await store.GetAsync(cancellationToken);
                return Results.Ok(new
                {
                    FilePath = store.FilePath,
                    Mappings = document
                });
            });

            app.MapGet("/api/rcp/employee-mappings/databases/{databaseName}", async (
                string databaseName,
                RcpEmployeeMappingStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    return Results.BadRequest("Brak nazwy bazy.");
                }

                RcpEmployeeDatabaseMapping database = await store.GetDatabaseAsync(databaseName, cancellationToken);
                return database == null
                    ? Results.NotFound(new { DatabaseName = databaseName, Message = "Nie znaleziono mapy dla wskazanej bazy." })
                    : Results.Ok(database);
            });

            app.MapPut("/api/rcp/employee-mappings", async (
                RcpEmployeeMappingsDocument document,
                RcpEmployeeMappingStore store,
                CancellationToken cancellationToken) =>
            {
                string validationError = ValidateDocument(document);
                if (validationError != null)
                {
                    return Results.BadRequest(validationError);
                }

                RcpEmployeeMappingsDocument savedDocument = await store.ReplaceAsync(document, cancellationToken);
                Log.Information("Zastapiono cala mape pracownikow RCP. Liczba baz: {DatabaseCount}", savedDocument.Databases.Count);
                return Results.Ok(savedDocument);
            });

            app.MapPut("/api/rcp/employee-mappings/databases/{databaseName}", async (
                string databaseName,
                RcpEmployeeDatabaseMapping request,
                RcpEmployeeMappingStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    return Results.BadRequest("Brak nazwy bazy.");
                }

                string validationError = ValidateEmployees(request?.Employees, allowEmpty: true);
                if (validationError != null)
                {
                    return Results.BadRequest(validationError);
                }

                RcpEmployeeDatabaseMapping savedDatabase = await store.ReplaceDatabaseAsync(
                    databaseName,
                    request?.Employees ?? new List<RcpEmployeeMapItem>(),
                    cancellationToken);

                Log.Information(
                    "Zastapiono mape pracownikow RCP dla bazy {DatabaseName}. Liczba pracownikow: {EmployeeCount}",
                    savedDatabase.DatabaseName,
                    savedDatabase.Employees.Count);

                return Results.Ok(savedDatabase);
            });

            app.MapPost("/api/rcp/employee-mappings/databases/{databaseName}/employees", async (
                string databaseName,
                RcpEmployeeUpsertRequest request,
                RcpEmployeeMappingStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    return Results.BadRequest("Brak nazwy bazy.");
                }

                string validationError = ValidateEmployees(request?.Employees, allowEmpty: false);
                if (validationError != null)
                {
                    return Results.BadRequest(validationError);
                }

                RcpEmployeeDatabaseMapping savedDatabase = await store.AddOrUpdateEmployeesAsync(
                    databaseName,
                    request.Employees,
                    cancellationToken);

                Log.Information(
                    "Dodano lub zaktualizowano pracownikow mapy RCP dla bazy {DatabaseName}. Liczba wpisow przeslanych: {SubmittedCount}",
                    savedDatabase.DatabaseName,
                    request.Employees.Count);

                return Results.Ok(savedDatabase);
            });

            app.MapPost("/api/rcp/employee-mappings/databases/{databaseName}/sync-from-timesheet", async (
                string databaseName,
                RcpTimesheetPayload payload,
                RcpEmployeeMappingStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    return Results.BadRequest("Brak nazwy bazy.");
                }

                if (payload == null)
                {
                    return Results.BadRequest("Brak payloadu RCP.");
                }

                RcpTimesheetEmployeeSyncResult result = await store.EnsureEmployeesFromTimesheetAsync(
                    databaseName,
                    payload,
                    cancellationToken);

                Log.Information(
                    "Zsynchronizowano brakujacych pracownikow mapy RCP z payloadu dla bazy {DatabaseName}. PayloadEmployees={PayloadEmployeesCount}, Added={AddedEmployeesCount}",
                    result.DatabaseName,
                    result.PayloadEmployeesCount,
                    result.AddedEmployeesCount);

                return Results.Ok(result);
            });

            return app;
        }

        private static string ValidateDocument(RcpEmployeeMappingsDocument document)
        {
            if (document == null)
            {
                return "Brak dokumentu mapy pracownikow.";
            }

            HashSet<string> databaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RcpEmployeeDatabaseMapping database in document.Databases ?? new List<RcpEmployeeDatabaseMapping>())
            {
                if (database == null || string.IsNullOrWhiteSpace(database.DatabaseName))
                {
                    return "Kazda baza w mapie musi miec uzupelnione pole databaseName.";
                }

                if (!databaseNames.Add(database.DatabaseName.Trim()))
                {
                    return $"W mapie wystepuje zduplikowana baza '{database.DatabaseName}'.";
                }

                string employeeValidationError = ValidateEmployees(database.Employees, allowEmpty: true);
                if (employeeValidationError != null)
                {
                    return $"{employeeValidationError} (baza: {database.DatabaseName})";
                }
            }

            return null;
        }

        private static string ValidateEmployees(IEnumerable<RcpEmployeeMapItem> employees, bool allowEmpty)
        {
            List<RcpEmployeeMapItem> employeeList = employees?.ToList() ?? new List<RcpEmployeeMapItem>();
            if (!allowEmpty && employeeList.Count == 0)
            {
                return "Lista pracownikow nie moze byc pusta.";
            }

            HashSet<string> employeeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RcpEmployeeMapItem employee in employeeList)
            {
                if (employee == null || string.IsNullOrWhiteSpace(employee.EmployeeId))
                {
                    return "Kazdy pracownik musi miec uzupelnione pole employeeId.";
                }

                string normalizedEmployeeId = employee.EmployeeId.Trim();
                if (!employeeIds.Add(normalizedEmployeeId))
                {
                    return $"W liscie pracownikow wystepuje zduplikowany employeeId '{normalizedEmployeeId}'.";
                }
            }

            return null;
        }
    }
}
