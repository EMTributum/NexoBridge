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
