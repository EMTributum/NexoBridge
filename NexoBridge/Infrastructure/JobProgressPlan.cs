using NexoBridge.Models;

namespace NexoBridge.Infrastructure
{
    public static class JobProgressPlan
    {
        public const int SferaStartupUnits = 30;
        public const int ManifestUnits = 2;
        public const int ImportInvoicesUnits = 20;
        public const int SkipImportUnits = 2;
        public const int AmortizationUnits = 12;
        public const int SkipAmortizationUnits = 2;
        public const int DecreeUnits = 22;
        public const int SkipDecreeUnits = 2;
        public const int KsefPostDecreeUnits = 4;
        public const int AttachmentsUnits = 8;
        public const int AttachmentsAndKsefUnits = KsefPostDecreeUnits + AttachmentsUnits;
        public const int PitUnits = 12;
        public const int SkipPitUnits = 2;
        public const int VatUnits = 14;
        public const int SkipVatUnits = 2;
        public const int VatUeUnits = 10;
        public const int SkipVatUeUnits = 2;
        public const int DuplicateAuditUnits = 5;
        public const int FinishUnits = 2;

        public static int CalculateTotalUnits(ImportJob job)
        {
            bool hasInvoiceImport = HasInvoiceImport(job);
            bool mayRequireDecree = hasInvoiceImport || job.CalculateAmortization;

            return SferaStartupUnits
                + ManifestUnits
                + (hasInvoiceImport ? ImportInvoicesUnits : SkipImportUnits)
                + (job.CalculateAmortization ? AmortizationUnits : SkipAmortizationUnits)
                + (mayRequireDecree ? DecreeUnits : SkipDecreeUnits)
                + (hasInvoiceImport ? AttachmentsAndKsefUnits : 0)
                + (job.CalculatePit ? PitUnits : SkipPitUnits)
                + (job.CalculateVat ? VatUnits : SkipVatUnits)
                + (job.CalculateVatUE ? VatUeUnits : SkipVatUeUnits)
                + DuplicateAuditUnits
                + FinishUnits;
        }

        public static bool HasInvoiceImport(ImportJob job)
        {
            return job.ImportInvoices && job.Files != null && job.Files.Count > 0;
        }
    }
}
