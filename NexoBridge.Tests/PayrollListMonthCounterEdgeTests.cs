using System;
using System.Collections.Generic;
using System.Linq;
using NexoBridge.Services;
using Xunit;

namespace NexoBridge.Tests
{
    // Dodatkowe przypadki brzegowe liczenia kadr wg miesiaca listy (granica miesiaca, puste/nieznane wiersze, grupy).
    public class PayrollListMonthCounterEdgeTests
    {
        private static HashSet<string> Groups(params string[] names)
            => names.Select(PayrollGroupCounter.NormalizeGroupKey).ToHashSet(StringComparer.Ordinal);

        private static PayrollListMonthCounter.PayrollCountRow Row(string title, PayrollDocumentKind kind, int? person, int count)
            => new PayrollListMonthCounter.PayrollCountRow(title, kind, person, count);

        [Theory]
        [InlineData(2028, 2, "2028-02-01", "2028-03-01")]   // rok przestepny
        [InlineData(2026, 1, "2026-01-01", "2026-02-01")]
        [InlineData(2026, 11, "2026-11-01", "2026-12-01")]
        public void ResolvePeriodRange_GranicaMiesiaca(int y, int m, string s, string e)
        {
            var (start, end) = PayrollLineSpecExtractor.ResolvePeriodRange(y, m);
            Assert.Equal(DateTime.Parse(s), start);
            Assert.Equal(DateTime.Parse(e), end);
            Assert.Equal(1, end.Day);
        }

        [Theory]
        [InlineData(2026, 0)]
        [InlineData(2026, 13)]
        public void ResolvePeriodRange_BledentMiesiacRzucaWyjatek(int y, int m)
        {
            Assert.ThrowsAny<ArgumentException>(() => PayrollLineSpecExtractor.ResolvePeriodRange(y, m));
        }

        [Fact]
        public void CountRows_WypłataBezPracownikaNieJestLiczona()
        {
            var rows = new[]
            {
                Row("Robotnicy", PayrollDocumentKind.Payout, null, 3),
                Row("Robotnicy", PayrollDocumentKind.Payout, 5, 1),
            };
            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("Robotnicy")));
        }

        [Fact]
        public void CountRows_WierszeNieznanegoRodzajuIgnorowane()
        {
            var rows = new[]
            {
                Row("Robotnicy", PayrollDocumentKind.Unknown, 1, 10),
                Row("Robotnicy", PayrollDocumentKind.Payout, 2, 1),
            };
            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("Robotnicy")));
            Assert.Equal(0, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.ContractBill, Groups("Robotnicy")));
        }

        [Fact]
        public void CountRows_TenSamPracownikWieleDokumentowToJedenNawetWRamachJednejListy()
        {
            var rows = new[] { Row("LP 1/10/2026 (A)", PayrollDocumentKind.Payout, 9, 4) };
            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("A")));
        }

        [Fact]
        public void CountRows_PracownikWWybranejINiewybranejGrupieLiczySieJednokrotnie()
        {
            var rows = new[]
            {
                Row("LP 1/10/2026 (A)", PayrollDocumentKind.Payout, 9, 1),
                Row("LP 2/10/2026 (B)", PayrollDocumentKind.Payout, 9, 1),
            };
            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("A")));
            Assert.Equal(1, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, Groups("B")));
        }

        [Fact]
        public void CountRows_PustyWyborGrupZwracaZero()
        {
            var rows = new[] { Row("Robotnicy", PayrollDocumentKind.Payout, 1, 1) };
            Assert.Equal(0, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.Payout, new HashSet<string>()));
        }

        [Fact]
        public void CountRows_NullowyTytulListyToBezOznaczenia()
        {
            var rows = new[] { Row(null, PayrollDocumentKind.ContractBill, null, 2) };
            Assert.Equal(2, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.ContractBill, Groups("__untagged__")));
            Assert.Equal(0, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.ContractBill, Groups("A")));
        }

        [Fact]
        public void CountRows_GrupaNiewrazliwaNaWielkoscLiterISpacje()
        {
            var rows = new[] { Row("LP 1/10/2026 (  Dzial   A )", PayrollDocumentKind.ContractBill, null, 3) };
            Assert.Equal(3, PayrollListMonthCounter.CountRows(rows, PayrollDocumentKind.ContractBill, Groups("DZIAL a")));
        }

        [Fact]
        public void ResolveKind_PPKNieMaLicznikaWiecNieJestRozpoznawany()
        {
            // pozycje PPK maja IsPpk=true i nie przechodza przez ResolveKind (Program.cs: counterLines = !IsPpk)
            var spec = new PendingPayrollLineSpec("PPK", Guid.Empty, new List<PayrollTierSpec>(), IsPpk: true);
            Assert.True(spec.IsPpk);
            Assert.Throws<InvalidOperationException>(() => PayrollListMonthCounter.ResolveKind(spec.CounterGuid));
        }

        [Fact]
        public void BuildOptions_RachunkiWPodgladzieGrupLiczaDokumentyWMiesiacuListy()
        {
            var rows = new[]
            {
                new PayrollGroupCounter.PayrollGroupRow(new DateTime(2026, 10, 1), "Lista rachunków", PayrollDocumentKind.ContractBill, null, 7),
                new PayrollGroupCounter.PayrollGroupRow(new DateTime(2026, 9, 1), "Lista rachunków", PayrollDocumentKind.ContractBill, null, 8),
            };
            var opt = Assert.Single(PayrollGroupCounter.BuildOptions(rows, 2026, 10));
            Assert.Equal(7, opt.ContractBillCount);
            Assert.Equal(15, opt.HistoryCount);
            Assert.Equal(0, opt.PayoutCount);
        }

        [Fact]
        public void BuildOptions_PracownikWDwochGrupachLiczonyWKazdejOsobno()
        {
            var rows = new[]
            {
                new PayrollGroupCounter.PayrollGroupRow(new DateTime(2026, 10, 1), "LP 1/10/2026 (A)", PayrollDocumentKind.Payout, 1, 1),
                new PayrollGroupCounter.PayrollGroupRow(new DateTime(2026, 10, 1), "LP 2/10/2026 (B)", PayrollDocumentKind.Payout, 1, 1),
            };
            var opts = PayrollGroupCounter.BuildOptions(rows, 2026, 10);
            Assert.Equal(2, opts.Count);
            Assert.All(opts, o => Assert.Equal(1, o.PayoutCount));
        }

        [Fact]
        public void BuildOptions_BrakWierszyDajePustaListe()
        {
            Assert.Empty(PayrollGroupCounter.BuildOptions(Array.Empty<PayrollGroupCounter.PayrollGroupRow>(), 2026, 10));
        }
    }
}
