using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public sealed class PoczekalniaBaselineDocument
    {
        public List<PoczekalniaBaselineEntry> Companies { get; set; } = new List<PoczekalniaBaselineEntry>();
    }

    public sealed class PoczekalniaBaselineEntry
    {
        public string DatabaseName { get; set; }
        public List<int> ZnaneNumeryDokumentow { get; set; } = new List<int>();
        public DateTimeOffset ZaktualizowanoUtc { get; set; }
    }

    /// <summary>
    /// Zamiennik zawodnego znacznika "Nowy" (CzyNowy) ze Sfery: przechowuje, dla każdej bazy firmowej,
    /// zbiór numerów (DokumentDoKsiegowania.Nr - to jest realny klucz główny, nie Id-GUID) dokumentów
    /// uznawanych za "znane" w poczekalni. Stan jest zawsze nadpisywany żywym stanem puli po dekretacji,
    /// więc nie rośnie w nieskończoność i samo-naprawia się przy manualnej dekretacji poza NexoBridge.
    /// </summary>
    public sealed class PoczekalniaBaselineStore
    {
        private const string FileName = "poczekalnia-baseline.json";

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly ILogger<PoczekalniaBaselineStore> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        public PoczekalniaBaselineStore(ILogger<PoczekalniaBaselineStore> logger)
        {
            _logger = logger;
            FilePath = Path.Combine(AppContext.BaseDirectory, FileName);
        }

        public string FilePath { get; }

        /// <summary>
        /// Zwraca zbiór znanych numerów dla danej bazy, albo null jeśli baza nie ma jeszcze zapisanego
        /// baseline'u (bootstrap - wywołujący ma sam zdecydować, co wtedy uznać za punkt odniesienia).
        /// </summary>
        public async Task<HashSet<int>> PobierzZnaneNumeryAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                PoczekalniaBaselineDocument document = await LoadLockedAsync(cancellationToken);
                PoczekalniaBaselineEntry entry = ZnajdzWpis(document, databaseName);
                return entry == null ? null : new HashSet<int>(entry.ZnaneNumeryDokumentow);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ZapiszZnaneNumeryAsync(string databaseName, IEnumerable<int> aktualneNumery, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("DatabaseName jest wymagany do zapisania baseline'u poczekalni.", nameof(databaseName));
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                PoczekalniaBaselineDocument document = await LoadLockedAsync(cancellationToken);
                document.Companies.RemoveAll(entry => string.Equals(entry.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase));
                document.Companies.Add(new PoczekalniaBaselineEntry
                {
                    DatabaseName = databaseName.Trim(),
                    ZnaneNumeryDokumentow = (aktualneNumery ?? Enumerable.Empty<int>()).Distinct().OrderBy(n => n).ToList(),
                    ZaktualizowanoUtc = DateTimeOffset.UtcNow
                });
                document.Companies = document.Companies
                    .OrderBy(entry => entry.DatabaseName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await SaveLockedAsync(document, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private static PoczekalniaBaselineEntry ZnajdzWpis(PoczekalniaBaselineDocument document, string databaseName)
        {
            return document.Companies.FirstOrDefault(entry =>
                string.Equals(entry.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<PoczekalniaBaselineDocument> LoadLockedAsync(CancellationToken cancellationToken)
        {
            await EnsureFileExistsLockedAsync(cancellationToken);
            string json = await File.ReadAllTextAsync(FilePath, cancellationToken);
            PoczekalniaBaselineDocument document = JsonSerializer.Deserialize<PoczekalniaBaselineDocument>(json, _jsonOptions)
                ?? new PoczekalniaBaselineDocument();
            document.Companies ??= new List<PoczekalniaBaselineEntry>();
            return document;
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

            await SaveLockedAsync(new PoczekalniaBaselineDocument(), cancellationToken);
            _logger.LogInformation("Utworzono nowy plik baseline'u poczekalni: {FilePath}", FilePath);
        }

        private async Task SaveLockedAsync(PoczekalniaBaselineDocument document, CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(document, _jsonOptions);
            string tempFilePath = FilePath + ".tmp";
            await File.WriteAllTextAsync(tempFilePath, json, cancellationToken);
            File.Move(tempFilePath, FilePath, overwrite: true);
        }
    }
}
