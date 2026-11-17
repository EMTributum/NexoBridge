using Microsoft.Data.SqlClient;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Data;
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
    /// Odczytuje grupę z tytułu listy wynagrodzeń (zawartość końcowego nawiasu, a bez nawiasu CAŁY tytuł listy;
    /// "Bez oznaczenia" tylko dla pustego tytułu/nawiasu) i liczy wypłaty/rachunki należące
    /// do grup wybranych w panelu billingu - wg miesiąca listy i tą samą miarą co klient bez filtra
    /// (wypłaty = różne osoby, rachunki = dokumenty; patrz PayrollListMonthCounter), także w podglądzie grup
    /// (DiscoverAsync) - liczby w panelu zgadzają się z fakturą. Konfiguracja klienta
    /// nie żyje tutaj: NexoBridge dostaje wyłącznie listę wybranych kluczy w żądaniu. Brak listy grup
    /// oznacza liczenie bez filtra (PayrollListMonthCounter.Count).
    /// </summary>
    internal static class PayrollGroupCounter
    {
        public const string UntaggedGroupKey = "__untagged__";
        private const int CommandTimeoutSeconds = 60;
        private static readonly Regex SuffixGroup = new Regex(
            @"\((?<group>[^()]*)\)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Myślniki (kategoria Unicode "Pd": łącznik, półpauza, pauza i pokrewne) wraz ze spacjami wokół - w kluczu grupy zawsze jako "-".
        private static readonly Regex DashWithSpaces = new Regex(
            @"\s*\p{Pd}\s*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private const int MaxGroupKeyLength = 80;
        private const string UntaggedGroupLabel = "Bez oznaczenia";

        // Wiersze (miesiąc listy, tytuł listy, rodzaj, pracownik dla wypłat, liczba dokumentów). Pracownik pozwala
        // liczyć wypłaty w OSOBACH (ten sam pracownik na dwóch listach to jedna wypłata - jak na fakturach), zarówno
        // przy liczeniu z filtrem grup, jak i w podglądzie grup.
        private const string RowsSql = @"
            SELECT l.Miesiac AS ListMonth,
                   l.Tytul AS ListTitle,
                   CASE WHEN wy.Id IS NOT NULL AND r.Id IS NULL THEN 'payout'
                        WHEN r.Id IS NOT NULL AND wy.Id IS NULL THEN 'contract_bill'
                        ELSE 'unknown' END AS Kind,
                   CASE WHEN wy.Id IS NOT NULL THEN u.Pracownik_Id END AS PersonId,
                   COUNT_BIG(*) AS ItemCount
            FROM ModelDanychContainer.Wynagrodzenia w
            JOIN ModelDanychContainer.ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
            LEFT JOIN ModelDanychContainer.Wynagrodzenia_Wyplata wy ON wy.Id = w.Id
            LEFT JOIN ModelDanychContainer.Wynagrodzenia_RachunekDoUmowyPracowniczej r ON r.Id = w.Id
            LEFT JOIN ModelDanychContainer.UmowyPracownicze u ON u.Id = w.Umowa_Id
            WHERE l.Miesiac >= @periodStart AND l.Miesiac < @periodEnd
            GROUP BY l.Miesiac, l.Tytul,
                     CASE WHEN wy.Id IS NOT NULL AND r.Id IS NULL THEN 'payout'
                          WHEN r.Id IS NOT NULL AND wy.Id IS NULL THEN 'contract_bill'
                          ELSE 'unknown' END,
                     CASE WHEN wy.Id IS NOT NULL THEN u.Pracownik_Id END";

        internal sealed record PayrollGroupRow(DateTime ListMonth, string ListTitle, PayrollDocumentKind Kind, int? PersonId, int Count);

        private sealed class MutableOption
        {
            public string Key { get; init; }
            public string Label { get; set; }
            public int PayoutCount { get; set; }
            public int ContractBillCount { get; set; }
            public int HistoryCount { get; set; }
            public DateTime? LastSeenAt { get; set; }
        }

        /// <summary>Klucz grupy: małe litery, scalone białe znaki, ujednolicone myślniki (-, –, —) i spacje wokół nich,
        /// np. "Prowizja Pawłyk - Handlowcy" i "prowizja Pawłyk-Handlowcy" to ten sam klucz. Ta sama zasada musi
        /// obowiązywać po stronie klasyfikatora (PayrollGroupPolicyRequest).</summary>
        public static string NormalizeGroupKey(string value)
        {
            if (string.Equals(value, UntaggedGroupKey, StringComparison.Ordinal))
            {
                return UntaggedGroupKey;
            }

            string collapsed = CollapseWhitespace((value ?? string.Empty).Normalize(NormalizationForm.FormKC));
            string key = DashWithSpaces.Replace(collapsed, "-").ToLowerInvariant();
            return key.Length <= MaxGroupKeyLength ? key : key.Substring(0, MaxGroupKeyLength).TrimEnd();
        }

        /// <summary>Grupa listy: zawartość końcowego nawiasu tytułu (np. "Lista płac (trenerzy)" -> "trenerzy"),
        /// a gdy nawiasu nie ma - CAŁY tytuł listy (np. "Robotnicy"). Pusty tytuł albo pusty nawias to "Bez oznaczenia".
        /// Etykieta zachowuje pisownię z tytułu, klucz jest znormalizowany (NormalizeGroupKey).</summary>
        public static (string Key, string Label) ExtractGroup(string listTitle)
        {
            string title = CollapseWhitespace(listTitle);
            if (title.Length == 0)
            {
                return (UntaggedGroupKey, UntaggedGroupLabel);
            }

            Match match = SuffixGroup.Match(title);
            string label = match.Success ? CollapseWhitespace(match.Groups["group"].Value) : title;
            string key = NormalizeGroupKey(label);
            return string.IsNullOrWhiteSpace(key)
                ? (UntaggedGroupKey, UntaggedGroupLabel)
                : (key, label);
        }

        /// <summary>Ilość z list należących do wybranych grup, wg miesiąca listy: wypłaty = różne osoby, rachunki = dokumenty
        /// (ta sama miara co PayrollListMonthCounter.Count dla klienta bez filtra).</summary>
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
            return PayrollListMonthCounter.CountRows(rows.Select(ToCountRow), kind, selected);
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

            return new PayrollGroupsResponse
            {
                DatabaseName = request.DatabaseName,
                PeriodYear = request.PeriodYear,
                PeriodMonth = request.PeriodMonth,
                MonthsBack = request.MonthsBack,
                Groups = BuildOptions(rows, request.PeriodYear, request.PeriodMonth),
            };
        }

        /// <summary>Opcje grup do panelu billingu. Liczby w tej samej mierze co faktura (PayrollListMonthCounter):
        /// wypłaty = różne osoby w miesiącu, rachunki = dokumenty; HistoryCount = suma miesięcznych wartości.</summary>
        internal static List<PayrollGroupOptionDto> BuildOptions(IEnumerable<PayrollGroupRow> rows, int periodYear, int periodMonth)
        {
            var options = new Dictionary<string, MutableOption>(StringComparer.Ordinal);
            var known = rows
                .Where(row => row.Kind != PayrollDocumentKind.Unknown)
                .Select(row => (Row: row, Group: ExtractGroup(row.ListTitle)))
                .ToList();

            foreach (var groupRows in known.GroupBy(item => item.Group.Key, StringComparer.Ordinal))
            {
                // Etykieta = pisownia z najnowszego miesiąca (przy remisie porządek alfabetyczny - wynik nie zależy od kolejności wierszy z SQL).
                string label = groupRows
                    .OrderByDescending(item => item.Row.ListMonth)
                    .ThenBy(item => item.Group.Label, StringComparer.Ordinal)
                    .First().Group.Label;
                var option = new MutableOption { Key = groupRows.Key, Label = label };
                options[groupRows.Key] = option;
                var onlyThisGroup = new HashSet<string>(StringComparer.Ordinal) { groupRows.Key };

                foreach (var monthKind in groupRows.GroupBy(item => (item.Row.ListMonth, item.Row.Kind)))
                {
                    (DateTime listMonth, PayrollDocumentKind kind) = monthKind.Key;
                    int measure = PayrollListMonthCounter.CountRows(
                        monthKind.Select(item => ToCountRow(item.Row)), kind, onlyThisGroup);

                    option.HistoryCount += measure;
                    option.LastSeenAt = !option.LastSeenAt.HasValue || listMonth > option.LastSeenAt.Value
                        ? listMonth
                        : option.LastSeenAt;

                    bool currentPeriod = listMonth.Year == periodYear && listMonth.Month == periodMonth;
                    if (currentPeriod && kind == PayrollDocumentKind.Payout)
                    {
                        option.PayoutCount += measure;
                    }
                    else if (currentPeriod && kind == PayrollDocumentKind.ContractBill)
                    {
                        option.ContractBillCount += measure;
                    }
                }
            }

            return options.Values
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
                .ToList();
        }

        private static PayrollListMonthCounter.PayrollCountRow ToCountRow(PayrollGroupRow row)
            => new PayrollListMonthCounter.PayrollCountRow(row.ListTitle, row.Kind, row.PersonId, row.Count);

        private static async Task<List<PayrollGroupRow>> ReadRowsAsync(
            string databaseName,
            DateTime periodStart,
            DateTime periodEnd,
            CancellationToken cancellationToken)
        {
            await using SqlConnection connection = await OpenReadOnlyConnectionAsync(databaseName, cancellationToken);

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
                int personOrdinal = reader.GetOrdinal("PersonId");
                rows.Add(new PayrollGroupRow(
                    reader.GetDateTime(reader.GetOrdinal("ListMonth")),
                    reader.IsDBNull(reader.GetOrdinal("ListTitle")) ? null : reader.GetString(reader.GetOrdinal("ListTitle")),
                    kind,
                    reader.IsDBNull(personOrdinal) ? null : Convert.ToInt32(reader.GetValue(personOrdinal)),
                    checked((int)count)));
            }
            return rows;
        }

        private static Task<SqlConnection> OpenReadOnlyConnectionAsync(string databaseName, CancellationToken cancellationToken)
            => GratyfikantReadOnlyAccess.OpenReadOnlyConnectionAsync(
                databaseName,
                "NexoBridge-PayrollGroups",
                "grup list wynagrodzeń",
                message => new OfficeReportsConfigurationException(message),
                cancellationToken);

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
    }
}
