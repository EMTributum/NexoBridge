using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class YearlyDuplicateScanJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public string Nip { get; set; }
        public int Year { get; set; }
    }

    public class YearlyDuplicateScanReport
    {
        public string JobId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string Nip { get; set; }
        public int Year { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
        public List<YearlyDuplicateMatch> Matches { get; set; } = new List<YearlyDuplicateMatch>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>Para potencjalnych duplikatów z rocznego skanu - w odróżnieniu od PotentialInvoiceDuplicate
    /// (ścieżka automatyczna, miesięczna) niesie rozbicie wyniku (podobieństwo numeru osobno od wyniku
    /// łącznego) i dwupoziomową flagę duplicate/review - patrz InvoiceDuplicateDetectionService.PorownajRoczny.</summary>
    public class YearlyDuplicateMatch
    {
        public string InvoiceNumber { get; set; }
        public string DuplicateInvoiceNumber { get; set; }
        public string Source { get; set; }
        public decimal NumberSimilarityPercent { get; set; }
        public decimal CompositeScorePercent { get; set; }

        /// <summary>"duplicate" (numberSim &gt; 80% i kwota/kontrahent/data identyczne) albo
        /// "review" (wynik łączny &gt; 70%, bez wymogu identyczności pozostałych kryteriów).</summary>
        public string Tier { get; set; }
    }
}
