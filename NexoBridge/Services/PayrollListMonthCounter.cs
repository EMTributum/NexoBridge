using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;

namespace NexoBridge.Services
{
    /// <summary>
    /// Liczy pozycje kadrowe faktury rozliczeniowej biura wg MIESIĄCA LISTY płac/rachunków
    /// (ListyWynagrodzen.Miesiac): faktura za miesiąc M = wypłaty z list płac "LP x/M" + rachunki z list
    /// rachunków "LR x/M". Liczymy też listy niezatwierdzone (Zatwierdzona = 0). Zwykłe, tylko-do-odczytu
    /// SELECT-y w bazie klienta - bez Sfery (tak jak PayrollPpkCounter, który też liczy po miesiącu listy).
    ///
    /// Dotyczy dwóch pozycji cennika z licznikiem nexo (jedyne liczniki używane w cennikach biura):
    ///   "Wypłata wg miesiąca rozliczenia"  - licznik 65AC6E7B-7EF3-42EE-A36F-F7F1BF6E0689,
    ///   "Rachunek do umowy pracowniczej"   - licznik B11A216C-6C8E-476A-850B-422ED025ECBD.
    ///
    /// WYPŁATY: to przeniesienie dotychczasowego liczenia ze Sfery na SQL, wynik bez zmian. Licznik nexo
    /// "wg miesiąca rozliczenia" już liczył wg miesiąca listy i w OSOBACH (potwierdzone na fakturach: np. 9 dokumentów,
    /// 5 osób, na fakturze 5), choć jego ZliczSqlCommandString pokazuje COUNT(WW.Id). Ten sam pracownik z wypłatami na
    /// dwóch listach jednego miesiąca (np. LP 6 i LP 8) to jedna pozycja faktury.
    /// RACHUNKI: tu ilość się zmienia - licznik nexo liczył wg DATY rachunku, teraz liczymy DOKUMENTY wg miesiąca listy.
    /// Rachunek zapłacony po przełomie miesiąca (np. stypendia wrześniowe wypłacone 1.10) zostaje w miesiącu swojej listy.
    ///
    /// Nieznany GUID licznika to błąd (patrz ResolveKind), a nie po cichu pominięta pozycja.
    /// </summary>
    internal static class PayrollListMonthCounter
    {
        public static readonly Guid PayoutCounterGuid = new Guid("65AC6E7B-7EF3-42EE-A36F-F7F1BF6E0689");
        public static readonly Guid ContractBillCounterGuid = new Guid("B11A216C-6C8E-476A-850B-422ED025ECBD");

        private const string PayoutSql = @"
            SELECT COUNT(DISTINCT u.Pracownik_Id)
            FROM ModelDanychContainer.Wynagrodzenia w
            JOIN ModelDanychContainer.Wynagrodzenia_Wyplata wy ON wy.Id = w.Id
            JOIN ModelDanychContainer.ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
            JOIN ModelDanychContainer.UmowyPracownicze u ON u.Id = w.Umowa_Id
            WHERE l.Miesiac >= @periodStart AND l.Miesiac < @periodEnd";

        private const string ContractBillSql = @"
            SELECT COUNT_BIG(*)
            FROM ModelDanychContainer.Wynagrodzenia w
            JOIN ModelDanychContainer.Wynagrodzenia_RachunekDoUmowyPracowniczej r ON r.Id = w.Id
            JOIN ModelDanychContainer.ListyWynagrodzen l ON l.Id = w.ListaWynagrodzen_Id
            WHERE l.Miesiac >= @periodStart AND l.Miesiac < @periodEnd";

        /// <summary>Rodzaj pozycji kadrowej po GUID-zie licznika nexo z cennika. Nieznany GUID = wyjątek, żeby
        /// pozycja nie znikała po cichu z faktury (wołający zgłasza FAILED klienta).</summary>
        public static PayrollDocumentKind ResolveKind(Guid counterGuid)
        {
            if (counterGuid == PayoutCounterGuid)
            {
                return PayrollDocumentKind.Payout;
            }
            if (counterGuid == ContractBillCounterGuid)
            {
                return PayrollDocumentKind.ContractBill;
            }

            throw new InvalidOperationException(
                $"Nie rozpoznano licznika kadrowego Nexo {counterGuid} - obsługiwane są tylko 'Wypłata wg miesiąca rozliczenia' i 'Rachunek do umowy pracowniczej'.");
        }

        /// <summary>Ilość wg miesiąca listy dla jednego klienta (bez filtra grup). Wołane w procesie potomnym
        /// (po jednym kliencie), więc synchronicznie. Błąd bazy leci wyżej.</summary>
        public static int Count(string databaseName, PayrollDocumentKind kind, DateTime periodStart, DateTime periodEnd)
        {
            string sql = kind switch
            {
                PayrollDocumentKind.Payout => PayoutSql,
                PayrollDocumentKind.ContractBill => ContractBillSql,
                _ => throw new InvalidOperationException("Nie udało się rozpoznać rodzaju licznika kadrowego Nexo."),
            };

            // Konto tylko do odczytu (DB_READ_USER), walidacja nazwy bazy, odmowa przy sysadminie - jak w PayrollGroupCounter.
            using SqlConnection connection = GratyfikantReadOnlyAccess.OpenReadOnlyConnection(
                databaseName,
                "NexoBridge-PayrollCounts",
                "liczby wypłat i rachunków",
                message => new OfficeReportsConfigurationException(message));
            using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            command.Parameters.Add("@periodStart", SqlDbType.DateTime2).Value = periodStart;
            command.Parameters.Add("@periodEnd", SqlDbType.DateTime2).Value = periodEnd;
            return checked((int)Convert.ToInt64(command.ExecuteScalar()));
        }

        /// <summary>Wiersz do liczenia z filtrem grup: jedna lista (tytuł), rodzaj, pracownik (tylko wypłaty), liczba dokumentów.</summary>
        internal sealed record PayrollCountRow(string ListTitle, PayrollDocumentKind Kind, int? PersonId, int Count);

        /// <summary>Ta sama miara co <see cref="Count"/>, ale tylko z list należących do wybranych grup: wypłaty = różni
        /// pracownicy (ten sam pracownik w dwóch wybranych grupach liczy się raz), rachunki = suma dokumentów.</summary>
        internal static int CountRows(IEnumerable<PayrollCountRow> rows, PayrollDocumentKind kind, ISet<string> selectedGroupKeys)
        {
            IEnumerable<PayrollCountRow> matching = rows
                .Where(row => row.Kind == kind && selectedGroupKeys.Contains(PayrollGroupCounter.ExtractGroup(row.ListTitle).Key));

            return kind == PayrollDocumentKind.Payout
                ? matching.Where(row => row.PersonId.HasValue).Select(row => row.PersonId.Value).Distinct().Count()
                : matching.Sum(row => row.Count);
        }
    }
}
