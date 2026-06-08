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

            string nipFront = NormalizeNip(meta.VendorNip);
            string numberFront = Normalize(meta.InvoiceNumber);
            if (string.IsNullOrWhiteSpace(nipFront) || string.IsNullOrWhiteSpace(numberFront))
            {
                return InvoiceMatchResult.NotFound("Brak numeru faktury albo NIP w metadanych.");
            }

            var allDocuments = (documents ?? Enumerable.Empty<DokumentDoKsiegowania>()).ToList();
            var nipCandidates = allDocuments
                .Where(d => NormalizeNip(d.PodmiotHistoria?.NIP).EndsWith(nipFront, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (nipCandidates.Count == 0)
            {
                return InvoiceMatchResult.NotFound($"Brak dokumentu z NIP {meta.VendorNip}.");
            }

            var variants = GenerateNumberVariants(meta.InvoiceNumber).ToList();
            var exact = Resolve(nipCandidates, d => Normalize(d.NumerDokumentu) == numberFront, "matchedExact", numberFront);
            if (exact.IsTerminal) return exact;

            var suffixFull = Resolve(nipCandidates, d => Normalize(d.NumerDokumentu).EndsWith(numberFront, StringComparison.OrdinalIgnoreCase), "matchedByFullSuffix", numberFront);
            if (suffixFull.IsTerminal) return suffixFull;

            foreach (string variant in variants)
            {
                var byVariant = Resolve(nipCandidates, d => Normalize(d.NumerDokumentu).EndsWith(variant, StringComparison.OrdinalIgnoreCase), "matchedByVariant", variant);
                if (byVariant.IsTerminal) return byVariant;
            }

            foreach (string variant in variants.Where(v => v.Length >= 8))
            {
                var containsVariant = Resolve(nipCandidates, d => Normalize(d.NumerDokumentu).Contains(variant, StringComparison.OrdinalIgnoreCase), "matchedByContainedVariant", variant);
                if (containsVariant.IsTerminal) return containsVariant;
            }

            InvoiceMatchResult lastAmbiguous = null;
            foreach (string fragment in GenerateProgressiveFragments(variants))
            {
                var byFragment = Resolve(nipCandidates, d => Normalize(d.NumerDokumentu).Contains(fragment, StringComparison.OrdinalIgnoreCase), "matchedByProgressiveFragment", fragment);
                if (byFragment.Status == "ambiguous")
                {
                    lastAmbiguous = byFragment;
                    continue;
                }

                if (byFragment.IsTerminal) return byFragment;
            }

            return lastAmbiguous ?? InvoiceMatchResult.NotFound($"Nie znaleziono jednoznacznego dokumentu dla numeru {meta.InvoiceNumber} i NIP {meta.VendorNip}.", nipCandidates);
        }

        public static InvoiceMetadataMatchResult MatchMetadataForDocument(IEnumerable<InvoiceMetadata> metadata, DokumentDoKsiegowania document)
        {
            var matches = (metadata ?? Enumerable.Empty<InvoiceMetadata>())
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
                    Reason = "Wiele wpisów metadanych pasuje do tego samego dokumentu."
                };
            }

            return new InvoiceMetadataMatchResult
            {
                Status = "notFound",
                Reason = "Brak metadanych pasujących do dokumentu."
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

        public static string Normalize(string input)
        {
            return input == null ? string.Empty : new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        public static string NormalizeNip(string input)
        {
            string normalized = Normalize(input);
            return normalized.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? normalized.Substring(2) : normalized;
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
                return InvoiceMatchResult.Ambiguous($"Znaleziono {matches.Count} kandydatów dla wariantu {variant}.", matches, variant);
            }

            return InvoiceMatchResult.Empty();
        }

        private static IEnumerable<string> GenerateProgressiveFragments(IEnumerable<string> variants)
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string variant in variants.Where(v => v.Length >= 6).OrderByDescending(v => v.Length))
            {
                for (int length = 6; length <= variant.Length; length++)
                {
                    int start = Math.Max(0, (variant.Length - length) / 2);
                    string fragment = variant.Substring(start, length);
                    if (emitted.Add(fragment))
                    {
                        yield return fragment;
                    }
                }
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
