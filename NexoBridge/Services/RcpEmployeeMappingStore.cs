using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public sealed class RcpEmployeeMappingStore
    {
        private const string FileName = "employee-mappings.json";

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly ILogger<RcpEmployeeMappingStore> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        public RcpEmployeeMappingStore(ILogger<RcpEmployeeMappingStore> logger)
        {
            _logger = logger;
            FilePath = Path.Combine(AppContext.BaseDirectory, FileName);
        }

        public string FilePath { get; }

        public async Task<RcpEmployeeMappingsDocument> GetAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await LoadLockedAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<RcpEmployeeDatabaseMapping> GetDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            string normalizedDatabaseName = NormalizeRequiredValue(databaseName, nameof(databaseName));

            await _gate.WaitAsync(cancellationToken);
            try
            {
                RcpEmployeeMappingsDocument document = await LoadLockedAsync(cancellationToken);
                return document.Databases.FirstOrDefault(database =>
                    string.Equals(database.DatabaseName, normalizedDatabaseName, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<RcpEmployeeMappingsDocument> ReplaceAsync(
            RcpEmployeeMappingsDocument document,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                RcpEmployeeMappingsDocument normalized = NormalizeDocument(document);
                await SaveLockedAsync(normalized, cancellationToken);
                return normalized;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<RcpEmployeeDatabaseMapping> ReplaceDatabaseAsync(
            string databaseName,
            IEnumerable<RcpEmployeeMapItem> employees,
            CancellationToken cancellationToken = default)
        {
            string normalizedDatabaseName = NormalizeRequiredValue(databaseName, nameof(databaseName));

            await _gate.WaitAsync(cancellationToken);
            try
            {
                RcpEmployeeMappingsDocument document = await LoadLockedAsync(cancellationToken);
                RcpEmployeeDatabaseMapping normalizedDatabase = NormalizeDatabase(new RcpEmployeeDatabaseMapping
                {
                    DatabaseName = normalizedDatabaseName,
                    Employees = employees?.ToList() ?? new List<RcpEmployeeMapItem>()
                });

                UpsertDatabase(document, normalizedDatabase);
                await SaveLockedAsync(document, cancellationToken);
                return normalizedDatabase;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<RcpEmployeeDatabaseMapping> AddOrUpdateEmployeesAsync(
            string databaseName,
            IEnumerable<RcpEmployeeMapItem> employees,
            CancellationToken cancellationToken = default)
        {
            string normalizedDatabaseName = NormalizeRequiredValue(databaseName, nameof(databaseName));

            await _gate.WaitAsync(cancellationToken);
            try
            {
                RcpEmployeeMappingsDocument document = await LoadLockedAsync(cancellationToken);
                RcpEmployeeDatabaseMapping database = GetOrCreateDatabase(document, normalizedDatabaseName);

                foreach (RcpEmployeeMapItem incomingEmployee in NormalizeEmployees(employees, allowNullOverwrite: false))
                {
                    RcpEmployeeMapItem existingEmployee = database.Employees.FirstOrDefault(employee =>
                        string.Equals(employee.EmployeeId, incomingEmployee.EmployeeId, StringComparison.OrdinalIgnoreCase));

                    if (existingEmployee == null)
                    {
                        database.Employees.Add(incomingEmployee);
                        continue;
                    }

                    if (incomingEmployee.Pesel != null)
                    {
                        existingEmployee.Pesel = incomingEmployee.Pesel;
                    }

                    if (incomingEmployee.WorkerName != null)
                    {
                        existingEmployee.WorkerName = incomingEmployee.WorkerName;
                    }
                }

                SortDatabaseEmployees(database);
                SortDatabases(document);
                await SaveLockedAsync(document, cancellationToken);
                return database;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<RcpTimesheetEmployeeSyncResult> EnsureEmployeesFromTimesheetAsync(
            string databaseName,
            RcpTimesheetPayload payload,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(payload);

            string normalizedDatabaseName = NormalizeRequiredValue(databaseName, nameof(databaseName));
            List<string> employeeIds = payload.EmployeesTimesheets
                .Where(employee => !string.IsNullOrWhiteSpace(employee?.EmployeeId))
                .Select(employee => NormalizeRequiredValue(employee.EmployeeId, nameof(employee.EmployeeId)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await _gate.WaitAsync(cancellationToken);
            try
            {
                RcpEmployeeMappingsDocument document = await LoadLockedAsync(cancellationToken);
                RcpEmployeeDatabaseMapping database = GetOrCreateDatabase(document, normalizedDatabaseName);

                List<RcpEmployeeMapItem> addedEmployees = new List<RcpEmployeeMapItem>();
                foreach (string employeeId in employeeIds)
                {
                    bool exists = database.Employees.Any(employee =>
                        string.Equals(employee.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase));

                    if (exists)
                    {
                        continue;
                    }

                    var placeholderEmployee = new RcpEmployeeMapItem
                    {
                        EmployeeId = employeeId,
                        Pesel = null,
                        WorkerName = null
                    };

                    database.Employees.Add(placeholderEmployee);
                    addedEmployees.Add(placeholderEmployee);
                }

                if (addedEmployees.Count > 0)
                {
                    SortDatabaseEmployees(database);
                    SortDatabases(document);
                    await SaveLockedAsync(document, cancellationToken);

                    _logger.LogInformation(
                        "Dopisano {AddedEmployeesCount} nowych pracownikow do mapy RCP dla bazy {DatabaseName}: {EmployeeIds}",
                        addedEmployees.Count,
                        normalizedDatabaseName,
                        string.Join(", ", addedEmployees.Select(employee => employee.EmployeeId)));
                }

                return new RcpTimesheetEmployeeSyncResult
                {
                    DatabaseName = normalizedDatabaseName,
                    PayloadEmployeesCount = employeeIds.Count,
                    AddedEmployeesCount = addedEmployees.Count,
                    AddedEmployees = addedEmployees
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<RcpEmployeeMappingsDocument> LoadLockedAsync(CancellationToken cancellationToken)
        {
            await EnsureFileExistsLockedAsync(cancellationToken);

            string json = await File.ReadAllTextAsync(FilePath, cancellationToken);
            RcpEmployeeMappingsDocument document = JsonSerializer.Deserialize<RcpEmployeeMappingsDocument>(json, _jsonOptions)
                ?? new RcpEmployeeMappingsDocument();

            return NormalizeDocument(document);
        }

        private async Task EnsureFileExistsLockedAsync(CancellationToken cancellationToken)
        {
            string directoryPath = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            if (File.Exists(FilePath))
            {
                return;
            }

            RcpEmployeeMappingsDocument emptyDocument = new RcpEmployeeMappingsDocument();
            await SaveLockedAsync(emptyDocument, cancellationToken);
            _logger.LogInformation("Utworzono nowy plik mapy pracownikow RCP: {FilePath}", FilePath);
        }

        private async Task SaveLockedAsync(RcpEmployeeMappingsDocument document, CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(document, _jsonOptions);
            string tempFilePath = FilePath + ".tmp";

            await File.WriteAllTextAsync(tempFilePath, json, cancellationToken);
            File.Move(tempFilePath, FilePath, overwrite: true);
        }

        private static RcpEmployeeMappingsDocument NormalizeDocument(RcpEmployeeMappingsDocument document)
        {
            List<RcpEmployeeDatabaseMapping> normalizedDatabases = (document.Databases ?? new List<RcpEmployeeDatabaseMapping>())
                .Select(NormalizeDatabase)
                .ToList();

            normalizedDatabases = normalizedDatabases
                .GroupBy(database => database.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(database => database.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new RcpEmployeeMappingsDocument
            {
                Databases = normalizedDatabases
            };
        }

        private static RcpEmployeeDatabaseMapping NormalizeDatabase(RcpEmployeeDatabaseMapping database)
        {
            string normalizedDatabaseName = NormalizeRequiredValue(database.DatabaseName, nameof(database.DatabaseName));
            List<RcpEmployeeMapItem> normalizedEmployees = NormalizeEmployees(database.Employees, allowNullOverwrite: true);

            return new RcpEmployeeDatabaseMapping
            {
                DatabaseName = normalizedDatabaseName,
                Employees = normalizedEmployees
            };
        }

        private static List<RcpEmployeeMapItem> NormalizeEmployees(IEnumerable<RcpEmployeeMapItem> employees, bool allowNullOverwrite)
        {
            IEnumerable<RcpEmployeeMapItem> sourceEmployees = employees ?? Enumerable.Empty<RcpEmployeeMapItem>();

            List<RcpEmployeeMapItem> normalizedEmployees = sourceEmployees
                .Where(employee => employee != null)
                .Select(employee => new RcpEmployeeMapItem
                {
                    EmployeeId = NormalizeRequiredValue(employee.EmployeeId, nameof(employee.EmployeeId)),
                    Pesel = NormalizeOptionalValue(employee.Pesel, allowNullOverwrite),
                    WorkerName = NormalizeOptionalValue(employee.WorkerName, allowNullOverwrite)
                })
                .GroupBy(employee => employee.EmployeeId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(employee => employee.EmployeeId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return normalizedEmployees;
        }

        private static string NormalizeRequiredValue(string value, string parameterName)
        {
            string normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new ArgumentException($"Pole {parameterName} jest wymagane.", parameterName);
            }

            return normalized;
        }

        private static string NormalizeOptionalValue(string value, bool allowNullOverwrite)
        {
            if (value == null && allowNullOverwrite)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return allowNullOverwrite ? null : null;
            }

            return value.Trim();
        }

        private static void UpsertDatabase(RcpEmployeeMappingsDocument document, RcpEmployeeDatabaseMapping database)
        {
            int existingIndex = document.Databases.FindIndex(existingDatabase =>
                string.Equals(existingDatabase.DatabaseName, database.DatabaseName, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                document.Databases[existingIndex] = database;
            }
            else
            {
                document.Databases.Add(database);
            }

            SortDatabases(document);
        }

        private static RcpEmployeeDatabaseMapping GetOrCreateDatabase(RcpEmployeeMappingsDocument document, string databaseName)
        {
            RcpEmployeeDatabaseMapping existingDatabase = document.Databases.FirstOrDefault(database =>
                string.Equals(database.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase));

            if (existingDatabase != null)
            {
                return existingDatabase;
            }

            var newDatabase = new RcpEmployeeDatabaseMapping
            {
                DatabaseName = databaseName
            };

            document.Databases.Add(newDatabase);
            SortDatabases(document);
            return newDatabase;
        }

        private static void SortDatabases(RcpEmployeeMappingsDocument document)
        {
            document.Databases = document.Databases
                .OrderBy(database => database.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void SortDatabaseEmployees(RcpEmployeeDatabaseMapping database)
        {
            database.Employees = database.Employees
                .OrderBy(employee => employee.EmployeeId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
