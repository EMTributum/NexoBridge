using System;
using System.Collections.Generic;
using System.Linq;
using NexoBridge.Services;
using Xunit;

namespace NexoBridge.Tests
{
    // Liczenie pozycji kadrowych wg miesiaca listy (a nie daty wyplaty/rachunku): okres, rozpoznanie licznika,
    // miara (wyplaty = osoby, rachunki = dokumenty) i filtr grup. Samo SQL jest sprawdzone na zywych bazach w konsoli.
    public class PayrollListMonthCounterTests
    {
        private static readonly Guid PayoutGuid = new Guid("65AC6E7B-7EF3-42EE-A36F-F7F1BF6E0689");
        private static readonly Guid BillGuid = new Guid("B11A216C-6C8E-476A-850B-422ED025ECBD");

        private static HashSet<string> Groups(params string[] names)
            => names.Select(PayrollGroupCounter.NormalizeGroupKey).ToHashSet(StringComparer.Ordinal);

        private static PayrollListMonthCounter.PayrollCountRow Payout(string title, int person, int count = 1)
            => new PayrollListMonthCounter.PayrollCountRow(title, PayrollDocumentKind.Payout, person, count);

        private static PayrollListMonthCounter.PayrollCountRow Bill(string title, int count)
            => new PayrollListMonthCounter.PayrollCountRow(title, PayrollDocumentKind.ContractBill, null, count);

        // ---- okres ----

        [Theory]
        [InlineData(2026, 10, "2026-10-01", "2026-11-01")]
        [InlineData(2026, 12, "2026-12-01", "2027-01-01")]
        [InlineData(2026, 2, "2026-02-01", "2026-03-01")]
        public void ResolvePeriodRange_KoniecToPierwszyDzienNastepnegoMiesiaca(int year, int month, string start, string end)
        {
            (DateTime s, DateTime e) = PayrollLineSpecExtractor.ResolvePeriodRange(year, month);
            Assert.Equal(DateTime.Parse(start), s);
            Assert.Equal(DateTime.Parse(end), e);
        }

        // ---- rozpoznawanie rodzaju licznika ----

        [Fact]
        public void ResolveKind_RozpoznajeLicznikWyplatIRachunkow()
        {
            Assert.Equal(PayrollDocumentKind.Payout, PayrollListMonthCounter.ResolveKind(PayoutGuid));
            Assert.Equal(PayrollDocumentKind.ContractBill, PayrollListMonthCounter.ResolveKind(BillGuid));
        }

        [Fact]
        public void ResolveKind_NieznanyLicznikRzucaWyjatek()
        {
            // Licznik "Wyplata" wg daty wyplaty (e9bf4b82...) i pusty GUID nie moga po cichu zniknac z faktury.
            Assert.Throws<InvalidOperationException>(
                () => PayrollListMonthCounter.ResolveKind(new Guid("E9BF4B82-0927-4900-B4AA-2AC1C3DDD697")));
            Assert.Throws<InvalidOperationException>(() => PayrollListMonthCounter.ResolveKind(Guid.Empty));
        }

        [Fact]
        public void ComputeLine_NieznanyLicznikRzucaWyjatekZamiastZwracacNull()
        {
            var spec = new PendingPayrollLineSpec("Nieznana", Guid.NewGuid(), new List<PayrollTierSpec>());
            Assert.Throws<InvalidOperationException>(
                () => PayrollLineSpecExtractor.ComputeLine(spec, "Nexo_Test", new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)));
            Assert.Throws<InvalidOperationException>(
                () => PayrollLineSpecExtractor.ComputeFilteredLine(spec, "Nexo_Test", new[] { "a" }, new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)));
        }

        // ---- miara: wyplaty = osoby, rachunki = dokumenty (z filtrem grup) ----

        [Fact]
        public void CountRows_WyplatyLiczaRoznychPracownikowNieDokumenty()
        {
            // Ten sam pracownik (100005) na dwoch listach jednego miesiaca - jedna pozycja faktury (jak WWS: 25 dok. = 24 osoby).
            var rows = new[]
            {
                Payout("LP 6/10/2026", 100005),
                Payout("LP 8/10/2026", 100005),
                Payout("LP 2/10/2026", 100006),
            };

            Assert.Equal(2, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("__untagged__")));
        }

        [Fact]
        public void CountRows_RachunkiLiczaDokumenty()
        {
            var rows = new[] { Bill("LR 1/10/2026", 8), Bill("LR 2/10/2026", 8), Bill("LR 3/10/2026", 9) };

            Assert.Equal(25, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.ContractBill, Groups("__untagged__")));
        }

        [Fact]
        public void CountRows_LiczyTylkoRodzajPozycji()
        {
            var rows = new[] { Payout("LP 1/10/2026", 1), Bill("LR 1/10/2026", 5) };

            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("__untagged__")));
            Assert.Equal(5, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.ContractBill, Groups("__untagged__")));
        }

        [Fact]
        public void CountRows_FiltrGrupLiczyTylkoWybraneListy()
        {
            var rows = new[]
            {
                Payout("LP 1/10/2026 (Dzial A)", 1),
                Payout("LP 2/10/2026 (Dzial B)", 2),
                Payout("LP 3/10/2026", 3),
                Bill("LR 1/10/2026 (Dzial A)", 4),
                Bill("LR 2/10/2026 (Dzial B)", 6),
            };

            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("dzial a")));
            Assert.Equal(2, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("Dzial A", "Dzial B")));
            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("__untagged__")));
            Assert.Equal(4, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.ContractBill, Groups("Dzial A")));
            Assert.Equal(10, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.ContractBill, Groups("Dzial A", "Dzial B")));
        }

        [Fact]
        public void CountRows_PracownikWDwochWybranychGrupachLiczySieRaz()
        {
            var rows = new[]
            {
                Payout("LP 1/10/2026 (Dzial A)", 7),
                Payout("LP 2/10/2026 (Dzial B)", 7),
            };

            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("Dzial A", "Dzial B")));
        }

        [Fact]
        public void CountRows_BezPasujacychListZwracaZero()
        {
            Assert.Equal(0, PayrollListMonthCounter.CountRows(Array.Empty<PayrollListMonthCounter.PayrollCountRow>(), PayrollDocumentKind.Payout, Groups("a")));
        }

        // ---- podglad grup (DiscoverAsync): ta sama miara co faktura ----

        private static PayrollGroupCounter.PayrollGroupRow GroupRow(string month, string title, PayrollDocumentKind kind, int? person, int count = 1)
            => new PayrollGroupCounter.PayrollGroupRow(DateTime.Parse(month), title, kind, person, count);

        [Fact]
        public void BuildOptions_WyplatyWPodgladzieGrupLiczaOsobyJakNaFakturze()
        {
            var rows = new[]
            {
                // 9 dokumentow, 5 osob (jak JURKOP) - dwa wiersze tej samej osoby na roznych listach
                GroupRow("2026-10-01", "LP 1/10/2026 (Dzial A)", PayrollDocumentKind.Payout, 1),
                GroupRow("2026-10-01", "LP 2/10/2026 (Dzial A)", PayrollDocumentKind.Payout, 1),
                GroupRow("2026-10-01", "LP 2/10/2026 (Dzial A)", PayrollDocumentKind.Payout, 2, 3),
                GroupRow("2026-10-01", "LP 3/10/2026 (Dzial A)", PayrollDocumentKind.Payout, 3),
                GroupRow("2026-10-01", "LP 3/10/2026 (Dzial A)", PayrollDocumentKind.Payout, 4),
                GroupRow("2026-10-01", "LP 3/10/2026 (Dzial A)", PayrollDocumentKind.Payout, 5),
                GroupRow("2026-10-01", "LR 1/10/2026 (Dzial A)", PayrollDocumentKind.ContractBill, null, 4),
                GroupRow("2026-10-01", "LP 4/10/2026", PayrollDocumentKind.Payout, 9),
            };

            var options = PayrollGroupCounter.BuildOptions(rows, 2026, 10);

            var a = options.Single(o => o.Label == "Dzial A");
            Assert.Equal(5, a.PayoutCount);
            Assert.Equal(4, a.ContractBillCount);
            Assert.Equal(9, a.HistoryCount);
            Assert.Equal(1, options.Single(o => o.Key == PayrollGroupCounter.UntaggedGroupKey).PayoutCount);
        }

        [Fact]
        public void BuildOptions_HistoriaSumujeMiesieczneWartosciIOdrzucaNieznanyRodzaj()
        {
            var rows = new[]
            {
                GroupRow("2026-09-01", "LP 1/09/2026 (A)", PayrollDocumentKind.Payout, 1),
                GroupRow("2026-09-01", "LP 2/09/2026 (A)", PayrollDocumentKind.Payout, 1),
                GroupRow("2026-10-01", "LP 1/10/2026 (A)", PayrollDocumentKind.Payout, 1),
                GroupRow("2026-10-01", "LP 9/10/2026 (A)", PayrollDocumentKind.Unknown, 7, 50),
            };

            var option = Assert.Single(PayrollGroupCounter.BuildOptions(rows, 2026, 10));

            Assert.Equal(1, option.PayoutCount);      // biezacy miesiac: jedna osoba
            Assert.Equal(2, option.HistoryCount);     // wrzesien 1 osoba + pazdziernik 1 osoba
            Assert.Equal(new DateTime(2026, 10, 1), option.LastSeenAt);
        }

        // ---- filtr grup wymaga grupy i rozpoznanego rodzaju ----

        [Fact]
        public void CountFiltered_BezGrupLubZNieznanymRodzajemRzucaWyjatek()
        {
            DateTime start = new DateTime(2026, 10, 1);
            DateTime end = new DateTime(2026, 11, 1);

            Assert.Throws<InvalidOperationException>(
                () => PayrollGroupCounter.CountFiltered("Nexo_Test", PayrollDocumentKind.Payout, Array.Empty<string>(), start, end));
            Assert.Throws<InvalidOperationException>(
                () => PayrollGroupCounter.CountFiltered("Nexo_Test", PayrollDocumentKind.Unknown, new[] { "a" }, start, end));
        }

        // ---- cena z cennika: ilosc -> pozycja faktury ----

        [Fact]
        public void BuildLine_CenaJednostkowaMnozyPrzezIlosc()
        {
            var spec = new PendingPayrollLineSpec(
                "Wyplata wg miesiaca rozliczenia", PayoutGuid,
                new List<PayrollTierSpec> { new PayrollTierSpec(1, int.MaxValue, 70m, 86.1m, null, null) });

            var line = PayrollLineSpecExtractor.BuildLine(spec, 24);

            Assert.NotNull(line);
            Assert.Equal(24m, line.Quantity);
            Assert.Equal(1680m, line.Net);
        }

        [Fact]
        public void BuildLine_ZeroSztukNieDajePozycji()
        {
            var spec = new PendingPayrollLineSpec(
                "Rachunek", BillGuid, new List<PayrollTierSpec> { new PayrollTierSpec(1, int.MaxValue, 10m, 12.3m, null, null) });

            Assert.Null(PayrollLineSpecExtractor.BuildLine(spec, 0));
        }
    }
}
