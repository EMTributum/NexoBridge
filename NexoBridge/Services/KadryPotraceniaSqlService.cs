using Microsoft.Data.SqlClient;
using NexoBridge.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    /// <summary>Brak DB_SERVER / DB_READ_USER / DB_READ_PASS albo konto odczytu ma uprawnienia administratora -
    /// endpoint zwraca 503 z komunikatem. Nie ma cofania do DB_USER.</summary>
    public class KadryPotraceniaConfigurationException : Exception
    {
        public KadryPotraceniaConfigurationException(string message) : base(message) { }
    }

    /// <summary>Nie znaleziono bazy albo potrącenia - endpoint zwraca 404.</summary>
    public class KadryPotraceniaNotFoundException : Exception
    {
        public KadryPotraceniaNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Odczyt potrąceń komorniczych (i danych do pisma do komornika / urzędu skarbowego) bezpośrednio SQL-em z baz
    /// Gratyfikanta (Nexo_*). WYŁĄCZNIE SELECT, konto DB_READ_USER / DB_READ_PASS, ApplicationIntent=ReadOnly, bez Sfery.
    /// Logika przeniesiona z prototypu NexoBridgeKonsola (Program.PotraceniaRaport.cs, --potracenia-raport) - tam sprawdzona.
    /// Różnica: brutto z 3 poprzednich miesięcy liczone tylko z ZATWIERDZONYCH list płac / rachunków.
    /// Do logów trafiają wyłącznie baza, id, liczby i czasy (nigdy PESEL, adresy, nazwiska).
    ///
    /// Ścieżki złączeń (ModelDanychContainer.*):
    ///   organ:      PotraceniaKomornicze.Komornik_Id -> Komornicy.Firma_Id -> Firmy -> Podmioty.NIP; adres: Adresy_AdresPodmiotu ->
    ///               Adresy -> AdresySzczegoly; konto: CentraGromadzeniaFinansow_RachunekBankowy.Wlasciciel_Id = Podmioty.Id
    ///   pracownik:  PotraceniaKomornicze.PracownikGr_Id -> PracownicyGr.Pracownik_Id -> Pracownicy.Osoba_Id -> Osoby
    ///   pracodawca: Firmy z __MdmDiscriminator__='MojaFirma'
    ///   umowa:      UmowyPracownicze + _UmowaPracowniczaGr (aneksy: UmowaDlaKtorejAneks_Id / UmowaGlowna_Id), etat, stanowisko, składniki
    ///   brutto:     Wynagrodzenia (Umowa_Id -> UmowyPracownicze.Pracownik_Id) + ListyWynagrodzen.Miesiac / Zatwierdzona
    /// Enumy (InsERT.Moria.API): RodzajPotraceniaKomorniczego 0..4, SposobPotracania 0..2, TypUmowyPracowniczej 1 o pracę / 2 cywilnoprawna.
    /// </summary>
    public class KadryPotraceniaSqlService
    {
        public const int MaxClientDatabases = 400;
        private const int CommandTimeoutSeconds = 120;
        private const string NexoDatabasePrefix = GratyfikantReadOnlyAccess.NexoDatabasePrefix;
        private const string ReadWhat = "potrąceń";
        private const string Schema = "ModelDanychContainer.";

        private static readonly string[] MonthNames =
        {
            "styczeń", "luty", "marzec", "kwiecień", "maj", "czerwiec",
            "lipiec", "sierpień", "wrzesień", "październik", "listopad", "grudzień",
        };

        private static readonly Dictionary<int, string> RodzajTekst = new()
        {
            [0] = "wykonawcze alimentacyjne", [1] = "wykonawcze inne", [2] = "spłata zaliczki", [3] = "kara pieniężna", [4] = "inne",
        };

        private static readonly Dictionary<int, string> SposobTekst = new()
        {
            [0] = "do wyzerowania salda", [1] = "stała rata", [2] = "stała rata do wyzerowania salda",
        };

        private static readonly Regex UrzadSkarbowyRegex = new(@"urz[ąa]d\w*\s+skarbow|naczelnik", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SygnaturaSeeRegex = new(@"\bSEE\b", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex ForbiddenSqlRegex = new(
            @"\b(INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|DROP|ALTER|CREATE|TRUNCATE|GRANT|REVOKE|INTO|DBCC|BACKUP|RESTORE|SHUTDOWN|KILL)\b|;",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] ListKeys =
        {
            "potracenie_id", "nazwa", "sygnatura_sprawy", "rodzaj_potracenia_kod", "rodzaj_potracenia", "sposob_potracania_kod", "sposob_potracania",
            "suma_do_potracenia", "kwota_potracenia_miesiecznego", "procent_wynagrodzenia_do_potracenia", "data_od", "data_do",
            "zawieszone", "anulowane", "anulowane_data_od", "organ_nazwa", "rodzaj_organu",
            "pracownik_id", "pracownik_imie", "pracownik_nazwisko", "umowa_rodzaj", "umowa_typ_prawny", "typ_wynagrodzenia",
            "aktywne_w_miesiacu", "nowe_w_miesiacu", "zatrudniony_w_miesiacu", "splata_miesiac_kwota", "braki_danych",
        };

        private sealed class Address
        {
            public string Typ, Pelny, Ulica, NrDomu, NrLokalu, Kod, Miejscowosc;
        }

        // ------------------------------------------------------------------------------------------------
        // Publiczne API
        // ------------------------------------------------------------------------------------------------

        /// <summary>Lista wszystkich potrąceń każdej z podanych baz (z flagami aktywne/nowe) bez PESEL i adresów.
        /// Błąd jednej bazy daje status FAILED dla niej i nie przerywa pozostałych.</summary>
        public async Task<KadryPotraceniaListResponse> ReadListAsync(
            int year, int month, IReadOnlyList<KadryClientDatabase> databases, CancellationToken cancellationToken)
        {
            var response = new KadryPotraceniaListResponse { PeriodYear = year, PeriodMonth = month };
            DateTime monthStart = new(year, month, 1);
            GetCredentials(out string server, out string user, out string password);

            await using var master = new SqlConnection(BuildConnectionString(server, user, password, "master"));
            await master.OpenAsync(cancellationToken);
            await EnsureReadOnlyLoginAsync(master, user, cancellationToken);

            foreach (var client in databases)
            {
                var item = new KadryPotraceniaListItem { Nip = client?.Nip, DatabaseName = client?.DatabaseName };
                // Nazwa z żądania trafia do logu dopiero po walidacji.
                string logDatabase = IsValidDatabaseName(client?.DatabaseName) ? client.DatabaseName.Trim() : "(niepoprawna nazwa)";
                response.Items.Add(item);
                var watch = Stopwatch.StartNew();
                try
                {
                    if (!IsValidDatabaseName(client?.DatabaseName))
                    {
                        throw new KadryPotraceniaNotFoundException($"Niepoprawna nazwa bazy (oczekiwano technicznej nazwy zaczynającej się od '{NexoDatabasePrefix}').");
                    }

                    string canonical = await ResolveDatabaseAsync(master, client.DatabaseName.Trim(), cancellationToken);
                    if (canonical == null)
                    {
                        throw new KadryPotraceniaNotFoundException("Baza nie istnieje na serwerze albo nie jest dostępna.");
                    }

                    var records = await CollectAsync(server, user, password, canonical, monthStart, null, item.Warnings, cancellationToken);
                    item.Entries = records.Select(ToListEntry).ToList();
                    item.Status = item.Entries.Count == 0 ? KadryPotraceniaStatus.NoDeductions : KadryPotraceniaStatus.Ok;
                    Log.Information(
                        "Odczyt potrąceń komorniczych (Baza: {Database}, Okres: {Period}): potrąceń {Count}, aktywnych {Active}, ostrzeżeń {Warnings}, {ElapsedMs} ms",
                        canonical, $"{year}-{month:00}", item.Entries.Count, records.Count(r => Flag(r, "aktywne_w_miesiacu")), item.Warnings.Count, watch.ElapsedMilliseconds);
                }
                catch (KadryPotraceniaConfigurationException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (KadryPotraceniaNotFoundException ex)
                {
                    item.Status = KadryPotraceniaStatus.Failed;
                    item.Error = ex.Message;
                    item.Entries = new();
                    Log.Warning("Odczyt potrąceń komorniczych nie powiódł się (Baza: {Database}, Okres: {Period}): {Message}",
                        logDatabase, $"{year}-{month:00}", ex.Message);
                }
                catch (Exception ex)
                {
                    // Do odpowiedzi tylko stały tekst (+ numer SQL); pełny komunikat wyłącznie w logu serwera.
                    item.Status = KadryPotraceniaStatus.Failed;
                    item.Error = ex.GetBaseException() is SqlException sql
                        ? $"Błąd bazy danych podczas odczytu potrąceń (SQL {sql.Number})."
                        : "Nie udało się odczytać potrąceń z tej bazy (szczegóły w logu serwera).";
                    item.Entries = new();
                    Log.Warning(ex, "Odczyt potrąceń komorniczych nie powiódł się (Baza: {Database}, Okres: {Period}): {Message}",
                        logDatabase, $"{year}-{month:00}", ex.GetBaseException().Message);
                }
            }

            return response;
        }

        /// <summary>Pełne dane jednego potrącenia (z PESEL, adresami, umową i brutto za 3 miesiące) do wygenerowania pisma.</summary>
        public async Task<KadryPotraceniaDetailsResponse> ReadDetailsAsync(
            string databaseName, int potracenieId, int year, int month, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            DateTime monthStart = new(year, month, 1);
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
                throw new KadryPotraceniaNotFoundException($"Nie można otworzyć bazy '{databaseName}'. Sprawdź techniczną nazwę bazy nexo.");
            }

            var warnings = new List<string>();
            var records = await CollectAsync(server, user, password, canonical, monthStart, potracenieId, warnings, cancellationToken);
            var rec = records.FirstOrDefault();
            if (rec == null)
            {
                throw new KadryPotraceniaNotFoundException($"Nie znaleziono potrącenia komorniczego o id {potracenieId} w bazie '{canonical}'.");
            }

            Log.Information("Odczyt szczegółów potrącenia komorniczego (Baza: {Database}, Id: {PotracenieId}, Okres: {Period}, ostrzeżeń {Warnings}, {ElapsedMs} ms)",
                canonical, potracenieId, $"{year}-{month:00}", warnings.Count, watch.ElapsedMilliseconds);
            return new KadryPotraceniaDetailsResponse { Entry = ToDetailsEntry(rec) };
        }

        /// <summary>Nazwa bazy: niepusta, do 128 znaków, bez znaków sterujących, zaczyna się od "Nexo_".</summary>
        public static bool IsValidDatabaseName(string databaseName) => GratyfikantReadOnlyAccess.IsValidDatabaseName(databaseName);

        // ------------------------------------------------------------------------------------------------
        // Połączenie (tylko konto odczytu) - wspólny kod w GratyfikantReadOnlyAccess
        // ------------------------------------------------------------------------------------------------

        private static Exception ConfigurationError(string message) => new KadryPotraceniaConfigurationException(message);

        private static void GetCredentials(out string server, out string user, out string password) =>
            GratyfikantReadOnlyAccess.GetCredentials(ConfigurationError, out server, out user, out password);

        private static string BuildConnectionString(string server, string user, string password, string database) =>
            GratyfikantReadOnlyAccess.BuildConnectionString(server, user, password, database, "NexoBridge-KadryPotracenia");

        private static Task EnsureReadOnlyLoginAsync(SqlConnection connection, string expectedUser, CancellationToken cancellationToken) =>
            GratyfikantReadOnlyAccess.EnsureReadOnlyLoginAsync(connection, expectedUser, ReadWhat, ConfigurationError, cancellationToken);

        private static Task<string> ResolveDatabaseAsync(SqlConnection master, string databaseName, CancellationToken cancellationToken) =>
            GratyfikantReadOnlyAccess.ResolveDatabaseAsync(master, databaseName, cancellationToken);

        private static async Task<List<Dictionary<string, object>>> QueryAsync(
            SqlConnection connection, string sql, Action<SqlParameterCollection> parameters, CancellationToken cancellationToken)
        {
            string trimmed = (sql ?? string.Empty).Trim();
            if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || ForbiddenSqlRegex.IsMatch(trimmed))
            {
                throw new InvalidOperationException("Odrzucono zapytanie: dozwolone są wyłącznie pojedyncze SELECT-y.");
            }

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
            parameters?.Invoke(command.Parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<Dictionary<string, object>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
            return rows;
        }

        // Listy id w IN (...) składane wyłącznie z liczb całkowitych (IEnumerable<int>) - nie ma w nich tekstu z zewnątrz.
        private static string IdList(IEnumerable<int> ids)
        {
            var list = ids.Distinct().ToList();
            return list.Count == 0 ? "0" : string.Join(",", list);
        }

        // ------------------------------------------------------------------------------------------------
        // Konwersje
        // ------------------------------------------------------------------------------------------------

        private static string Str(object value)
        {
            string s = value switch { null => null, string str => str.Trim(), _ => Convert.ToString(value, CultureInfo.InvariantCulture) };
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        /// <summary>Tekst do jednej linii: końce wierszy i wielokrotne spacje zamienione na pojedynczą spację.</summary>
        private static string OneLine(object value)
        {
            string s = Str(value);
            if (s == null) return null;
            string collapsed = WhitespaceRegex.Replace(s, " ").Trim();
            return collapsed.Length == 0 ? null : collapsed;
        }

        private static int? Int(object value) => value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        private static decimal? Dec(object value) => value == null ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        private static DateTime? Date(object value) => value == null ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        private static bool Bool(object value) => value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        private static string DateText(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private static bool Flag(Dictionary<string, object> rec, string key) => rec.TryGetValue(key, out object v) && v is bool b && b;
        private static object Get(Dictionary<string, object> rec, string key) => rec.TryGetValue(key, out object v) ? v : null;
        private static string Text(Dictionary<string, object> rec, string key) => Get(rec, key) as string;
        private static int Id(Dictionary<string, object> row, string column) => Convert.ToInt32(row[column], CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------------------------------------
        // Zbieranie danych jednej bazy
        // ------------------------------------------------------------------------------------------------

        private static Address PickAddress(Dictionary<int, List<(int ApId, Address Addr)>> map, int? podmiotId, params string[] preferredTypes)
        {
            if (podmiotId == null || !map.TryGetValue(podmiotId.Value, out var list)) return null;
            foreach (string type in preferredTypes)
            {
                var found = list.Where(a => string.Equals(a.Addr.Typ, type, StringComparison.OrdinalIgnoreCase)).OrderByDescending(a => a.ApId).FirstOrDefault();
                if (found.Addr != null) return found.Addr;
            }
            return null;
        }

        /// <summary>Zbiera pełne rekordy potrąceń bazy (płaskie słowniki z PESEL/adresami - wolno je tylko rzutować
        /// przez ToListEntry/ToDetailsEntry). Gdy onlyPotracenieId != null - tylko to jedno potrącenie.</summary>
        private static async Task<List<Dictionary<string, object>>> CollectAsync(
            string server, string user, string password, string database, DateTime monthStart, int? onlyPotracenieId,
            List<string> warnings, CancellationToken ct)
        {
            await using var connection = new SqlConnection(BuildConnectionString(server, user, password, database));
            await connection.OpenAsync(ct);
            await EnsureReadOnlyLoginAsync(connection, user, ct);

            DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1);
            DateTime nextMonth = monthStart.AddMonths(1);
            DateTime from3 = monthStart.AddMonths(-3);
            var records = new List<Dictionary<string, object>>();
            object potIdParam = onlyPotracenieId.HasValue ? onlyPotracenieId.Value : DBNull.Value;

            // a) potrącenia; baza bez tabeli PotraceniaKomornicze (SQL 208) = baza bez potrąceń
            List<Dictionary<string, object>> potracenia;
            try
            {
                potracenia = await QueryAsync(connection, $@"
                    SELECT p.Id, p.Nazwa, p.SygnaturaSprawy, p.RodzajPotracenia, p.SposobPotracania, p.CzestotliwoscPotracania,
                           p.SumaDoPotracenia, p.KwotaPotraceniaMiesiecznego, p.ProcentWynagrodzeniaDoPotracenia, p.ProcentOchronyWynagrodzenia,
                           p.DataOd, p.DataDo, p.Bezterminowo, p.Zawieszone, p.ZawieszoneDataOd, p.ZawieszoneDataDo,
                           p.Anulowane, p.AnulowaneDataOd, p.PrzyczynaAnulowania, p.PracownikGr_Id, p.Komornik_Id
                    FROM {Schema}PotraceniaKomornicze p
                    WHERE (@potId IS NULL OR p.Id = @potId)
                    ORDER BY p.Id",
                    p => p.Add("@potId", System.Data.SqlDbType.Int).Value = potIdParam, ct);
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                return records;
            }
            if (potracenia.Count == 0) return records;

            var splaty = await QueryAsync(connection, $@"
                SELECT s.PotracenieKomornicze_Id, s.Miesiac, s.KwotaPotracenia, s.KwotaPotraceniaZasilku,
                       s.KwotaPotraceniaZRachunkow, s.KwotaPotraceniaZasilkuZRachunkow
                FROM {Schema}SplatyPotracenKomorniczych s
                WHERE (@potId IS NULL OR s.PotracenieKomornicze_Id = @potId)",
                p => p.Add("@potId", System.Data.SqlDbType.Int).Value = potIdParam, ct);

            // d) pracodawca
            var firmy = await QueryAsync(connection, $@"
                SELECT f.Id, f.Nazwa, f.Firma_Podmiot_Id, p.NIP
                FROM {Schema}Firmy f
                LEFT JOIN {Schema}Podmioty p ON p.Id = f.Firma_Podmiot_Id
                WHERE f.__MdmDiscriminator__ = @discriminator
                ORDER BY f.Id",
                p => p.Add("@discriminator", System.Data.SqlDbType.NVarChar, 64).Value = "MojaFirma", ct);
            if (firmy.Count == 0) warnings.Add("Brak własnej firmy (Firmy z dyskryminatorem MojaFirma) - brak danych pracodawcy.");
            if (firmy.Count > 1) warnings.Add($"W bazie jest {firmy.Count} własnych firm - użyto pierwszej (najniższe Id).");
            var firma = firmy.FirstOrDefault();

            // b) organy
            var komornicy = (await QueryAsync(connection, $@"
                SELECT k.Id, f.Nazwa, f.Firma_Podmiot_Id, p.NIP
                FROM {Schema}Komornicy k
                JOIN {Schema}Firmy f ON f.Id = k.Firma_Id
                LEFT JOIN {Schema}Podmioty p ON p.Id = f.Firma_Podmiot_Id", null, ct))
                .ToDictionary(r => Id(r, "Id"));

            // c) pracownicy
            var pracIds = potracenia.Select(p => Id(p, "PracownikGr_Id")).Distinct().ToList();
            var pracownicy = (await QueryAsync(connection, $@"
                SELECT pg.Id AS PracownikGrId, pr.Id AS PracownikId, o.Imie, o.Nazwisko, o.PESEL, o.Podmiot_Id
                FROM {Schema}PracownicyGr pg
                JOIN {Schema}Pracownicy pr ON pr.Id = pg.Pracownik_Id
                JOIN {Schema}Osoby o ON o.Id = pr.Osoba_Id
                WHERE pg.Id IN ({IdList(pracIds)})", null, ct))
                .ToDictionary(r => Id(r, "PracownikGrId"));

            // adresy podmiotów (organy, pracownicy, pracodawca)
            var organPodmioty = komornicy.Values.Select(k => Int(k["Firma_Podmiot_Id"])).Where(i => i != null).Select(i => i.Value).ToList();
            var podmiotIds = new List<int>(organPodmioty);
            podmiotIds.AddRange(pracownicy.Values.Select(k => Int(k["Podmiot_Id"])).Where(i => i != null).Select(i => i.Value));
            if (firma != null && Int(firma["Firma_Podmiot_Id"]) != null) podmiotIds.Add(Int(firma["Firma_Podmiot_Id"]).Value);
            var adresy = new Dictionary<int, List<(int, Address)>>();
            foreach (var row in await QueryAsync(connection, $@"
                SELECT ap.Id AS ApId, ap.Podmiot_Id, ta.Nazwa AS Typ, a.LiniaCalosc, s.Ulica, s.NrDomu, s.NrLokalu, s.KodPocztowy, s.Miejscowosc
                FROM {Schema}Adresy_AdresPodmiotu ap
                JOIN {Schema}Adresy a ON a.Id = ap.Id
                JOIN {Schema}TypyAdresow ta ON ta.Id = a.TypAdresu_Id
                OUTER APPLY (SELECT TOP 1 x.Ulica, x.NrDomu, x.NrLokalu, x.KodPocztowy, x.Miejscowosc
                             FROM {Schema}AdresySzczegoly x WHERE x.Adres_Id = a.Id ORDER BY x.Id DESC) s
                WHERE ap.Podmiot_Id IN ({IdList(podmiotIds)})", null, ct))
            {
                int podmiot = Id(row, "Podmiot_Id");
                if (!adresy.TryGetValue(podmiot, out var list)) adresy[podmiot] = list = new();
                list.Add((Id(row, "ApId"), new Address
                {
                    Typ = Str(row["Typ"]),
                    Pelny = OneLine(row["LiniaCalosc"]),
                    Ulica = Str(row["Ulica"]),
                    NrDomu = Str(row["NrDomu"]),
                    NrLokalu = Str(row["NrLokalu"]),
                    Kod = Str(row["KodPocztowy"]),
                    Miejscowosc = Str(row["Miejscowosc"]),
                }));
            }

            // konta organów
            var konta = await QueryAsync(connection, $@"
                SELECT r.Wlasciciel_Id, r.WlascicielPodstawowego_Id, r.NumerBezSeparatorow, r.Numer, r.Aktywny
                FROM {Schema}CentraGromadzeniaFinansow_RachunekBankowy r
                WHERE r.Wlasciciel_Id IN ({IdList(organPodmioty)})", null, ct);

            // e) umowy pracowników
            var pracownikIds = pracownicy.Values.Select(p => Id(p, "PracownikId")).ToList();
            var umowy = await QueryAsync(connection, $@"
                SELECT u.Id, u.Pracownik_Id, u.Typ, u.JestRozwiazana, u.DataRozwiazania, u.PierwszyDzienObowiazywania, u.OstatniDzienObowiazywania,
                       u.OkresObowiazywania_DataPoczatkowa, u.OkresObowiazywania_DataKoncowa, u.Sygnatura_PelnaSygnatura,
                       g.Kwota, g.UmowaDlaKtorejAneks_Id, g.UmowaGlowna_Id,
                       r.Nazwa AS CzasNazwa, tg.Nazwa AS RodzajNazwa, z.Nazwa AS Zawod
                FROM {Schema}UmowyPracownicze u
                JOIN {Schema}UmowyPracownicze_UmowaPracowniczaGr g ON g.Id = u.Id
                LEFT JOIN {Schema}RodzajeUmowPracowniczychGr r ON r.Id = g.RodzajUmowyPracowniczej_Id
                LEFT JOIN {Schema}TypyUmowPracowniczychGrHistoria th ON th.Id = g.TypUmowyPracowniczej_Id
                LEFT JOIN {Schema}TypyUmowPracowniczychGr tg ON tg.Id = th.TypUmowyPracowniczejDlaKtoregoAktualny_Id
                LEFT JOIN {Schema}Zawody z ON z.Id = g.Zawod_Id
                WHERE u.Pracownik_Id IN ({IdList(pracownikIds)})", null, ct);
            var umowaIds = umowy.Select(u => Id(u, "Id")).ToList();

            var skladniki = await QueryAsync(connection, $@"
                SELECT sup.UmowaPracownicza_Id, sp.Nazwa, v.RodzajWartosci, v.Wartosc,
                       v.OkresObowiazywania_DataPoczatkowa AS Od, v.OkresObowiazywania_DataKoncowa AS Do
                FROM {Schema}SkladnikiPlacoweUmowPracowniczych sup
                JOIN {Schema}SkladnikiPlacowe sp ON sp.Id = sup.SkladnikPlacowy_Id
                LEFT JOIN {Schema}WartosciSkladnikowPlacowychUmowPracowniczych v ON v.SkladnikPlacowyUmowyPracowniczej_Id = sup.Id
                WHERE sup.UmowaPracownicza_Id IN ({IdList(umowaIds)})", null, ct);

            var etaty = await QueryAsync(connection, $@"
                SELECT k.UmowaPracownicza_Id, k.DataOd, k.Lp, w.Licznik, w.Mianownik
                FROM {Schema}KalendarzeWUmowiePracowniczej k
                JOIN {Schema}WymiaryZatrudnien w ON w.Id = k.WymiarZatrudnienia_Id
                WHERE k.UmowaPracownicza_Id IN ({IdList(umowaIds)})", null, ct);

            var stanowiska = await QueryAsync(connection, $@"
                SELECT s.UmowaPracownicza_Id, s.DataOd, s.LP, sp.Nazwa AS Stanowisko, j.PelnaNazwa AS Jednostka
                FROM {Schema}StanowiskaWUmowiePracowniczej s
                LEFT JOIN {Schema}StanowiskaPracownicze sp ON sp.Id = s.Stanowisko_Id
                LEFT JOIN {Schema}JednostkiOrganizacyjneGr j ON j.Id = s.JednostkaOrganizacyjna_Id
                WHERE s.UmowaPracownicza_Id IN ({IdList(umowaIds)})", null, ct);

            // f) wynagrodzenia z list płac / rachunków (zatwierdzone liczone do sumy, niezatwierdzone tylko zliczane)
            var wynagrodzenia = await QueryAsync(connection, $@"
                SELECT u.Pracownik_Id, l.Miesiac, w.Brutto, l.Zatwierdzona
                FROM {Schema}Wynagrodzenia w
                JOIN {Schema}ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
                JOIN {Schema}UmowyPracownicze u ON u.Id = w.Umowa_Id
                WHERE l.Miesiac >= @from AND l.Miesiac < @to AND u.Pracownik_Id IN ({IdList(pracownikIds)})",
                p =>
                {
                    p.Add("@from", System.Data.SqlDbType.DateTime2).Value = from3;
                    p.Add("@to", System.Data.SqlDbType.DateTime2).Value = monthStart;
                }, ct);

            var umowyByPracownik = umowy.GroupBy(u => Id(u, "Pracownik_Id")).ToDictionary(g => g.Key, g => g.ToList());
            var splatyByPotracenie = splaty.GroupBy(s => Id(s, "PotracenieKomornicze_Id")).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var pot in potracenia)
            {
                var rec = new Dictionary<string, object>();
                int potId = Id(pot, "Id");
                rec["potracenie_id"] = potId;

                // a)
                int rodzaj = Id(pot, "RodzajPotracenia");
                int sposob = Id(pot, "SposobPotracania");
                rec["nazwa"] = Str(pot["Nazwa"]);
                rec["sygnatura_sprawy"] = Str(pot["SygnaturaSprawy"]);
                rec["rodzaj_potracenia_kod"] = rodzaj;
                rec["rodzaj_potracenia"] = RodzajTekst.TryGetValue(rodzaj, out string rt) ? rt : $"nieznany ({rodzaj})";
                rec["sposob_potracania_kod"] = sposob;
                rec["sposob_potracania"] = SposobTekst.TryGetValue(sposob, out string st) ? st : $"nieznany ({sposob})";
                rec["czestotliwosc_potracania"] = Int(pot["CzestotliwoscPotracania"]);
                rec["suma_do_potracenia"] = Dec(pot["SumaDoPotracenia"]);
                rec["kwota_potracenia_miesiecznego"] = Dec(pot["KwotaPotraceniaMiesiecznego"]);
                rec["procent_wynagrodzenia_do_potracenia"] = Dec(pot["ProcentWynagrodzeniaDoPotracenia"]);
                rec["procent_ochrony_wynagrodzenia"] = Dec(pot["ProcentOchronyWynagrodzenia"]);
                DateTime? dataOd = Date(pot["DataOd"]);
                DateTime? dataDo = Date(pot["DataDo"]);
                bool zawieszone = Bool(pot["Zawieszone"]);
                DateTime? zawOd = Date(pot["ZawieszoneDataOd"]);
                DateTime? zawDo = Date(pot["ZawieszoneDataDo"]);
                bool anulowane = Bool(pot["Anulowane"]);
                DateTime? anulOd = Date(pot["AnulowaneDataOd"]);
                rec["data_od"] = DateText(dataOd);
                rec["data_do"] = DateText(dataDo);
                rec["bezterminowo"] = Bool(pot["Bezterminowo"]);
                rec["zawieszone"] = zawieszone;
                rec["zawieszone_data_od"] = DateText(zawOd);
                rec["zawieszone_data_do"] = DateText(zawDo);
                rec["anulowane"] = anulowane;
                rec["anulowane_data_od"] = DateText(anulOd);
                rec["przyczyna_anulowania"] = Str(pot["PrzyczynaAnulowania"]);

                var potSplaty = splatyByPotracenie.TryGetValue(potId, out var sl) ? sl : new List<Dictionary<string, object>>();
                decimal SplataSum(Dictionary<string, object> s) =>
                    (Dec(s["KwotaPotracenia"]) ?? 0) + (Dec(s["KwotaPotraceniaZasilku"]) ?? 0)
                    + (Dec(s["KwotaPotraceniaZRachunkow"]) ?? 0) + (Dec(s["KwotaPotraceniaZasilkuZRachunkow"]) ?? 0);
                var splatyMiesiac = potSplaty.Where(s => Date(s["Miesiac"]) is DateTime m && m >= monthStart && m < nextMonth).ToList();
                rec["splata_miesiac_kwota"] = splatyMiesiac.Count > 0 ? splatyMiesiac.Sum(SplataSum) : (decimal?)null;
                rec["splaty_miesiac_liczba"] = splatyMiesiac.Count;
                rec["splaty_dotychczas_kwota"] = potSplaty.Count > 0 ? potSplaty.Sum(SplataSum) : (decimal?)null;
                rec["splaty_dotychczas_liczba"] = potSplaty.Count;

                // g) wyliczone
                rec["aktywne_w_miesiacu"] = dataOd != null && dataOd.Value.Date <= monthEnd
                    && (dataDo == null || dataDo.Value.Date >= monthStart)
                    && !(anulowane && (anulOd == null || anulOd.Value.Date < monthStart));
                rec["nowe_w_miesiacu"] = dataOd != null && dataOd.Value.Date >= monthStart && dataOd.Value.Date <= monthEnd;
                rec["zawieszone_w_miesiacu"] = zawieszone && (zawOd == null || zawOd.Value.Date <= monthEnd) && (zawDo == null || zawDo.Value.Date >= monthStart);

                // b) organ
                int? organId = Int(pot["Komornik_Id"]);
                Dictionary<string, object> komornik = null;
                if (organId != null) komornicy.TryGetValue(organId.Value, out komornik);
                if (organId != null && komornik == null) warnings.Add($"Potrącenie {potId}: Komornik_Id={organId} bez wiersza w Komornicy/Firmy.");
                string organNazwa = OneLine(komornik?["Nazwa"]);
                int? organPodmiot = komornik == null ? null : Int(komornik["Firma_Podmiot_Id"]);
                rec["organ_nazwa"] = organNazwa;
                rec["organ_nip"] = Str(komornik?["NIP"]);
                var organAdres = PickAddress(adresy, organPodmiot, "główny", "korespondencyjny");
                rec["organ_ulica"] = organAdres?.Ulica;
                rec["organ_nr_domu"] = organAdres?.NrDomu;
                rec["organ_nr_lokalu"] = organAdres?.NrLokalu;
                rec["organ_kod_pocztowy"] = organAdres?.Kod;
                rec["organ_miejscowosc"] = organAdres?.Miejscowosc;
                rec["organ_adres_pelny"] = organAdres?.Pelny;
                var kontoOrganu = organPodmiot == null ? null : konta
                    .Where(k => Int(k["Wlasciciel_Id"]) == organPodmiot)
                    .OrderByDescending(k => Bool(k["Aktywny"])).ThenByDescending(k => Int(k["WlascicielPodstawowego_Id"]) == organPodmiot)
                    .FirstOrDefault();
                rec["organ_konto"] = Str(kontoOrganu?["NumerBezSeparatorow"]) ?? Str(kontoOrganu?["Numer"]);

                string sygnatura = Str(pot["SygnaturaSprawy"]) ?? "";
                string rodzajOrganu;
                if ((organNazwa != null && UrzadSkarbowyRegex.IsMatch(organNazwa)) || SygnaturaSeeRegex.IsMatch(sygnatura)) rodzajOrganu = "URZAD_SKARBOWY";
                else if (organNazwa != null && organNazwa.Contains("komornik", StringComparison.OrdinalIgnoreCase)) rodzajOrganu = "KOMORNIK";
                else if (organNazwa != null) rodzajOrganu = "INNY";
                else rodzajOrganu = "BRAK";
                rec["rodzaj_organu"] = rodzajOrganu;

                // c) pracownik
                int pracGrId = Id(pot, "PracownikGr_Id");
                pracownicy.TryGetValue(pracGrId, out var prac);
                if (prac == null) warnings.Add($"Potrącenie {potId}: PracownikGr_Id={pracGrId} bez wiersza w PracownicyGr/Pracownicy/Osoby.");
                int? pracownikId = prac == null ? null : Int(prac["PracownikId"]);
                rec["pracownik_id"] = pracownikId;
                rec["pracownik_imie"] = Str(prac?["Imie"]);
                rec["pracownik_nazwisko"] = Str(prac?["Nazwisko"]);
                rec["pracownik_pesel"] = Str(prac?["PESEL"]);
                var pracAdres = PickAddress(adresy, prac == null ? null : Int(prac["Podmiot_Id"]), "główny", "zameldowania", "korespondencyjny");
                rec["pracownik_ulica"] = pracAdres?.Ulica;
                rec["pracownik_nr_domu"] = pracAdres?.NrDomu;
                rec["pracownik_nr_lokalu"] = pracAdres?.NrLokalu;
                rec["pracownik_kod_pocztowy"] = pracAdres?.Kod;
                rec["pracownik_miejscowosc"] = pracAdres?.Miejscowosc;
                rec["pracownik_adres_pelny"] = pracAdres?.Pelny;

                // d) pracodawca
                rec["pracodawca_nazwa"] = OneLine(firma?["Nazwa"]);
                rec["pracodawca_nip"] = Str(firma?["NIP"]);
                var firmaAdres = PickAddress(adresy, firma == null ? null : Int(firma["Firma_Podmiot_Id"]), "główny", "korespondencyjny");
                rec["pracodawca_ulica"] = firmaAdres?.Ulica;
                rec["pracodawca_nr_domu"] = firmaAdres?.NrDomu;
                rec["pracodawca_nr_lokalu"] = firmaAdres?.NrLokalu;
                rec["pracodawca_kod_pocztowy"] = firmaAdres?.Kod;
                rec["pracodawca_miejscowosc"] = firmaAdres?.Miejscowosc;
                rec["pracodawca_adres_pelny"] = firmaAdres?.Pelny;

                // e) umowa
                var umowyPracownika = pracownikId != null && umowyByPracownik.TryGetValue(pracownikId.Value, out var up) ? up : new List<Dictionary<string, object>>();
                var umowa = PickUmowa(umowyPracownika, monthStart, monthEnd, out string zrodlo);
                rec["umowa_zrodlo"] = zrodlo;
                rec["zatrudniony_w_miesiacu"] = umowa != null && zrodlo == "w_miesiacu";
                if (umowa != null)
                {
                    int umowaId = Id(umowa, "Id");
                    int typ = Id(umowa, "Typ");
                    rec["umowa_numer"] = Str(umowa["Sygnatura_PelnaSygnatura"]);
                    rec["umowa_rodzaj"] = Str(umowa["RodzajNazwa"]);
                    rec["umowa_typ_prawny"] = typ == 1 ? "o pracę" : typ == 2 ? "cywilnoprawna" : $"nieznany ({typ})";
                    rec["umowa_czas"] = Str(umowa["CzasNazwa"]);
                    rec["umowa_data_od"] = DateText(Date(umowa["OkresObowiazywania_DataPoczatkowa"]));
                    rec["umowa_data_do"] = DateText(Date(umowa["OkresObowiazywania_DataKoncowa"]));
                    rec["umowa_obowiazuje_od"] = DateText(Date(umowa["PierwszyDzienObowiazywania"]));
                    rec["umowa_data_rozwiazania"] = Bool(umowa["JestRozwiazana"]) ? DateText(Date(umowa["DataRozwiazania"])) : null;
                    rec["umowa_kwota"] = Dec(umowa["Kwota"]);
                    rec["zawod"] = Str(umowa["Zawod"]);

                    var chainIds = Chain(umowyPracownika, umowa).Select(c => Id(c, "Id")).ToHashSet();
                    var etat = PickByDate(etaty, "UmowaPracownicza_Id", "DataOd", "Lp", umowaId, chainIds, monthEnd);
                    rec["wymiar_etatu"] = etat == null ? null : $"{etat["Licznik"]}/{etat["Mianownik"]}";
                    var stan = PickByDate(stanowiska, "UmowaPracownicza_Id", "DataOd", "LP", umowaId, chainIds, monthEnd);
                    rec["stanowisko"] = Str(stan?["Stanowisko"]);
                    rec["jednostka_organizacyjna"] = Str(stan?["Jednostka"]);

                    FillPodstawa(rec, skladniki.Where(s => Id(s, "UmowaPracownicza_Id") == umowaId).ToList(), monthStart, monthEnd);
                }
                else
                {
                    rec["typ_wynagrodzenia"] = "INNE";
                }

                // f) brutto za 3 poprzednie miesiące - tylko zatwierdzone listy / rachunki
                var pracWyn = pracownikId == null ? new List<Dictionary<string, object>>() : wynagrodzenia.Where(w => Id(w, "Pracownik_Id") == pracownikId).ToList();
                decimal sum3 = 0;
                int niezatw3 = 0, monthsWithData = 0;
                for (int k = 1; k <= 3; k++)
                {
                    DateTime m = monthStart.AddMonths(-k);
                    var inMonth = pracWyn.Where(w => Date(w["Miesiac"]) is DateTime d && d >= m && d < m.AddMonths(1)).ToList();
                    var approved = inMonth.Where(w => Bool(w["Zatwierdzona"])).ToList();
                    rec[$"brutto_m{k}_okres"] = m.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                    rec[$"brutto_m{k}_miesiac"] = $"{MonthNames[m.Month - 1]} {m.Year}";
                    if (approved.Count > 0)
                    {
                        decimal s = approved.Sum(w => Dec(w["Brutto"]) ?? 0);
                        rec[$"brutto_m{k}"] = s;
                        sum3 += s;
                        monthsWithData++;
                    }
                    else
                    {
                        rec[$"brutto_m{k}"] = null;
                    }
                    niezatw3 += inMonth.Count - approved.Count;
                }
                rec["brutto_3m_suma"] = monthsWithData == 3 ? sum3 : (decimal?)null;
                rec["brutto_3m_suma_czesciowa"] = monthsWithData > 0 ? sum3 : (decimal?)null;
                rec["brutto_niezatwierdzone_dokumenty"] = niezatw3;

                // wynagrodzenie_podstawa: dla zleceń (umowa cywilnoprawna) kwota z umowy, dla umów o pracę podstawa ze składnika
                bool cywilna = Text(rec, "umowa_typ_prawny") == "cywilnoprawna";
                decimal? kwotaUmowy = Get(rec, "umowa_kwota") as decimal?;
                decimal? podstawaWartosc = Get(rec, "podstawa_wartosc") as decimal?;
                rec["wynagrodzenie_podstawa"] = cywilna ? (kwotaUmowy > 0 ? kwotaUmowy : null) : podstawaWartosc;

                rec["braki_danych"] = MissingFields(rec);
                records.Add(rec);
            }

            return records;
        }

        /// <summary>Klucze pól potrzebnych do pisma, które są puste (pula: organ_nazwa, organ_adres, organ_konto, sygnatura_sprawy,
        /// umowa, stanowisko, wynagrodzenie_podstawa, brutto_3m).</summary>
        private static List<string> MissingFields(Dictionary<string, object> rec)
        {
            var missing = new List<string>();
            if (Text(rec, "organ_nazwa") == null) missing.Add("organ_nazwa");
            if (Text(rec, "organ_adres_pelny") == null && Text(rec, "organ_miejscowosc") == null) missing.Add("organ_adres");
            if (Text(rec, "organ_konto") == null) missing.Add("organ_konto");
            if (Text(rec, "sygnatura_sprawy") == null) missing.Add("sygnatura_sprawy");
            if (Text(rec, "umowa_zrodlo") == "brak") missing.Add("umowa");
            if (Text(rec, "stanowisko") == null) missing.Add("stanowisko");
            if (Get(rec, "wynagrodzenie_podstawa") == null) missing.Add("wynagrodzenie_podstawa");
            if (Get(rec, "brutto_3m_suma") == null) missing.Add("brutto_3m");
            return missing;
        }

        // ------------------------------------------------------------------------------------------------
        // Rzutowanie rekordu na odpowiedź
        // ------------------------------------------------------------------------------------------------

        /// <summary>Wpis listy: bez PESEL, adresów i kwot brutto.</summary>
        private static Dictionary<string, object> ToListEntry(Dictionary<string, object> rec)
        {
            return ListKeys.ToDictionary(k => k, k => Get(rec, k));
        }

        private static Dictionary<string, object> Pick(Dictionary<string, object> rec, string prefix, IEnumerable<string> keys)
        {
            var result = new Dictionary<string, object>();
            foreach (string key in keys) result[key] = Get(rec, prefix + key);
            return result;
        }

        /// <summary>Wpis szczegółów: pola listy + organ, pracownik (z PESEL i adresem), pracodawca, umowa, brutto i spłaty.
        /// Bez wolnego tekstu "opis" potrącenia.</summary>
        private static Dictionary<string, object> ToDetailsEntry(Dictionary<string, object> rec)
        {
            var entry = ToListEntry(rec);
            foreach (string key in new[]
            {
                "czestotliwosc_potracania", "procent_ochrony_wynagrodzenia", "bezterminowo", "zawieszone_data_od", "zawieszone_data_do",
                "zawieszone_w_miesiacu", "przyczyna_anulowania", "wynagrodzenie_podstawa",
            })
            {
                entry[key] = Get(rec, key);
            }

            string[] addr = { "ulica", "nr_domu", "nr_lokalu", "kod_pocztowy", "miejscowosc", "adres_pelny" };
            entry["organ"] = Pick(rec, "organ_", new[] { "nazwa", "nip" }.Concat(addr).Append("konto"));
            entry["pracownik"] = Pick(rec, "pracownik_", new[] { "pesel" }.Concat(addr));
            entry["pracodawca"] = Pick(rec, "pracodawca_", new[] { "nazwa", "nip" }.Concat(addr));
            entry["umowa"] = new Dictionary<string, object>
            {
                ["numer"] = Get(rec, "umowa_numer"),
                ["czas"] = Get(rec, "umowa_czas"),
                ["data_od"] = Get(rec, "umowa_data_od"),
                ["data_do"] = Get(rec, "umowa_data_do"),
                ["obowiazuje_od"] = Get(rec, "umowa_obowiazuje_od"),
                ["data_rozwiazania"] = Get(rec, "umowa_data_rozwiazania"),
                ["wymiar_etatu"] = Get(rec, "wymiar_etatu"),
                ["stanowisko"] = Get(rec, "stanowisko"),
                ["jednostka_organizacyjna"] = Get(rec, "jednostka_organizacyjna"),
                ["zawod"] = Get(rec, "zawod"),
                ["podstawa_skladnik"] = Get(rec, "podstawa_skladnik"),
                ["podstawa_wartosc"] = Get(rec, "podstawa_wartosc"),
                ["umowa_kwota"] = Get(rec, "umowa_kwota"),
                ["zrodlo"] = Get(rec, "umowa_zrodlo"),
            };
            for (int k = 1; k <= 3; k++)
            {
                entry[$"brutto_m{k}"] = Get(rec, $"brutto_m{k}");
                entry[$"brutto_m{k}_okres"] = Get(rec, $"brutto_m{k}_okres");
                entry[$"brutto_m{k}_miesiac"] = Get(rec, $"brutto_m{k}_miesiac");
            }
            entry["brutto_3m_suma"] = Get(rec, "brutto_3m_suma");
            entry["brutto_3m_suma_czesciowa"] = Get(rec, "brutto_3m_suma_czesciowa");
            entry["brutto_niezatwierdzone_dokumenty"] = Get(rec, "brutto_niezatwierdzone_dokumenty");
            entry["splaty"] = new Dictionary<string, object>
            {
                ["miesiac_kwota"] = Get(rec, "splata_miesiac_kwota"),
                ["miesiac_liczba"] = Get(rec, "splaty_miesiac_liczba"),
                ["dotychczas_kwota"] = Get(rec, "splaty_dotychczas_kwota"),
                ["dotychczas_liczba"] = Get(rec, "splaty_dotychczas_liczba"),
            };
            return entry;
        }

        // ------------------------------------------------------------------------------------------------
        // Wybór umowy, etatu, stanowiska i podstawy (jak w prototypie z konsoli)
        // ------------------------------------------------------------------------------------------------

        /// <summary>Umowa obowiązująca w miesiącu (z ciągu umowa+aneksy najnowszy aneks); gdy brak - ostatnia umowa rozpoczęta
        /// przed końcem miesiąca (zrodlo = "ostatnia_przed_miesiacem"); w przeciwnym razie "brak".</summary>
        private static Dictionary<string, object> PickUmowa(List<Dictionary<string, object>> umowy, DateTime monthStart, DateTime monthEnd, out string zrodlo)
        {
            bool InForce(Dictionary<string, object> u)
            {
                DateTime? first = Date(u["PierwszyDzienObowiazywania"]);
                DateTime? last = Date(u["OstatniDzienObowiazywania"]);
                DateTime? ended = Bool(u["JestRozwiazana"]) ? Date(u["DataRozwiazania"]) : null;
                return first != null && first.Value.Date <= monthEnd
                    && (last == null || last.Value.Date >= monthStart)
                    && (ended == null || ended.Value.Date >= monthStart);
            }

            var inForce = umowy.Where(InForce).ToList();
            if (inForce.Count > 0)
            {
                var perChain = inForce
                    .GroupBy(u => Int(u["UmowaGlowna_Id"]) ?? Id(u, "Id"))
                    .Select(g => g.OrderByDescending(u => Date(u["PierwszyDzienObowiazywania"])).ThenByDescending(u => Id(u, "Id")).First())
                    .ToList();
                zrodlo = "w_miesiacu";
                return perChain.OrderBy(u => Id(u, "Typ") == 1 ? 0 : 1)
                    .ThenByDescending(u => Date(u["PierwszyDzienObowiazywania"]))
                    .ThenByDescending(u => Id(u, "Id")).First();
            }

            var before = umowy.Where(u => Date(u["PierwszyDzienObowiazywania"]) is DateTime f && f.Date <= monthEnd).ToList();
            if (before.Count > 0)
            {
                zrodlo = "ostatnia_przed_miesiacem";
                return before.OrderByDescending(u => Date(u["OstatniDzienObowiazywania"]) ?? DateTime.MaxValue)
                    .ThenByDescending(u => Id(u, "Id")).First();
            }

            zrodlo = "brak";
            return null;
        }

        /// <summary>Wszystkie umowy (główna + aneksy) tego samego ciągu co wybrana umowa.</summary>
        private static List<Dictionary<string, object>> Chain(List<Dictionary<string, object>> umowy, Dictionary<string, object> umowa)
        {
            int root = Int(umowa["UmowaGlowna_Id"]) ?? Id(umowa, "Id");
            return umowy.Where(u => (Int(u["UmowaGlowna_Id"]) ?? Id(u, "Id")) == root || Id(u, "Id") == root).ToList();
        }

        /// <summary>Wiersz obowiązujący na koniec miesiąca; najpierw wiersze wybranej umowy, potem pozostałych z ciągu aneksów.</summary>
        private static Dictionary<string, object> PickByDate(List<Dictionary<string, object>> rows, string umowaCol, string dateCol, string orderCol,
            int umowaId, HashSet<int> chainIds, DateTime monthEnd)
        {
            IEnumerable<Dictionary<string, object>> Candidates(Func<int, bool> filter) => rows
                .Where(r => filter(Id(r, umowaCol)) && Date(r[dateCol]) is DateTime d && d.Date <= monthEnd)
                .OrderByDescending(r => Date(r[dateCol])).ThenBy(r => Id(r, orderCol));
            return Candidates(id => id == umowaId).FirstOrDefault() ?? Candidates(id => chainIds.Contains(id)).FirstOrDefault();
        }

        /// <summary>Podstawa wynagrodzenia: składniki "Podstawa godzinowa" / "Podstawa miesięczna" z wartością obowiązującą w miesiącu
        /// (RodzajWartosci=2 "ZUmowy", najpóźniejsza data początkowa). Typ: GODZINOWE / STALE_MIESIECZNE / INNE.</summary>
        private static void FillPodstawa(Dictionary<string, object> rec, List<Dictionary<string, object>> skladniki, DateTime monthStart, DateTime monthEnd)
        {
            (string Name, decimal? Value) Effective(Func<string, bool> nameMatches)
            {
                string name = null;
                decimal? value = null;
                DateTime? bestStart = null;
                foreach (var s in skladniki)
                {
                    string n = Str(s["Nazwa"]);
                    if (n == null || !nameMatches(n)) continue;
                    name ??= n;
                    decimal? v = Dec(s["Wartosc"]);
                    DateTime? od = Date(s["Od"]) ?? DateTime.MinValue; // brak daty początkowej = obowiązuje od zawsze
                    DateTime? doo = Date(s["Do"]);
                    int rodzajWartosci = Int(s["RodzajWartosci"]) ?? 0;
                    if (v == null || rodzajWartosci != 2 || od.Value.Date > monthEnd || (doo != null && doo.Value.Date < monthStart)) continue;
                    if (bestStart == null || od > bestStart) { bestStart = od; value = v; name = n; }
                }
                return (name, value);
            }

            var godz = Effective(n => n.StartsWith("Podstawa godzin", StringComparison.OrdinalIgnoreCase));
            var mies = Effective(n => n.StartsWith("Podstawa miesi", StringComparison.OrdinalIgnoreCase));
            bool godzVal = godz.Value > 0, miesVal = mies.Value > 0;
            string typ;
            if (godzVal && !miesVal) typ = "GODZINOWE";
            else if (miesVal && !godzVal) typ = "STALE_MIESIECZNE";
            else if (!godzVal && !miesVal && godz.Name != null && mies.Name == null) typ = "GODZINOWE";
            else if (!godzVal && !miesVal && mies.Name != null && godz.Name == null) typ = "STALE_MIESIECZNE";
            else typ = "INNE";
            rec["typ_wynagrodzenia"] = typ;
            rec["podstawa_skladnik"] = godzVal ? godz.Name : miesVal ? mies.Name : (mies.Name ?? godz.Name);
            rec["podstawa_wartosc"] = godzVal ? godz.Value : miesVal ? mies.Value : null;
        }
    }
}
