using System;
using System.Collections.Generic;
using System.Linq;
using NexoBridge.Services;

namespace NexoBridge.Models
{
    public class EppImportResult
    {
        public int ObjectsCount { get; set; }
        public int KsefAssignedCount { get; set; }
        public List<EppImportedHeader> Headers { get; set; } = new List<EppImportedHeader>();
    }

    public class EppImportedHeader
    {
        public string InvoiceNumber { get; set; }
        public string FullNumber { get; set; }
        public string VendorNip { get; set; }
        public string TechnicalNumber { get; set; }
        public DateTime? ReceivedDate { get; set; }
        public DateTime? IssueDate { get; set; }
        public bool KsefAssigned { get; set; }
        public string KsefNumber { get; set; }
        public string KsefCode { get; set; }

        public InvoiceMetadata ToInvoiceMetadata()
        {
            return new InvoiceMetadata
            {
                InvoiceNumber = !string.IsNullOrWhiteSpace(InvoiceNumber) ? InvoiceNumber : FullNumber,
                VendorNip = VendorNip,
                KsefNumber = KsefNumber,
                KsefCode = KsefCode
            };
        }
    }

    public class ImportPackageContext
    {
        public List<InvoiceMetadata> Metadata { get; set; } = new List<InvoiceMetadata>();
        public List<EppImportedHeader> EppHeaders { get; set; } = new List<EppImportedHeader>();

        public static ImportPackageContext FromJob(ImportJob job, EppImportResult importResult)
        {
            return new ImportPackageContext
            {
                Metadata = job?.InvoicesMetadata ?? new List<InvoiceMetadata>(),
                EppHeaders = importResult?.Headers ?? new List<EppImportedHeader>()
            };
        }

        public EppImportedHeader FindHeaderForMetadata(InvoiceMetadata metadata)
        {
            if (metadata == null) return null;

            var candidates = EppHeaders
                .Where(h => HeaderMatchesMetadata(h, metadata))
                .ToList();

            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static bool HeaderMatchesMetadata(EppImportedHeader header, InvoiceMetadata metadata)
        {
            if (header == null || metadata == null) return false;

            string metaNip = InvoiceDocumentMatcher.NormalizeNip(metadata.VendorNip);
            string headerNip = InvoiceDocumentMatcher.NormalizeNip(header.VendorNip);
            if (string.IsNullOrWhiteSpace(metaNip) || string.IsNullOrWhiteSpace(headerNip) ||
                !headerNip.EndsWith(metaNip, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string metaNumber = InvoiceDocumentMatcher.Normalize(metadata.InvoiceNumber);
            if (string.IsNullOrWhiteSpace(metaNumber)) return false;

            var headerNumbers = new[]
                {
                    header.InvoiceNumber,
                    header.FullNumber,
                    header.TechnicalNumber
                }
                .Select(InvoiceDocumentMatcher.Normalize)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return headerNumbers.Any(n => InvoiceDocumentMatcher.IsSafeNumberMatch(metaNumber, n));
        }
    }
}

