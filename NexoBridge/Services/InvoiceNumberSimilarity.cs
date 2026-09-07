using System;

namespace NexoBridge.Services
{
    /// <summary>Ciągłe (0-100) podobieństwo numerów faktur - dla rocznego skanu duplikatów
    /// (InvoiceDuplicateDetectionService.SprawdzDuplikatyRoczneAsync). InvoiceDocumentMatcher
    /// robi tylko boolowskie "czy to bezpieczne dopasowanie" (do innego celu - dopasowywania
    /// metadanych OCR do dokumentów Rachmistrza), więc prawdziwe fuzzy matching numerów
    /// dopisujemy tutaj, reużywając jego IsSafeNumberMatch jako skrót do 100%.</summary>
    public static class InvoiceNumberSimilarity
    {
        public static decimal NumberSimilarityPercent(string normalizedA, string normalizedB)
        {
            if (string.IsNullOrWhiteSpace(normalizedA) || string.IsNullOrWhiteSpace(normalizedB))
            {
                return 0m;
            }

            if (string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase))
            {
                return 100m;
            }

            if (InvoiceDocumentMatcher.IsSafeNumberMatch(normalizedA, normalizedB))
            {
                return 100m;
            }

            return LevenshteinRatio(normalizedA, normalizedB);
        }

        public static decimal LevenshteinRatio(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return 0m;
            }

            int[,] distances = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) distances[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) distances[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    distances[i, j] = Math.Min(
                        Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                        distances[i - 1, j - 1] + cost);
                }
            }

            int maxLength = Math.Max(a.Length, b.Length);
            decimal ratio = (1m - (decimal)distances[a.Length, b.Length] / maxLength) * 100m;
            return Math.Round(ratio, 2, MidpointRounding.AwayFromZero);
        }
    }
}
