using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NexoBridge.Models
{
    // Główny raport wysyłany do Front-endu
    public class TaxSummaryReport
    {
        public string JobId { get; set; }

        /// <summary>
        /// Możliwe wartości: "SUCCESS", "PARTIAL_SUCCESS", "FAILED"
        /// </summary>
        public string Status { get; set; }

        public string Message { get; set; }

        public AmortizationReport Amortization { get; set; }
        public List<PitResult> PitTaxes { get; set; } = new List<PitResult>();
        public VatReport VatTax { get; set; }
        public List<DocumentProcessingReport> Documents { get; set; } = new List<DocumentProcessingReport>();
    }

    public class DocumentProcessingReport
    {
        public string Source { get; set; }
        public string InvoiceNumber { get; set; }
        public string VendorNip { get; set; }
        public string KsefNumber { get; set; }
        public string PdfFileName { get; set; }

        public string MatchStatus { get; set; } = "pending";
        public string WaitingRoomStatus { get; set; } = "notChecked";
        public string WaitingRoomId { get; set; }
        public int? WaitingRoomNr { get; set; }
        public string WaitingRoomNumber { get; set; }
        public string WaitingRoomNip { get; set; }

        public string KsefStatus { get; set; } = "notProvided";
        public string AttachmentStatus { get; set; } = "notProvided";
        public string DecreeStatus { get; set; } = "notChecked";
        public string DecreeSchema { get; set; }
        public List<DocumentResultEntry> ResultEntries { get; set; } = new List<DocumentResultEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class DocumentResultEntry
    {
        public string ResultType { get; set; }
        public int? DocumentId { get; set; }
        public string EntityType { get; set; }
        public string KsefNumber { get; set; }
    }

    // Raport z modułu Środków Trwałych
    public class AmortizationReport
    {
        public bool Processed { get; set; }
        public int DocumentsGenerated { get; set; }
        public decimal TotalCostAdded { get; set; }

        // Zignorowane błędy (np. brak środków do amortyzacji)
        public string Warning { get; set; }
    }

    // Raport dla pojedynczego wspólnika
    public class PitResult
    {
        public string PartnerName { get; set; }
        public string TaxType { get; set; }
        public decimal AmountDue { get; set; }
        public bool IsGenerated { get; set; }
        public string CriticalError { get; set; }
        public string Warning { get; set; }
    }

    // Raport z pliku JPK_V7
    public class VatReport
    {
        public bool IsVatPayer { get; set; }
        public decimal AmountToPay { get; set; }
        public decimal AmountToCarryOver { get; set; }

        // Błąd podczas generowania JPK
        public string ErrorMsg { get; set; }
    }
}

