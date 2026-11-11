using Microsoft.Data.SqlClient;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    /// <summary>Brak DB_SERVER / DB_READ_USER / DB_READ_PASS albo konto odczytu ma uprawnienia administratora -
    /// endpoint zwraca 503 z komunikatem. Nie ma cofania do DB_USER.</summary>
    public class KadryPodstawyZusConfigurationException : Exception
    {
        public KadryPodstawyZusConfigurationException(string message) : base(message) { }
    }

    /// <summary>Baza nie istnieje albo jest niedostępna - endpoint zwraca 404.</summary>
    public class KadryPodstawyZusNotFoundException : Exception
    {
        public KadryPodstawyZusNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Podstawy składek ZUS pracowników jednej bazy Gratyfikanta za miesiąc - to, co kadrowe przepisują do Excela
    /// (Płace -> Wynagrodzenia -> Składki ZUS pracownika -> Emerytalna -> Podstawa). Na osobę: suma
    /// PodstawaWymiaruSkladekEmerytalnejIRentowej z wypłat (umowy o pracę) list za miesiąc, osobno suma z rachunków,
    /// flaga niezatwierdzonej listy oraz stopień niepełnosprawności i schorzenia specjalne aktywne w miesiącu.
    /// WYŁĄCZNIE SELECT, konto DB_READ_USER / DB_READ_PASS, ApplicationIntent=ReadOnly, bez Sfery.
    /// Sprawdzone w NexoBridgeKonsola (--podstawy-zus) na wszystkich bazach (10.2026).
    /// Do logów trafiają wyłącznie baza, liczby i czasy (nigdy nazwiska).
    ///
    /// Złączenia (ModelDanychContainer.*):
    ///   Wynagrodzenia.ListaWynagrodzen_Id -> ListyWynagrodzen (Miesiac, Zatwierdzona);
    ///   Wynagrodzenia_Wyplata.Id = Wynagrodzenia.Id -> wypłata z umowy o pracę (pozostałe = rachunki);
    ///   Wynagrodzenia.Umowa_Id -> UmowyPracownicze.Pracownik_Id -> PracownicyGr -> Pracownicy.Osoba_Id -> Osoby;
    ///   OkresyNiepelnosprawnosci.PracownikGr_Id -> StopnieNiepelnosprawnosci.KodZUS (0 = brak orzeczenia).
    /// </summary>
    public class KadryPodstawyZusSqlService
    {
        private const int CommandTimeoutSeconds = 60;

        private const string EmployeesSql = @"
            SELECT pg.Id AS EmployeeId, o.Nazwisko AS LastName, o.Imie AS FirstName,
                   SUM(CASE WHEN wy.Id IS NOT NULL THEN w.PodstawaWymiaruSkladekEmerytalnejIRentowej ELSE 0 END) AS ContributionBase,
                   SUM(CASE WHEN wy.Id IS NOT NULL THEN w.Brutto ELSE 0 END) AS Gross,
                   SUM(CASE WHEN wy.Id IS NULL THEN w.PodstawaWymiaruSkladekEmerytalnejIRentowej ELSE 0 END) AS BillsContributionBase,
                   SUM(CASE WHEN wy.Id IS NOT NULL THEN 1 ELSE 0 END) AS PayoutCount,
                   SUM(CASE WHEN wy.Id IS NULL THEN 1 ELSE 0 END) AS BillCount,
                   SUM(CASE WHEN l.Zatwierdzona = 0 THEN 1 ELSE 0 END) AS UnapprovedCount
            FROM ModelDanychContainer.Wynagrodzenia w
            JOIN ModelDanychContainer.ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
            LEFT JOIN ModelDanychContainer.Wynagrodzenia_Wyplata wy ON wy.Id = w.Id
            JOIN ModelDanychContainer.UmowyPracownicze u ON u.Id = w.Umowa_Id
            JOIN ModelDanychContainer.PracownicyGr pg ON pg.Id = u.Pracownik_Id
            JOIN ModelDanychContainer.Pracownicy pr ON pr.Id = pg.Pracownik_Id
            JOIN ModelDanychContainer.Osoby o ON o.Id = pr.Osoba_Id
            WHERE l.Miesiac >= @start AND l.Miesiac < @end
            GROUP BY pg.Id, o.Nazwisko, o.Imie";

        // Okresy orzeczeń aktywne choć jeden dzień miesiąca; przy kilku bierzemy najnowszy (Od).
        private const string DisabilitySql = @"
            SELECT n.PracownikGr_Id AS EmployeeId, s.KodZUS AS Code, n.SchorzeniaSpecjalne AS Special, n.Od AS DateFrom
            FROM ModelDanychContainer.OkresyNiepelnosprawnosci n
            JOIN ModelDanychContainer.StopnieNiepelnosprawnosci s ON s.Id = n.StopienNiepelnosprawnosci_Id
            WHERE n.Od < @end AND (n.Do IS NULL OR n.Do >= @start)";

        // Kody tytułu ubezpieczenia (4 cyfry, np. 0110 = pracownik) z umów wypłat za miesiąc i prawo do emerytury/renty
        // (Pracownicy.PrawoDoEmeryturyRenty_Id, KodZUS 0/1/2). Pełny 6-cyfrowy kod jak w RCA i w Excelu kadrowych =
        // tytuł + prawo do emerytury + stopień niepełnosprawności (np. 011002). Rozróżnia dwa rekordy tej samej osoby.
        private const string InsuranceCodesSql = @"
            SELECT DISTINCT u.Pracownik_Id AS EmployeeId, RTRIM(k.Kod) AS TitleCode, pe.KodZUS AS PensionCode
            FROM ModelDanychContainer.Wynagrodzenia w
            JOIN ModelDanychContainer.ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
            JOIN ModelDanychContainer.Wynagrodzenia_Wyplata wy ON wy.Id = w.Id
            JOIN ModelDanychContainer.UmowyPracownicze u ON u.Id = w.Umowa_Id
            JOIN ModelDanychContainer.KodyTytuluUbezpieczenia k ON k.Id = u.KodTytuluUbezpieczenia_Id
            JOIN ModelDanychContainer.PracownicyGr pg ON pg.Id = u.Pracownik_Id
            JOIN ModelDanychContainer.Pracownicy pr ON pr.Id = pg.Pracownik_Id
            LEFT JOIN ModelDanychContainer.PrawaDoEmeryturyRenty pe ON pe.Id = pr.PrawoDoEmeryturyRenty_Id
            WHERE l.Miesiac >= @start AND l.Miesiac < @end";

        public async Task<KadryPodstawyZusResponse> ReadAsync(string databaseName, int year, int month, CancellationToken cancellationToken)
        {
            GetCredentials(out string server, out string user, out string password);
            string canonical;
            await using (var master = new SqlConnection(BuildConnectionString(server, user, password, "master")))
            {
                await master.OpenAsync(cancellationToken);
                await EnsureReadOnlyLoginAsync(master, user, cancellationToken);
                canonical = await ResolveDatabaseAsync(master, databaseName.Trim(), cancellationToken);
            }

            if (canonical == null)
            {
                throw new KadryPodstawyZusNotFoundException("Baza nie istnieje na serwerze albo nie jest dostępna.");
            }

            var start = new DateTime(year, month, 1);
            var response = new KadryPodstawyZusResponse { DatabaseName = canonical, PeriodYear = year, PeriodMonth = month };

            await using var connection = new SqlConnection(BuildConnectionString(server, user, password, canonical));
            await connection.OpenAsync(cancellationToken);

            var disability = new Dictionary<int, (string Code, bool Special, DateTime From)>();
            await ReadAsync(connection, DisabilitySql, start, reader =>
            {
                string code = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                if (string.IsNullOrEmpty(code) || code == "0")
                {
                    return;
                }
                int employeeId = reader.GetInt32(0);
                DateTime from = reader.GetDateTime(3);
                if (!disability.TryGetValue(employeeId, out var current) || from > current.From)
                {
                    disability[employeeId] = (code, reader.GetBoolean(2), from);
                }
            }, cancellationToken);

            var insuranceCodes = new Dictionary<int, SortedSet<string>>();
            await ReadAsync(connection, InsuranceCodesSql, start, reader =>
            {
                int employeeId = reader.GetInt32(0);
                string title = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                if (title.Length == 0)
                {
                    return;
                }
                string pension = reader.IsDBNull(2) ? "0" : reader.GetString(2).Trim();
                string degreeCode = disability.TryGetValue(employeeId, out var d) ? d.Code : "0";
                if (!insuranceCodes.TryGetValue(employeeId, out var codes))
                {
                    insuranceCodes[employeeId] = codes = new SortedSet<string>(StringComparer.Ordinal);
                }
                codes.Add(title + (pension.Length == 0 ? "0" : pension) + degreeCode);
            }, cancellationToken);

            await ReadAsync(connection, EmployeesSql, start, reader =>
            {
                int employeeId = reader.GetInt32(0);
                bool disabled = disability.TryGetValue(employeeId, out var degree);
                response.Employees.Add(new KadryPodstawyZusEmployee
                {
                    EmployeeId = employeeId,
                    LastName = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
                    FirstName = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim(),
                    ContributionBase = reader.GetDecimal(3),
                    Gross = reader.GetDecimal(4),
                    BillsContributionBase = reader.GetDecimal(5),
                    PayoutCount = reader.GetInt32(6),
                    BillCount = reader.GetInt32(7),
                    HasUnapproved = reader.GetInt32(8) > 0,
                    DisabilityCode = disabled ? degree.Code : null,
                    SpecialConditions = disabled && degree.Special,
                    InsuranceCodes = insuranceCodes.TryGetValue(employeeId, out var codes) ? codes.ToList() : new List<string>(),
                });
            }, cancellationToken);

            return response;
        }

        public static bool IsValidDatabaseName(string databaseName) => GratyfikantReadOnlyAccess.IsValidDatabaseName(databaseName);

        private static async Task ReadAsync(SqlConnection connection, string sql, DateTime start, Action<SqlDataReader> onRow, CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add("@start", System.Data.SqlDbType.DateTime2).Value = start;
            command.Parameters.Add("@end", System.Data.SqlDbType.DateTime2).Value = start.AddMonths(1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                onRow(reader);
            }
        }

        private static Exception ConfigurationError(string message) => new KadryPodstawyZusConfigurationException(message);

        private static void GetCredentials(out string server, out string user, out string password) =>
            GratyfikantReadOnlyAccess.GetCredentials(ConfigurationError, out server, out user, out password);

        private static string BuildConnectionString(string server, string user, string password, string database) =>
            GratyfikantReadOnlyAccess.BuildConnectionString(server, user, password, database, "NexoBridge-KadryPodstawyZus");

        private static Task EnsureReadOnlyLoginAsync(SqlConnection connection, string expectedUser, CancellationToken cancellationToken) =>
            GratyfikantReadOnlyAccess.EnsureReadOnlyLoginAsync(connection, expectedUser, "podstaw składek", ConfigurationError, cancellationToken);

        private static Task<string> ResolveDatabaseAsync(SqlConnection master, string databaseName, CancellationToken cancellationToken) =>
            GratyfikantReadOnlyAccess.ResolveDatabaseAsync(master, databaseName, cancellationToken);
    }
}
