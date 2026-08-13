using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NexoBridge.Services
{
    public class NexoBridgeLogReader
    {
        private const int DefaultWindowSeconds = 90;
        private const int MaxWindowSeconds = 3600;
        private const int MaxLogFragmentChars = 200_000;
        private static readonly Regex LogEntryStart = new Regex(
            @"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[[A-Z]{3}\]",
            RegexOptions.Compiled);

        private readonly ILogger<NexoBridgeLogReader> _logger;
        private readonly string _logDirectory;

        public NexoBridgeLogReader(ILogger<NexoBridgeLogReader> logger)
        {
            _logger = logger;
            _logDirectory = Environment.GetEnvironmentVariable("NEXO_BRIDGE_LOG_DIR");
            if (string.IsNullOrWhiteSpace(_logDirectory))
            {
                _logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
            }
        }

        public string ReadWindow(DateTimeOffset? before, int? windowSeconds, string activity)
        {
            DateTimeOffset end = before ?? DateTimeOffset.UtcNow;
            int seconds = Math.Max(1, Math.Min(MaxWindowSeconds, windowSeconds ?? DefaultWindowSeconds));
            DateTimeOffset start = end.AddSeconds(-seconds);

            if (!Directory.Exists(_logDirectory))
            {
                return $"Brak katalogu logów NexoBridge: {_logDirectory}";
            }

            var entries = new List<LogEntry>();
            foreach (string filePath in GetCandidateFiles())
            {
                try
                {
                    entries.AddRange(ReadEntries(filePath, start, end));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LOG ENDPOINT] Nie udało się odczytać pliku logu {Path}", filePath);
                }
            }

            var matching = entries
                .Where(e => e.Timestamp >= start && e.Timestamp <= end)
                .OrderBy(e => e.Timestamp)
                .Select(e => e.Text.TrimEnd())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            if (matching.Count == 0)
            {
                string activityInfo = string.IsNullOrWhiteSpace(activity) ? "" : $" activity='{activity}',";
                return $"Brak wpisów logu dla{activityInfo} okno={start:O}..{end:O}, katalog={_logDirectory}.";
            }

            return TrimToMaxLength(matching);
        }

        // Bez tego capa okno logu (do 3600s) potrafiło rozrosnąć się do megabajtów przy zaspamowanych
        // wpisach (np. seria [ZAŁĄCZNIK DOKUMENT] po kilka KB każdy) i nginx odrzucał cały raport błędu
        // z 413 "too large chunked body" - zgłoszenie nigdy nie docierało do Klasyfikatora. Zachowujemy
        // najnowsze wpisy (najbliższe momentowi błędu), bo są najbardziej istotne dla diagnozy.
        private static string TrimToMaxLength(List<string> matchingOldestFirst)
        {
            string full = string.Join(Environment.NewLine, matchingOldestFirst);
            if (full.Length <= MaxLogFragmentChars)
            {
                return full;
            }

            var kept = new List<string>();
            int length = 0;
            for (int i = matchingOldestFirst.Count - 1; i >= 0; i--)
            {
                string entry = matchingOldestFirst[i];
                int addedLength = entry.Length + Environment.NewLine.Length;
                if (length + addedLength > MaxLogFragmentChars)
                {
                    break;
                }

                kept.Insert(0, entry);
                length += addedLength;
            }

            string header = $"[PRZYCIĘTO: pokazano {kept.Count} z {matchingOldestFirst.Count} wpisów logu (najnowsze), całość miała {full.Length} znaków, limit={MaxLogFragmentChars}]";
            return header + Environment.NewLine + string.Join(Environment.NewLine, kept);
        }

        private IEnumerable<string> GetCandidateFiles()
        {
            return Directory
                .EnumerateFiles(_logDirectory, "nexobridge-*.log", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Contains("attachments-debug", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path)
                .ToList();
        }

        private IEnumerable<LogEntry> ReadEntries(string filePath, DateTimeOffset start, DateTimeOffset end)
        {
            var entries = new List<LogEntry>();
            DateTimeOffset? currentTimestamp = null;
            var currentText = new StringBuilder();

            foreach (string line in ReadLinesShared(filePath))
            {
                Match match = LogEntryStart.Match(line);
                if (match.Success && TryParseTimestamp(match.Groups["timestamp"].Value, out DateTimeOffset timestamp))
                {
                    FlushCurrent(entries, currentTimestamp, currentText, start, end);
                    currentTimestamp = timestamp;
                    currentText.Clear();
                    currentText.AppendLine(line);
                    continue;
                }

                if (currentText.Length > 0)
                {
                    currentText.AppendLine(line);
                }
            }

            FlushCurrent(entries, currentTimestamp, currentText, start, end);
            return entries;
        }

        private IEnumerable<string> ReadLinesShared(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            while (!reader.EndOfStream)
            {
                yield return reader.ReadLine();
            }
        }

        private void FlushCurrent(List<LogEntry> entries, DateTimeOffset? timestamp, StringBuilder text, DateTimeOffset start, DateTimeOffset end)
        {
            if (!timestamp.HasValue || text.Length == 0)
            {
                return;
            }

            if (timestamp.Value >= start && timestamp.Value <= end)
            {
                entries.Add(new LogEntry
                {
                    Timestamp = timestamp.Value,
                    Text = text.ToString()
                });
            }
        }

        private bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
        {
            return DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss.fff zzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp);
        }

        private sealed class LogEntry
        {
            public DateTimeOffset Timestamp { get; set; }
            public string Text { get; set; }
        }
    }
}
