using InsERT.Moria.ModelDanych;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NexoBridge.Services
{
    public static class InvoiceDocumentMatcher
    {
        private static readonly HashSet<string> CommonTrailingMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fv", "fvs", "fa", "fs", "faktura", "vat"
        };

        public static InvoiceMatchResult Match(IEnumerable<DokumentDoKsiegowania> documents, InvoiceMetadata meta)
        {
            if (meta == null)
            {
                return InvoiceMatchResult.NotFound("Brak metadanych faktury.");
            }

            string numberFront = Normalize(meta.InvoiceNumber);
            if (string.IsNullOrWhiteSpace(numberFront))
            {
                return InvoiceMatchResult.NotFound("Brak numeru faktury w metadanych.");
            }

            var allDocuments = (documents ?? Enumerable.Empty<DokumentDoKsiegowania>()).ToList();
            string nipFront = NormalizeNip(meta.VendorNip);
            if (string.IsNullOrWhiteSpace(nipFront))
            {
                var exactWithoutNip = Resolve(
                    allDocuments,
                    d => GetNormalizedDocumentNumbers(d).Any(n => n == numberFront),
                    "matchedExactWithoutNip",
                    numberFront);

                if (exactWithoutNip.IsTerminal) return exactWithoutNip;

                return InvoiceMatchResult.NotFound($"Brak jednoznacznego dokumentu dla numeru {meta.InvoiceNumber} bez NIP. Bez NIP dopuszczamy tylko dokładne dopasowanie numeru.", allDocuments);
            }

            var nipCandidates = allDocuments
                .Where(d => NormalizeNip(d.PodmiotHistoria?.NIP).EndsWith(nipFront, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (nipCandidates.Count == 0)
            {
                return InvoiceMatchResult.NotFound($"Brak dokumentu z NIP {meta.VendorNip}.");
            }

            var exact = Resolve(nipCandidates, d => GetNormalizedDocumentNumbers(d).Any(n => n == numberFront), "matchedExact", numberFront);
            if (exact.IsTerminal) return exact;

            var suffixFull = Resolve(nipCandidates, d => GetNormalizedDocumentNumbers(d).Any(n => n.EndsWith(numberFront, StringComparison.OrdinalIgnoreCase)), "matchedByFullSuffix", numberFront);
            if (suffixFull.IsTerminal) return suffixFull;

            var truncatedFull = Resolve(nipCandidates, d => GetNormalizedDocumentNumbers(d).Any(n => IsSafeNumberMatch(numberFront, n)), "matchedByControlledTruncation", numberFront);
            if (truncatedFull.IsTerminal) return truncatedFull;

            foreach (string variant in GenerateNumberVariants(meta.InvoiceNumber))
            {
                var byVariantExact = Resolve(nipCandidates, d => GetNormalizedDocumentNumbers(d).Any(n => n == variant), "matchedByVariant", variant);
                if (byVariantExact.IsTerminal) return byVariantExact;

                var byVariantSuffix = Resolve(nipCandidates, d => GetNormalizedDocumentNumbers(d).Any(n => n.EndsWith(variant, StringComparison.OrdinalIgnoreCase)), "matchedByVariantSuffix", variant);
                if (byVariantSuffix.IsTerminal) return byVariantSuffix;

                var byVariantTruncation = Resolve(nipCandidates, d => GetNormalizedDocumentNumbers(d).Any(n => IsSafeNumberMatch(variant, n)), "matchedByVariantControlledTruncation", variant);
                if (byVariantTruncation.IsTerminal) return byVariantTruncation;
            }

            return InvoiceMatchResult.NotFound($"Nie znaleziono jednoznacznego dokumentu dla numeru {meta.InvoiceNumber} i NIP {meta.VendorNip}.", nipCandidates);
        }

        public static InvoiceMatchResult MatchByExactKsefAndNip(IEnumerable<DokumentDoKsiegowania> documents, InvoiceMetadata meta)
        {
            string nipFront = NormalizeNip(meta?.VendorNip);
            string ksefFront = NormalizeKsef(meta?.KsefNumber);
            if (string.IsNullOrWhiteSpace(nipFront) || string.IsNullOrWhiteSpace(ksefFront))
            {
                return InvoiceMatchResult.Empty();
            }

            var matches = (documents ?? Enumerable.Empty<DokumentDoKsiegowania>())
                .Where(d => string.Equals(NormalizeNip(d.PodmiotHistoria?.NIP), nipFront, StringComparison.OrdinalIgnoreCase))
                .Where(d => string.Equals(NormalizeKsef(d.NumerKSeF), ksefFront, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1)
            {
                return InvoiceMatchResult.Matched("matchedByExactKsefAndNip", matches[0], ksefFront);
            }

            if (matches.Count > 1)
            {
                return InvoiceMatchResult.Ambiguous($"Znaleziono {matches.Count} dokumentow z tym samym NIP i numerem KSeF.", matches, ksefFront);
            }

            return InvoiceMatchResult.Empty();
        }

        public static InvoiceMetadataMatchResult MatchMetadataForDocument(IEnumerable<InvoiceMetadata> metadata, DokumentDoKsiegowania document)
        {
            var metadataList = (metadata ?? Enumerable.Empty<InvoiceMetadata>()).ToList();
            var matches = metadataList
                .Select(m => new { Meta = m, Match = Match(new[] { document }, m) })
                .Where(x => x.Match.Document != null)
                .ToList();

            if (matches.Count == 1)
            {
                return new InvoiceMetadataMatchResult
                {
                    Status = matches[0].Match.Status,
                    Metadata = matches[0].Meta,
                    MatchedVariant = matches[0].Match.MatchedVariant
                };
            }

            if (matches.Count > 1)
            {
                return new InvoiceMetadataMatchResult
                {
                    Status = "ambiguous",
                    Reason = "Wiele wpisow metadanych pasuje do tego samego dokumentu."
                };
            }

            var ksefMatches = metadataList
                .Select(m => new { Meta = m, Match = MatchByExactKsefAndNip(new[] { document }, m) })
                .Where(x => x.Match.Document != null)
                .ToList();

            if (ksefMatches.Count == 1)
            {
                return new InvoiceMetadataMatchResult
                {
                    Status = ksefMatches[0].Match.Status,
                    Metadata = ksefMatches[0].Meta,
                    MatchedVariant = ksefMatches[0].Match.MatchedVariant
                };
            }

            if (ksefMatches.Count > 1)
            {
                return new InvoiceMetadataMatchResult
                {
                    Status = "ambiguous",
                    Reason = "Wiele wpisow metadanych ma ten sam NIP i numer KSeF."
                };
            }

            return new InvoiceMetadataMatchResult
            {
                Status = "notFound",
                Reason = "Brak metadanych pasujacych do dokumentu."
            };
        }

        public static IEnumerable<string> GenerateNumberVariants(string input)
        {
            var variants = new List<string>();
            string full = Normalize(input);
            AddVariant(variants, full);

            var tokens = Regex.Matches(input ?? string.Empty, "[A-Za-z0-9]+")
                .Cast<Match>()
                .Select(m => m.Value.ToLowerInvariant())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (tokens.Count > 1 && CommonTrailingMarkers.Contains(tokens[^1]))
            {
                AddVariant(variants, string.Concat(tokens.Take(tokens.Count - 1)));
            }

            for (int end = tokens.Count; end >= 2; end--)
            {
                AddVariant(variants, string.Concat(tokens.Take(end)));
            }

            for (int start = 0; start < tokens.Count; start++)
            {
                for (int length = tokens.Count - start; length >= 2; length--)
                {
                    AddVariant(variants, string.Concat(tokens.Skip(start).Take(length)));
                }
            }

            return variants
                .Where(v => v.Length >= 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => v.Length)
                .ThenBy(v => v);
        }

        public static bool IsSafeNumberMatch(string expectedNormalized, string actualNormalized)
        {
            if (string.IsNullOrWhiteSpace(expectedNormalized) || string.IsNullOrWhiteSpace(actualNormalized)) return false;
            if (expectedNormalized.Length < 8 || actualNormalized.Length < 8) return false;

            if (string.Equals(expectedNormalized, actualNormalized, StringComparison.OrdinalIgnoreCase)) return true;
            if (actualNormalized.EndsWith(expectedNormalized, StringComparison.OrdinalIgnoreCase)) return true;

            int minControlledPrefixLength = Math.Min(12, expectedNormalized.Length);
            bool expectedWasTrimmedAtEnd = expectedNormalized.StartsWith(actualNormalized, StringComparison.OrdinalIgnoreCase) &&
                                           actualNormalized.Length >= minControlledPrefixLength;
            if (expectedWasTrimmedAtEnd) return true;

            bool actualWasTrimmedAtEnd = actualNormalized.StartsWith(expectedNormalized, StringComparison.OrdinalIgnoreCase) &&
                                         expectedNormalized.Length >= Math.Min(12, actualNormalized.Length);
            return actualWasTrimmedAtEnd;
        }

        public static string Normalize(string input)
        {
            return input == null ? string.Empty : new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        public static string NormalizeNip(string input)
        {
            string normalized = Normalize(input);
            return normalized.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? normalized.Substring(2) : normalized;
        }

        public static string NormalizeKsef(string input)
        {
            string normalized = input?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
        }

        public static string Describe(DokumentDoKsiegowania document)
        {
            if (document == null) return "brak";
            return $"Nr={document.Nr}, Id={document.Id}, Numer={document.NumerDokumentu}, NIP={document.PodmiotHistoria?.NIP}, KSeF={document.NumerKSeF ?? "brak"}";
        }

        private static InvoiceMatchResult Resolve(List<DokumentDoKsiegowania> candidates, Func<DokumentDoKsiegowania, bool> predicate, string status, string variant)
        {
            var matches = candidates.Where(predicate).ToList();
            if (matches.Count == 1)
            {
                return InvoiceMatchResult.Matched(status, matches[0], variant);
            }

            if (matches.Count > 1)
            {
                return InvoiceMatchResult.Ambiguous($"Znaleziono {matches.Count} kandydatow dla wariantu {variant}.", matches, variant);
            }

            return InvoiceMatchResult.Empty();
        }

        private static IEnumerable<string> GetNormalizedDocumentNumbers(DokumentDoKsiegowania document)
        {
            if (document == null) yield break;

            string raw = document.NumerDokumentu ?? string.Empty;
            string normalizedRaw = Normalize(raw);
            if (!string.IsNullOrWhiteSpace(normalizedRaw)) yield return normalizedRaw;

            string withoutSystemPrefix = Regex.Replace(raw, @"^(FZ|FS|FZK|FSK|PA)\s+\d+\s+", string.Empty, RegexOptions.IgnoreCase);
            string normalizedWithoutPrefix = Normalize(withoutSystemPrefix);
            if (!string.IsNullOrWhiteSpace(normalizedWithoutPrefix) &&
                !string.Equals(normalizedWithoutPrefix, normalizedRaw, StringComparison.OrdinalIgnoreCase))
            {
                yield return normalizedWithoutPrefix;
            }

            string withoutLeadingTechnicalNumber = Regex.Replace(withoutSystemPrefix, @"^\d+\s+", string.Empty, RegexOptions.IgnoreCase);
            string normalizedWithoutTechnicalNumber = Normalize(withoutLeadingTechnicalNumber);
            if (!string.IsNullOrWhiteSpace(normalizedWithoutTechnicalNumber) &&
                !string.Equals(normalizedWithoutTechnicalNumber, normalizedRaw, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedWithoutTechnicalNumber, normalizedWithoutPrefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return normalizedWithoutTechnicalNumber;
            }
        }

        private static void AddVariant(List<string> variants, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                variants.Add(value);
            }
        }
    }

    public class InvoiceMatchResult
    {
        public string Status { get; set; }
        public string Reason { get; set; }
        public DokumentDoKsiegowania Document { get; set; }
        public string MatchedVariant { get; set; }
        public List<string> Candidates { get; set; } = new List<string>();
        public bool IsTerminal => Status != "empty";

        public static InvoiceMatchResult Empty() => new InvoiceMatchResult { Status = "empty" };

        public static InvoiceMatchResult Matched(string status, DokumentDoKsiegowania document, string variant)
        {
            return new InvoiceMatchResult
            {
                Status = status,
                Document = document,
                MatchedVariant = variant,
                Candidates = new List<string> { InvoiceDocumentMatcher.Describe(document) }
            };
        }

        public static InvoiceMatchResult NotFound(string reason, IEnumerable<DokumentDoKsiegowania> candidates = null)
        {
            return new InvoiceMatchResult
            {
                Status = "notFound",
                Reason = reason,
                Candidates = (candidates ?? Enumerable.Empty<DokumentDoKsiegowania>()).Take(20).Select(InvoiceDocumentMatcher.Describe).ToList()
            };
        }

        public static InvoiceMatchResult Ambiguous(string reason, IEnumerable<DokumentDoKsiegowania> candidates, string variant)
        {
            return new InvoiceMatchResult
            {
                Status = "ambiguous",
                Reason = reason,
                MatchedVariant = variant,
                Candidates = (candidates ?? Enumerable.Empty<DokumentDoKsiegowania>()).Take(20).Select(InvoiceDocumentMatcher.Describe).ToList()
            };
        }
    }

    public class InvoiceMetadataMatchResult
    {
        public string Status { get; set; }
        public string Reason { get; set; }
        public InvoiceMetadata Metadata { get; set; }
        public string MatchedVariant { get; set; }
    }
}
