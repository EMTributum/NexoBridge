using Microsoft.Data.SqlClient;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    /// <summary>Błąd merytoryczny odczytu (niejednoznaczna firma, przekroczony limit, powtórzony wiersz) -
    /// endpoint zwraca go jako 422 z komunikatem.</summary>
    public class OfficeReportsException : Exception
    {
        public OfficeReportsException(string message) : base(message) { }
    }

    /// <summary>Brak DB_SERVER / DB_USER / DB_PASS w środowisku usługi.</summary>
    public class OfficeReportsConfigurationException : Exception
    {
        public OfficeReportsConfigurationException(string message) : base(message) { }
    }

    /// <summary>
    /// Synchroniczne odczyty z WŁASNEJ bazy biura (Subiekt nexo) dla raportów biura: koszt pracodawcy z list płac,
    /// faktury sprzedaży FS z pozycjami, adresy e-mail i cechy klientów biura. Czysty Microsoft.Data.SqlClient - bez Sfery i bez kolejki zleceń.
    /// Wyłącznie sparametryzowane SELECT-y, połączenie z ApplicationIntent=ReadOnly.
    /// Zapytania przeniesione 1:1 z dawnej aplikacji (rejestr_nexo_payroll.py / rejestr_nexo_invoices.py)
    /// i sprawdzone w NexoBridgeKonsola (--office-payroll-costs / --office-sales-invoices) na bazie biura.
    /// </summary>
    public class OfficeReportsSqlService
    {
        public const int MaxPayrollDocuments = 2000;
        public const int MaxInvoiceRows = 20000;
        public const int MaxInvoiceDocuments = 2000;
        public const int MaxClientEmails = 20000;
        public const int MaxClientFeatureRows = 20000;
        private const int MaxIssuerCandidates = 200;
        private const int CommandTimeoutSeconds = 60;
        private const int ConnectTimeoutSeconds = 15;

        // Kontakty rodzaju "e-mail" podmiotów, które są klientami biura (KlienciBiura, aktywnymi i nie). Nazwa: pełna
        // nazwa firmy, a dla osoby fizycznej imię i nazwisko; NazwaSkrocona jako zapas. Sprawdzone w NexoBridgeKonsola
        // (--office-sql-json) na bazie biura.
        private const string ClientEmailsSql = @"
            SELECT TOP (@limit)
                k.Id AS ContactId,
                kb.Id AS ClientId,
                p.Id AS EntityId,
                p.NIP AS Nip,
                COALESCE(
                    NULLIF(LTRIM(RTRIM(f.Nazwa)), ''),
                    NULLIF(LTRIM(RTRIM(CONCAT(o.Imie, ' ', o.Nazwisko))), ''),
                    p.NazwaSkrocona) AS Name,
                p.NazwaSkrocona AS ShortName,
                kb.Aktywny AS Active,
                k.Wartosc AS Value,
                k.Podstawowy AS IsPrimary,
                k.Komentarz AS Comment
            FROM ModelDanychContainer.KlienciBiura kb
            JOIN ModelDanychContainer.Podmioty p ON p.Id = kb.Podmiot_Id
            JOIN ModelDanychContainer.Kontakty k ON k.Podmiot_Id = p.Id
            JOIN ModelDanychContainer.RodzajeKontaktu r ON r.Id = k.Rodzaj_Id
            OUTER APPLY (
                SELECT TOP (1) fx.Nazwa FROM ModelDanychContainer.Firmy fx
                WHERE fx.Firma_Podmiot_Id = p.Id ORDER BY fx.Id) f
            OUTER APPLY (
                SELECT TOP (1) ox.Imie, ox.Nazwisko FROM ModelDanychContainer.Osoby ox
                WHERE ox.Podmiot_Id = p.Id ORDER BY ox.Id) o
            WHERE r.Nazwa = @contactKind
            ORDER BY kb.Id, k.Id";

        // Wszyscy klienci biura (aktywni i nie), po wierszu na cechę; klient bez cech ma jeden wiersz z Feature = NULL.
        // Sprawdzone w NexoBridgeKonsola (--office-sql-json) na bazie biura.
        private const string ClientFeaturesSql = @"
            SELECT TOP (@limit)
                kb.Id AS ClientId,
                p.NIP AS Nip,
                p.NazwaSkrocona AS Name,
                kb.Aktywny AS Active,
                c.Nazwa AS Feature
            FROM ModelDanychContainer.KlienciBiura kb
            JOIN ModelDanychContainer.Podmioty p ON p.Id = kb.Podmiot_Id
            LEFT JOIN ModelDanychContainer.CechaPodmiot cp ON cp.Podmioty_Id = p.Id
            LEFT JOIN ModelDanychContainer.Cechy c ON c.Id = cp.Cechy_Id
            ORDER BY kb.Id, c.Nazwa";

        private const string CompanySql = @"
            SELECT TOP (2) f.Id AS CompanyId, f.Firma_Podmiot_Id AS EntityId, f.Nazwa AS Name, p.NIP AS Nip
            FROM ModelDanychContainer.Firmy f
            JOIN ModelDanychContainer.Podmioty p ON p.Id = f.Firma_Podmiot_Id
            WHERE f.__MdmDiscriminator__ = @discriminator
            ORDER BY f.Id";

        private const string PayrollSql = @"
            SELECT TOP (@limit)
                w.Id AS DocumentId,
                l.Id AS ListId,
                u.Id AS ContractId,
                p.Id AS EmployeeId,
                o.Id AS PersonId,
                o.Imie AS FirstName,
                o.Nazwisko AS LastName,
                l.Miesiac AS ListMonth,
                l.Tytul AS ListTitle,
                l.Numer_PelnaSygnatura AS ListNumber,
                COALESCE(r.Numer_PelnaSygnatura, l.Numer_PelnaSygnatura) AS Number,
                CASE WHEN wp.Id IS NOT NULL AND r.Id IS NULL THEN 'payroll'
                     WHEN r.Id IS NOT NULL AND wp.Id IS NULL THEN 'bill' ELSE 'unknown' END AS Kind,
                l.Zatwierdzona AS Approved,
                w.WymagaKorekty AS NeedsCorrection,
                w.PozaSystememWynagrodzen AS OutsidePayrollSystem,
                w.Brutto AS Gross,
                w.ObciazeniePracodawcy AS EmployerCost
            FROM ModelDanychContainer.Wynagrodzenia w
            JOIN ModelDanychContainer.ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
            LEFT JOIN ModelDanychContainer.UmowyPracownicze u ON u.Id = w.Umowa_Id
            LEFT JOIN ModelDanychContainer.Pracownicy p ON p.Id = u.Pracownik_Id
            LEFT JOIN ModelDanychContainer.Osoby o ON o.Id = p.Osoba_Id
            LEFT JOIN ModelDanychContainer.Wynagrodzenia_Wyplata wp ON wp.Id = w.Id
            LEFT JOIN ModelDanychContainer.Wynagrodzenia_RachunekDoUmowyPracowniczej r ON r.Id = w.Id
            WHERE l.Miesiac >= @periodFrom AND l.Miesiac < @periodTo
            ORDER BY p.Id, w.Id";

        // Zapisy NIP wystawcy na FS własnej firmy (kontrola jednoznaczności, patrz ResolveIssuerAsync).
        private const string IssuerCandidatesSql = @"
            SELECT DISTINCT TOP (@limit) h.NIP AS Nip
            FROM ModelDanychContainer.Dokumenty d
            JOIN ModelDanychContainer.PodmiotHistorie h ON h.Id = d.MojaFirmaWybranaId
            WHERE d.Symbol = @symbol AND d.__MdmDiscriminator__ = @discriminator AND d.MojaFirmaId = @issuerId";

        private const string InvoicesSql = @"
            SELECT TOP (@limit)
                d.Id AS DocumentId, d.NumerWewnetrzny_PelnaSygnatura AS Number,
                d.DataWydaniaWystawienia AS IssueDate, d.DataSprzedazy AS SaleDate,
                h.Nazwa AS BuyerName, h.NIP AS BuyerNip, w.Symbol AS Currency, s.Nazwa AS Status,
                d.Wartosc_NettoPoRabacie AS NetTotal, d.Wartosc_VatPoRabacie AS VatTotal,
                d.Wartosc_BruttoPoRabacie AS GrossTotal,
                p.Id AS PositionId, p.LP AS LineNumber, a.Nazwa AS Name, p.Opis AS Description,
                p.Ilosc AS Quantity, jm.Symbol AS Unit, p.Cena_NettoPoRabacie AS UnitNetPrice,
                p.Wartosc_NettoPoRabacie AS Net, p.Wartosc_VatPoRabacie AS Vat,
                p.Wartosc_BruttoPoRabacie AS Gross
            FROM ModelDanychContainer.Dokumenty d
            JOIN ModelDanychContainer.StatusyDokumentow s ON s.Id = d.StatusDokumentuId
            JOIN ModelDanychContainer.PodmiotHistorie issuer ON issuer.Id = d.MojaFirmaWybranaId
            LEFT JOIN ModelDanychContainer.PodmiotHistorie h
                ON h.Id = COALESCE(d.NabywcaSprzedawcaWybranyId, d.PodmiotWybranyId)
            LEFT JOIN ModelDanychContainer.Waluty w ON w.Id = d.Dokument_Waluta_Id
            LEFT JOIN ModelDanychContainer.PozycjeDokumentu p ON p.Dokument_Id = d.Id
            LEFT JOIN ModelDanychContainer.AsortymentyHistoria a ON a.Id = p.AsortymentWybranyId
            LEFT JOIN ModelDanychContainer.JednostkiMiarAsortymentow ja ON ja.Id = p.JednostkaMiaryAsId
            LEFT JOIN ModelDanychContainer.JednostkiMiar jm ON jm.Id = ja.JednostkaMiary_Id
            WHERE d.DataWydaniaWystawienia >= @issueFrom AND d.DataWydaniaWystawienia < @issueTo
                AND d.Symbol = @symbol AND d.__MdmDiscriminator__ = @discriminator
                AND d.MojaFirmaId = @issuerId AND issuer.NIP = @issuerNip
                AND s.Uniewazniony = 0 AND ISNULL(s.Przejsciowy, 0) = 0
            ORDER BY d.DataWydaniaWystawienia DESC, d.Id DESC, p.LP, p.Id";

        public async Task<OfficePayrollCostsResponse> ReadPayrollCostsAsync(string databaseName, int year, int month, CancellationToken cancellationToken)
        {
            var periodFrom = new DateTime(year, month, 1);
            var periodTo = periodFrom.AddMonths(1);
            var response = new OfficePayrollCostsResponse
            {
                DatabaseName = databaseName,
                Year = year,
                Month = month,
                PeriodFrom = FormatDate(periodFrom),
                PeriodTo = FormatDate(periodTo),
            };

            await using var connection = new SqlConnection(BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);

            List<OfficeCompanyInfo> companies = await ReadOwnCompaniesAsync(connection, cancellationToken);
            response.CompanyCount = companies.Count;
            response.Company = companies.Count == 1 ? companies[0] : null;

            await using (var command = CreateCommand(connection, PayrollSql))
            {
                command.Parameters.Add("@limit", SqlDbType.Int).Value = MaxPayrollDocuments + 1;
                command.Parameters.Add("@periodFrom", SqlDbType.DateTime2).Value = periodFrom;
                command.Parameters.Add("@periodTo", SqlDbType.DateTime2).Value = periodTo;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var seen = new HashSet<int>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    var document = new OfficePayrollDocument
                    {
                        DocumentId = GetInt(reader, "DocumentId").Value,
                        ListId = GetInt(reader, "ListId").Value,
                        ContractId = GetInt(reader, "ContractId"),
                        EmployeeId = GetInt(reader, "EmployeeId"),
                        PersonId = GetInt(reader, "PersonId"),
                        FirstName = GetString(reader, "FirstName"),
                        LastName = GetString(reader, "LastName"),
                        ListMonth = GetDate(reader, "ListMonth"),
                        ListTitle = GetString(reader, "ListTitle"),
                        ListNumber = GetString(reader, "ListNumber"),
                        Number = GetString(reader, "Number"),
                        Kind = GetString(reader, "Kind"),
                        Approved = GetBool(reader, "Approved") ?? false,
                        NeedsCorrection = GetBool(reader, "NeedsCorrection") ?? false,
                        OutsidePayrollSystem = GetBool(reader, "OutsidePayrollSystem") ?? false,
                        Gross = GetDecimal(reader, "Gross"),
                        EmployerCost = GetDecimal(reader, "EmployerCost"),
                    };
                    if (!seen.Add(document.DocumentId))
                    {
                        throw new OfficeReportsException("Powtórzony dokument płacowy w wyniku zapytania. Odczyt przerwany.");
                    }
                    response.Documents.Add(document);
                }
            }

            if (response.Documents.Count > MaxPayrollDocuments)
            {
                throw new OfficeReportsException($"Za dużo dokumentów płacowych w miesiącu (> {MaxPayrollDocuments}). Odczyt przerwany, aby nie zwrócić niepełnych danych.");
            }

            response.DocumentCount = response.Documents.Count;
            return response;
        }

        public async Task<OfficeClientEmailsResponse> ReadClientEmailsAsync(string databaseName, CancellationToken cancellationToken)
        {
            var response = new OfficeClientEmailsResponse { DatabaseName = databaseName };

            await using var connection = new SqlConnection(BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);

            await using (var command = CreateCommand(connection, ClientEmailsSql))
            {
                command.Parameters.Add("@limit", SqlDbType.Int).Value = MaxClientEmails + 1;
                command.Parameters.Add("@contactKind", SqlDbType.NVarChar, 100).Value = "e-mail";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var seen = new HashSet<int>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    var contact = new OfficeClientEmail
                    {
                        ContactId = GetInt(reader, "ContactId").Value,
                        ClientId = GetInt(reader, "ClientId").Value,
                        EntityId = GetInt(reader, "EntityId").Value,
                        Nip = GetString(reader, "Nip"),
                        Name = GetString(reader, "Name"),
                        ShortName = GetString(reader, "ShortName"),
                        Active = GetBool(reader, "Active") ?? false,
                        Value = GetString(reader, "Value"),
                        IsPrimary = GetBool(reader, "IsPrimary") ?? false,
                        Comment = GetString(reader, "Comment"),
                    };
                    if (!seen.Add(contact.ContactId))
                    {
                        throw new OfficeReportsException("Powtórzony kontakt e-mail w wyniku zapytania. Odczyt przerwany.");
                    }
                    response.Contacts.Add(contact);
                }
            }

            if (response.Contacts.Count > MaxClientEmails)
            {
                throw new OfficeReportsException($"Za dużo kontaktów e-mail klientów (> {MaxClientEmails}). Odczyt przerwany, aby nie zwrócić niepełnych danych.");
            }

            response.ContactCount = response.Contacts.Count;
            return response;
        }

        public async Task<OfficeClientFeaturesResponse> ReadClientFeaturesAsync(string databaseName, CancellationToken cancellationToken)
        {
            var response = new OfficeClientFeaturesResponse { DatabaseName = databaseName };

            await using var connection = new SqlConnection(BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);

            var clients = new Dictionary<int, OfficeClientFeatures>();
            int rowCount = 0;
            await using (var command = CreateCommand(connection, ClientFeaturesSql))
            {
                command.Parameters.Add("@limit", SqlDbType.Int).Value = MaxClientFeatureRows + 1;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rowCount++;
                    int clientId = GetInt(reader, "ClientId").Value;
                    if (!clients.TryGetValue(clientId, out var client))
                    {
                        client = new OfficeClientFeatures
                        {
                            ClientId = clientId,
                            Nip = GetString(reader, "Nip"),
                            Name = GetString(reader, "Name"),
                            Active = GetBool(reader, "Active") ?? false,
                        };
                        clients.Add(clientId, client);
                        response.Clients.Add(client);
                    }

                    string feature = GetString(reader, "Feature");
                    if (!string.IsNullOrWhiteSpace(feature))
                    {
                        client.Features.Add(feature.Trim());
                    }
                }
            }

            if (rowCount > MaxClientFeatureRows)
            {
                throw new OfficeReportsException($"Za dużo wierszy cech klientów (> {MaxClientFeatureRows}). Odczyt przerwany, aby nie zwrócić niepełnych danych.");
            }

            response.ClientCount = response.Clients.Count;
            return response;
        }

        public async Task<OfficeSalesInvoicesResponse> ReadSalesInvoicesAsync(string databaseName, int issueYear, int issueMonth, CancellationToken cancellationToken)
        {
            var issueFrom = new DateTime(issueYear, issueMonth, 1);
            var issueTo = issueFrom.AddMonths(1);
            var response = new OfficeSalesInvoicesResponse
            {
                DatabaseName = databaseName,
                IssueYear = issueYear,
                IssueMonth = issueMonth,
                IssueDateFrom = FormatDate(issueFrom),
                IssueDateTo = FormatDate(issueTo),
            };

            await using var connection = new SqlConnection(BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);

            response.Issuer = await ResolveIssuerAsync(connection, cancellationToken);

            var documents = new Dictionary<int, OfficeSalesInvoice>();
            var positionIds = new HashSet<int>();
            int rowCount = 0;
            await using (var command = CreateCommand(connection, InvoicesSql))
            {
                command.Parameters.Add("@limit", SqlDbType.Int).Value = MaxInvoiceRows + 1;
                command.Parameters.Add("@issueFrom", SqlDbType.DateTime2).Value = issueFrom;
                command.Parameters.Add("@issueTo", SqlDbType.DateTime2).Value = issueTo;
                command.Parameters.Add("@symbol", SqlDbType.NVarChar, 50).Value = "FS";
                command.Parameters.Add("@discriminator", SqlDbType.NVarChar, 128).Value = "DokumentDS";
                command.Parameters.Add("@issuerId", SqlDbType.Int).Value = response.Issuer.EntityId;
                command.Parameters.Add("@issuerNip", SqlDbType.NVarChar, 100).Value = response.Issuer.Nip;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rowCount++;
                    int documentId = GetInt(reader, "DocumentId").Value;
                    if (!documents.TryGetValue(documentId, out OfficeSalesInvoice document))
                    {
                        document = new OfficeSalesInvoice
                        {
                            DocumentId = documentId,
                            Number = GetString(reader, "Number"),
                            IssueDate = GetDate(reader, "IssueDate"),
                            SaleDate = GetDate(reader, "SaleDate"),
                            Status = GetString(reader, "Status"),
                            BuyerName = GetString(reader, "BuyerName"),
                            BuyerNip = GetString(reader, "BuyerNip"),
                            Currency = GetString(reader, "Currency")?.Trim(),
                            NetTotal = GetDecimal(reader, "NetTotal"),
                            VatTotal = GetDecimal(reader, "VatTotal"),
                            GrossTotal = GetDecimal(reader, "GrossTotal"),
                        };
                        documents.Add(documentId, document);
                        response.Documents.Add(document);
                    }

                    int? positionId = GetInt(reader, "PositionId");
                    if (positionId == null)
                    {
                        continue;
                    }

                    if (!positionIds.Add(positionId.Value))
                    {
                        throw new OfficeReportsException("Wykryto powtórzoną pozycję faktury. Odczyt przerwany, aby nie zawyżyć kwot.");
                    }

                    document.Positions.Add(new OfficeSalesInvoicePosition
                    {
                        PositionId = positionId.Value,
                        LineNumber = GetInt(reader, "LineNumber"),
                        Name = GetString(reader, "Name"),
                        Description = GetString(reader, "Description"),
                        Quantity = GetDecimal(reader, "Quantity"),
                        Unit = GetString(reader, "Unit"),
                        UnitNetPrice = GetDecimal(reader, "UnitNetPrice"),
                        Net = GetDecimal(reader, "Net"),
                        Vat = GetDecimal(reader, "Vat"),
                        Gross = GetDecimal(reader, "Gross"),
                    });
                }
            }

            if (rowCount > MaxInvoiceRows)
            {
                throw new OfficeReportsException($"Przekroczono limit pozycji faktur w miesiącu (> {MaxInvoiceRows}). Odczyt przerwany, aby nie zwrócić niepełnych danych.");
            }

            if (documents.Count > MaxInvoiceDocuments)
            {
                throw new OfficeReportsException($"Przekroczono limit faktur w miesiącu (> {MaxInvoiceDocuments}). Odczyt przerwany, aby nie zwrócić niepełnych danych.");
            }

            response.DocumentCount = documents.Count;
            response.PositionCount = positionIds.Count;
            return response;
        }

        /// <summary>Wystawca faktur FS = własna firma bazy (Firmy z dyskryminatorem MojaFirma).
        /// Dokumenty.MojaFirmaId wskazuje Firmy.Firma_Podmiot_Id (EntityId). Nazwa wystawcy w PodmiotHistorie
        /// zmienia się w czasie (w bazie biura: "EMTRIBUTUM", "EMTRIBUTUM sp. z o.o.", pełna nazwa spółki),
        /// więc zamiast dawnego dopasowania po nazwie z lokalnej konfiguracji (resolve_issuer) bierzemy
        /// EntityId + NIP własnej firmy. Wśród FS tej firmy zapis NIP wystawcy (po normalizacji równy NIP firmy)
        /// musi być jednoznaczny - wtedy filtr "MojaFirmaId = @issuerId AND issuer.NIP = @issuerNip" daje to samo,
        /// co dawna aplikacja.</summary>
        private static async Task<OfficeCompanyInfo> ResolveIssuerAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            List<OfficeCompanyInfo> companies = await ReadOwnCompaniesAsync(connection, cancellationToken);
            if (companies.Count != 1)
            {
                throw new OfficeReportsException($"Nie można jednoznacznie ustalić własnej firmy w bazie (rekordów MojaFirma: {companies.Count}, oczekiwano 1).");
            }

            OfficeCompanyInfo company = companies[0];
            string companyNip = NormalizeNip(company.Nip);
            if (string.IsNullOrEmpty(companyNip))
            {
                throw new OfficeReportsException("Własna firma w bazie nie ma NIP - nie można potwierdzić wystawcy faktur FS.");
            }

            var issuerNips = new HashSet<string>(StringComparer.Ordinal);
            int candidateCount = 0;
            await using (var command = CreateCommand(connection, IssuerCandidatesSql))
            {
                command.Parameters.Add("@limit", SqlDbType.Int).Value = MaxIssuerCandidates + 1;
                command.Parameters.Add("@symbol", SqlDbType.NVarChar, 50).Value = "FS";
                command.Parameters.Add("@discriminator", SqlDbType.NVarChar, 128).Value = "DokumentDS";
                command.Parameters.Add("@issuerId", SqlDbType.Int).Value = company.EntityId;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    candidateCount++;
                    string nip = GetString(reader, "Nip");
                    if (nip != null && NormalizeNip(nip) == companyNip)
                    {
                        issuerNips.Add(nip);
                    }
                }
            }

            if (candidateCount > MaxIssuerCandidates || issuerNips.Count > 1)
            {
                throw new OfficeReportsException("Niejednoznaczny zapis NIP wystawcy na fakturach FS własnej firmy. Potrzebne jest doprecyzowanie podmiotu.");
            }

            return new OfficeCompanyInfo
            {
                CompanyId = company.CompanyId,
                EntityId = company.EntityId,
                Name = company.Name,
                // Brak FS w ogóle -> NIP z kartoteki firmy (zapytanie i tak zwróci pustą listę).
                Nip = issuerNips.Count == 1 ? issuerNips.First() : company.Nip,
            };
        }

        private static async Task<List<OfficeCompanyInfo>> ReadOwnCompaniesAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            var companies = new List<OfficeCompanyInfo>();
            await using var command = CreateCommand(connection, CompanySql);
            command.Parameters.Add("@discriminator", SqlDbType.NVarChar, 128).Value = "MojaFirma";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                companies.Add(new OfficeCompanyInfo
                {
                    CompanyId = GetInt(reader, "CompanyId").Value,
                    EntityId = GetInt(reader, "EntityId").Value,
                    Name = GetString(reader, "Name"),
                    Nip = GetString(reader, "Nip"),
                });
            }
            return companies;
        }

        /// <summary>Połączenie z DB_SERVER / DB_USER / DB_PASS (te same zmienne co Sfera) i bazą z żądania.
        /// Encrypt + TrustServerCertificate ustawiane słowami kluczowymi, żeby kod działał identycznie z
        /// Microsoft.Data.SqlClient 5.x (NexoDLLs) i 7.x (pakiet NuGet).</summary>
        internal static string BuildConnectionString(string databaseName)
        {
            string server = Environment.GetEnvironmentVariable("DB_SERVER");
            string user = Environment.GetEnvironmentVariable("DB_USER");
            string password = Environment.GetEnvironmentVariable("DB_PASS");
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user) || password == null)
            {
                throw new OfficeReportsConfigurationException("Brak konfiguracji połączenia SQL (DB_SERVER / DB_USER / DB_PASS) w środowisku NexoBridge.");
            }

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = databaseName,
                UserID = user,
                Password = password,
                ApplicationIntent = ApplicationIntent.ReadOnly,
                ConnectTimeout = ConnectTimeoutSeconds,
                ApplicationName = "NexoBridge-OfficeReports",
            };
            builder["Encrypt"] = "True";
            builder["TrustServerCertificate"] = "True";
            return builder.ConnectionString;
        }

        private static SqlCommand CreateCommand(SqlConnection connection, string sql)
        {
            return new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        }

        /// <summary>Jak normalize_nip z dawnej aplikacji: wielkie litery, tylko znaki alfanumeryczne, bez prefiksu PL.</summary>
        private static string NormalizeNip(string value)
        {
            var builder = new StringBuilder();
            foreach (char c in (value ?? string.Empty).Trim().ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                }
            }
            string raw = builder.ToString();
            return raw.StartsWith("PL", StringComparison.Ordinal) && raw.Length > 2 && raw.Skip(2).All(char.IsDigit) ? raw.Substring(2) : raw;
        }

        private static string FormatDate(DateTime value)
        {
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string GetString(SqlDataReader reader, string column)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static int? GetInt(SqlDataReader reader, string column)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static decimal? GetDecimal(SqlDataReader reader, string column)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static bool? GetBool(SqlDataReader reader, string column)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static string GetDate(SqlDataReader reader, string column)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : FormatDate(reader.GetDateTime(ordinal));
        }
    }
}
