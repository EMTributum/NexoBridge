using System;
using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using static NexoBridge.Services.SferaReflectionHelpers;

namespace NexoBridge.Services
{
    /// <summary>
    /// Pozycja cennika "Obsługa PPK": nexo nie ma wbudowanego licznika PPK (pozycja w cenniku biura nie ma
    /// FunkcjiZliczajacej), więc ilość liczymy sami - prostym, tylko-do-odczytu SELECT-em w bazie Gratyfikanta
    /// klienta (bez Sfery). Ilość = liczba RÓŻNYCH pracowników, którzy na listach płac i listach rachunków
    /// z danego miesiąca mają niezerową podstawową wpłatę pracodawcy na PPK (Wynagrodzenia.WplataPodstawowaPracodawcyNaPPK).
    /// Zapytanie sprawdzone w NexoBridgeKonsola (--scan-dbs-sql) na wszystkich bazach Nexo_* za 09 i 10.2026.
    /// Cena i przedziały pochodzą z cennika (PayrollLineSpecExtractor.BuildLine), tak jak przy wypłatach.
    /// </summary>
    internal static class PayrollPpkCounter
    {
        private const string Sql = @"
            SELECT COUNT(DISTINCT u.Pracownik_Id)
            FROM ModelDanychContainer.Wynagrodzenia w
            JOIN ModelDanychContainer.ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
            JOIN ModelDanychContainer.UmowyPracownicze u ON u.Id = w.Umowa_Id
            WHERE l.Miesiac >= @periodStart AND l.Miesiac < @periodEnd
              AND w.WplataPodstawowaPracodawcyNaPPK <> 0";

        // PPK jako osobne słowo (po normalizacji: wielkie litery, bez ogonków) - "Obsługa PPK" tak, "PPKOWY" nie.
        private static readonly Regex PpkWord = new Regex(@"(?<![A-Z0-9])PPK(?![A-Z0-9])", RegexOptions.CultureInvariant);

        public static bool IsPpkName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && PpkWord.IsMatch(NormalizeText(name));
        }

        /// <summary>Wołane w procesie potomnym (po jednym kliencie), więc synchronicznie. Błąd bazy leci wyżej -
        /// wołający zgłasza FAILED, zamiast po cichu pominąć pozycję.</summary>
        public static int CountEmployees(string databaseName, DateTime periodStart, DateTime periodEnd)
        {
            using var connection = new SqlConnection(OfficeReportsSqlService.BuildConnectionString(databaseName));
            connection.Open();
            using var command = new SqlCommand(Sql, connection) { CommandTimeout = 60 };
            command.Parameters.Add("@periodStart", SqlDbType.DateTime2).Value = periodStart;
            command.Parameters.Add("@periodEnd", SqlDbType.DateTime2).Value = periodEnd;
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }
}
