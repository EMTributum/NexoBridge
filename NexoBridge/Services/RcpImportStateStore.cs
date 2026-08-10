using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public sealed class RcpImportStateStore
    {
        private const string FileName = "rcp-import-state.json";

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly ILogger<RcpImportStateStore> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        public RcpImportStateStore(ILogger<RcpImportStateStore> logger)
        {
            _logger = logger;
            FilePath = Path.Combine(AppContext.BaseDirectory, FileName);
        }

        public string FilePath { get; }

        public async Task<RcpImportStateDocument> GetAsync(CancellationToken cancellationToken = default)
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

        public async Task<bool> HasSuccessfulImportAsync(
            string databaseName,
            int periodYear,
            int periodMonth,
            string payloadHash,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                RcpImportStateDocument document = await LoadLockedAsync(cancellationToken);
                return document.Imports.Any(entry =>
                    string.Equals(entry.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase) &&
                    entry.PeriodYear == periodYear &&
                    entry.PeriodMonth == periodMonth &&
                    string.Equals(entry.PayloadHash, payloadHash, StringComparison.Ordinal));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task MarkSuccessfulImportAsync(RcpImportReport report, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(report);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                RcpImportStateDocument document = await LoadLockedAsync(cancellationToken);
                document.Imports.RemoveAll(entry =>
                    string.Equals(entry.DatabaseName, report.DatabaseName, StringComparison.OrdinalIgnoreCase) &&
                    entry.PeriodYear == report.PeriodYear &&
                    entry.PeriodMonth == report.PeriodMonth);

                document.Imports.Add(new RcpImportStateEntry
                {
                    DatabaseName = report.DatabaseName?.Trim(),
                    PeriodYear = report.PeriodYear,
                    PeriodMonth = report.PeriodMonth,
                    PayloadHash = report.PayloadHash?.Trim(),
                    JobId = report.JobId?.Trim(),
                    SourceMode = report.SourceMode?.Trim(),
                    ImportedAtUtc = report.FinishedAtUtc == default ? DateTimeOffset.UtcNow : report.FinishedAtUtc
                });

                document.Imports = document.Imports
                    .OrderBy(entry => entry.DatabaseName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.PeriodYear)
                    .ThenBy(entry => entry.PeriodMonth)
                    .ToList();

                await SaveLockedAsync(document, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<RcpImportStateDocument> LoadLockedAsync(CancellationToken cancellationToken)
        {
            await EnsureFileExistsLockedAsync(cancellationToken);
            string json = await File.ReadAllTextAsync(FilePath, cancellationToken);
            RcpImportStateDocument document = JsonSerializer.Deserialize<RcpImportStateDocument>(json, _jsonOptions)
                ?? new RcpImportStateDocument();
            document.Imports ??= new System.Collections.Generic.List<RcpImportStateEntry>();
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

            await SaveLockedAsync(new RcpImportStateDocument(), cancellationToken);
            _logger.LogInformation("Utworzono nowy plik stanu importu RCP: {FilePath}", FilePath);
        }

        private async Task SaveLockedAsync(RcpImportStateDocument document, CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(document, _jsonOptions);
            string tempFilePath = FilePath + ".tmp";
            await File.WriteAllTextAsync(tempFilePath, json, cancellationToken);
            File.Move(tempFilePath, FilePath, overwrite: true);
        }
    }
}
