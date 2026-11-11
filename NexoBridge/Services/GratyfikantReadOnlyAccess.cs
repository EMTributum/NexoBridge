using Microsoft.Data.SqlClient;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    /// <summary>
    /// Wspólny dostęp tylko do odczytu do baz Gratyfikanta (Nexo_*) dla odczytów kadrowych (podstawy ZUS, potrącenia):
    /// konto DB_READ_USER / DB_READ_PASS (nigdy DB_USER), ApplicationIntent=ReadOnly, odmowa przy innym loginie
    /// albo roli sysadmin, walidacja i ustalenie nazwy bazy. Błąd konfiguracji zgłaszany jest wyjątkiem podanym
    /// przez wywołującego (każdy odczyt ma własny typ, który jego endpoint mapuje na 503).
    /// </summary>
    public static class GratyfikantReadOnlyAccess
    {
        public const int MaxDatabaseNameLength = 128;
        public const string NexoDatabasePrefix = "Nexo_";
        private const int CommandTimeoutSeconds = 60;
        private const int ConnectTimeoutSeconds = 15;

        /// <summary>Nazwa bazy: niepusta, do 128 znaków, bez znaków sterujących, zaczyna się od "Nexo_".</summary>
        public static bool IsValidDatabaseName(string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName)) return false;
            string trimmed = databaseName.Trim();
            return trimmed.Length <= MaxDatabaseNameLength
                && trimmed.StartsWith(NexoDatabasePrefix, StringComparison.OrdinalIgnoreCase)
                && !trimmed.Any(char.IsControl);
        }

        public static void GetCredentials(Func<string, Exception> configurationError, out string server, out string user, out string password)
        {
            server = Environment.GetEnvironmentVariable("DB_SERVER");
            user = Environment.GetEnvironmentVariable("DB_READ_USER");
            password = Environment.GetEnvironmentVariable("DB_READ_PASS");
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            {
                throw configurationError(
                    "Brak konfiguracji konta tylko do odczytu (DB_SERVER / DB_READ_USER / DB_READ_PASS) w środowisku NexoBridge. Konto administracyjne nie jest używane.");
            }
        }

        /// <summary>Nazwa bazy trafia do połączenia wyłącznie przez SqlConnectionStringBuilder.</summary>
        public static string BuildConnectionString(string server, string user, string password, string database, string applicationName)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                UserID = user,
                Password = password,
                ApplicationIntent = ApplicationIntent.ReadOnly,
                ConnectTimeout = ConnectTimeoutSeconds,
                ApplicationName = applicationName,
            };
            builder["Encrypt"] = "True";
            builder["TrustServerCertificate"] = "True";
            return builder.ConnectionString;
        }

        /// <summary>Połączenie musi iść na koncie DB_READ_USER, które nie jest sysadminem. <paramref name="what"/> trafia do komunikatu (np. "podstaw składek").</summary>
        public static async Task EnsureReadOnlyLoginAsync(
            SqlConnection connection, string expectedUser, string what, Func<string, Exception> configurationError, CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                "SELECT SUSER_SNAME() AS LoginName, ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS IsSysAdmin", connection) { CommandTimeout = CommandTimeoutSeconds };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            string login = null;
            int isSysAdmin = 0;
            if (await reader.ReadAsync(cancellationToken))
            {
                login = reader.IsDBNull(0) ? null : reader.GetString(0);
                isSysAdmin = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
            if (!string.Equals(login, expectedUser, StringComparison.OrdinalIgnoreCase))
            {
                throw configurationError($"Połączenie SQL nie idzie na koncie DB_READ_USER - przerywam odczyt {what}.");
            }
            if (isSysAdmin != 0)
            {
                throw configurationError($"Konto DB_READ_USER ma rolę sysadmin - to nie jest konto tylko do odczytu. Odczyt {what} odrzucony.");
            }
        }

        /// <summary>Zwraca nazwę bazy z sys.databases (online) albo null, gdy takiej bazy nie ma lub nazwa jest niepoprawna.</summary>
        public static async Task<string> ResolveDatabaseAsync(SqlConnection master, string databaseName, CancellationToken cancellationToken)
        {
            if (!IsValidDatabaseName(databaseName)) return null;
            await using var command = new SqlCommand("SELECT name FROM sys.databases WHERE name = @name AND state = 0", master) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, MaxDatabaseNameLength).Value = databaseName;
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
    }
}
