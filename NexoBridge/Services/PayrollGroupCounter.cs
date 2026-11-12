using InsERT.Moria.OperacjeZewnetrzne;
using Microsoft.Data.SqlClient;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    internal enum PayrollDocumentKind
    {
        Unknown,
        Payout,
        ContractBill,
    }

    /// <summary>
    /// Odczytuje grupę z końcowego nawiasu tytułu listy wynagrodzeń i liczy wypłaty/rachunki należące
    /// do grup wybranych w panelu billingu. Konfiguracja klienta nie żyje tutaj: NexoBridge dostaje
    /// wyłącznie listę wybranych kluczy w żądaniu. Brak listy zachowuje standardowy licznik Nexo.
    /// </summary>
    internal static class PayrollGroupCounter
    {
        public const string UntaggedGroupKey = "__untagged__";
        private const int CommandTimeoutSeconds = 60;
        private static readonly Regex SuffixGroup = new Regex(
            @"\((?<group>[^()]*)\)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private const string RowsSql = @"
            SELECT l.Miesiac AS ListMonth,
                   l.Tytul AS ListTitle,
                   CASE WHEN wy.Id IS NOT NULL AND r.Id IS NULL THEN 'payout'
                        WHEN r.Id IS NOT NULL AND wy.Id IS NULL THEN 'contract_bill'
                        ELSE 'unknown' END AS Kind,
                   COUNT_BIG(*) AS ItemCount
            FROM ModelDanychContainer.Wynagrodzenia w
            JOIN ModelDanychContainer.ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
            LEFT JOIN ModelDanychContainer.Wynagrodzenia_Wyplata wy ON wy.Id = w.Id
            LEFT JOIN ModelDanychContainer.Wynagrodzenia_RachunekDoUmowyPracowniczej r ON r.Id = w.Id
            WHERE l.Miesiac >= @periodStart AND l.Miesiac < @periodEnd
            GROUP BY l.Miesiac, l.Tytul,
                     CASE WHEN wy.Id IS NOT NULL AND r.Id IS NULL THEN 'payout'
                          WHEN r.Id IS NOT NULL AND wy.Id IS NULL THEN 'contract_bill'
                          ELSE 'unknown' END";

        private sealed record PayrollGroupRow(DateTime ListMonth, string ListTitle, PayrollDocumentKind Kind, int Count);

        private sealed class MutableOption
        {
            public string Key { get; init; }
            public string Label { get; set; }
            public int PayoutCount { get; set; }
            public int ContractBillCount { get; set; }
            public int HistoryCount { get; set; }
            public DateTime? LastSeenAt { get; set; }
        }

        public static string NormalizeGroupKey(string value)
        {
            if (string.Equals(value, UntaggedGroupKey, StringComparison.Ordinal))
            {
                return UntaggedGroupKey;
            }

            string collapsed = CollapseWhitespace(value).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            return collapsed.Length <= 80 ? collapsed : collapsed.Substring(0, 80);
        }

        public static (string Key, string Label) ExtractGroup(string listTitle)
        {
            Match match = SuffixGroup.Match(listTitle ?? string.Empty);
            if (!match.Success)
            {
                return (UntaggedGroupKey, "Bez oznaczenia");
            }

            string label = CollapseWhitespace(match.Groups["group"].Value);
            string key = NormalizeGroupKey(label);
            return string.IsNullOrWhiteSpace(key)
                ? (UntaggedGroupKey, "Bez oznaczenia")
                : (key, label);
        }

        public static PayrollDocumentKind DetectCounterKind(ILicznikObiektow counter)
        {
            string sql = NormalizeSql(counter?.ZliczSqlCommandString());
            if (string.IsNullOrWhiteSpace(sql))
            {
                return PayrollDocumentKind.Unknown;
            }

            if ((sql.Contains("RACHUN") && sql.Contains("UMOW")) || sql.Contains("CONTRACTBILL"))
            {
                return PayrollDocumentKind.ContractBill;
            }
            if (sql.Contains("WYPLAT") || sql.Contains("PAYOUT"))
            {
                return PayrollDocumentKind.Payout;
            }
            return PayrollDocumentKind.Unknown;
        }

        public static int CountFiltered(
            string databaseName,
            PayrollDocumentKind kind,
            IEnumerable<string> includedGroups,
            DateTime periodStart,
            DateTime periodEnd)
        {
            HashSet<string> selected = NormalizeSelection(includedGroups);
            if (selected.Count == 0)
            {
                throw new InvalidOperationException("Filtrowane naliczanie kadr wymaga co najmniej jednej grupy.");
            }
            if (kind == PayrollDocumentKind.Unknown)
            {
                throw new InvalidOperationException("Nie udało się rozpoznać rodzaju licznika kadrowego Nexo.");
            }

            List<PayrollGroupRow> rows = ReadRowsAsync(
                databaseName, periodStart, periodEnd, CancellationToken.None).GetAwaiter().GetResult();
            return rows
                .Where(row => row.Kind == kind && selected.Contains(ExtractGroup(row.ListTitle).Key))
                .Sum(row => row.Count);
        }

        public static async Task<PayrollGroupsResponse> DiscoverAsync(
            PayrollGroupsRequest request,
            CancellationToken cancellationToken)
        {
            DateTime currentStart = new DateTime(request.PeriodYear, request.PeriodMonth, 1);
            DateTime historyStart = currentStart.AddMonths(-(request.MonthsBack - 1));
            DateTime periodEnd = currentStart.AddMonths(1);
            List<PayrollGroupRow> rows = await ReadRowsAsync(
                request.DatabaseName, historyStart, periodEnd, cancellationToken);

            var options = new Dictionary<string, MutableOption>(StringComparer.Ordinal);
            foreach (PayrollGroupRow row in rows.Where(row => row.Kind != PayrollDocumentKind.Unknown))
            {
                (string key, string label) = ExtractGroup(row.ListTitle);
                if (!options.TryGetValue(key, out MutableOption option))
                {
                    option = new MutableOption { Key = key, Label = label };
                    options[key] = option;
                }

                option.HistoryCount += row.Count;
                option.LastSeenAt = !option.LastSeenAt.HasValue || row.ListMonth > option.LastSeenAt.Value
                    ? row.ListMonth
                    : option.LastSeenAt;

                bool currentPeriod = row.ListMonth.Year == request.PeriodYear && row.ListMonth.Month == request.PeriodMonth;
                if (currentPeriod && row.Kind == PayrollDocumentKind.Payout)
                {
                    option.PayoutCount += row.Count;
                }
                else if (currentPeriod && row.Kind == PayrollDocumentKind.ContractBill)
                {
                    option.ContractBillCount += row.Count;
                }
            }

            return new PayrollGroupsResponse
            {
                DatabaseName = request.DatabaseName,
                PeriodYear = request.PeriodYear,
                PeriodMonth = request.PeriodMonth,
                MonthsBack = request.MonthsBack,
                Groups = options.Values
                    .OrderByDescending(option => option.PayoutCount + option.ContractBillCount)
                    .ThenBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
                    .Select(option => new PayrollGroupOptionDto
                    {
                        Key = option.Key,
                        Label = option.Label,
                        PayoutCount = option.PayoutCount,
                        ContractBillCount = option.ContractBillCount,
                        HistoryCount = option.HistoryCount,
                        LastSeenAt = option.LastSeenAt,
                    })
                    .ToList(),
            };
        }

        private static async Task<List<PayrollGroupRow>> ReadRowsAsync(
            string databaseName,
            DateTime periodStart,
            DateTime periodEnd,
            CancellationToken cancellationToken)
        {
            if (!GratyfikantReadOnlyAccess.IsValidDatabaseName(databaseName))
            {
                throw new ArgumentException("Niepoprawna nazwa bazy Gratyfikanta.", nameof(databaseName));
            }

            GratyfikantReadOnlyAccess.GetCredentials(
                message => new OfficeReportsConfigurationException(message),
                out string server,
                out string user,
                out string password);

            string connectionString = GratyfikantReadOnlyAccess.BuildConnectionString(
                server, user, password, databaseName.Trim(), "NexoBridge-PayrollGroups");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await GratyfikantReadOnlyAccess.EnsureReadOnlyLoginAsync(
                connection,
                user,
                "grup list wynagrodzeń",
                message => new OfficeReportsConfigurationException(message),
                cancellationToken);

            await using var command = new SqlCommand(RowsSql, connection) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add("@periodStart", SqlDbType.DateTime2).Value = periodStart;
            command.Parameters.Add("@periodEnd", SqlDbType.DateTime2).Value = periodEnd;

            var rows = new List<PayrollGroupRow>();
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string kindValue = reader.GetString(reader.GetOrdinal("Kind"));
                PayrollDocumentKind kind = kindValue == "payout"
                    ? PayrollDocumentKind.Payout
                    : kindValue == "contract_bill"
                        ? PayrollDocumentKind.ContractBill
                        : PayrollDocumentKind.Unknown;
                long count = reader.GetInt64(reader.GetOrdinal("ItemCount"));
                rows.Add(new PayrollGroupRow(
                    reader.GetDateTime(reader.GetOrdinal("ListMonth")),
                    reader.IsDBNull(reader.GetOrdinal("ListTitle")) ? null : reader.GetString(reader.GetOrdinal("ListTitle")),
                    kind,
                    checked((int)count)));
            }
            return rows;
        }

        private static HashSet<string> NormalizeSelection(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Select(NormalizeGroupKey)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string CollapseWhitespace(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string NormalizeSql(string value)
        {
            return (value ?? string.Empty)
                .Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .Aggregate(new StringBuilder(), (builder, c) => builder.Append(c))
                .ToString()
                .ToUpperInvariant();
        }
    }
}
