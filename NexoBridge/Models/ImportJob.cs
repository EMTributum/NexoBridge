using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NexoBridge.Models
{
    public class ImportJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }

        public int BillingMonth { get; set; }
        public int BillingYear { get; set; }

        // ==========================================
        // NOWE: Flagi sterujące procesem (Feature Flags)
        // ==========================================
        public bool ImportInvoices { get; set; } = false;
        public bool CalculateVat { get; set; } = false;
        [JsonPropertyName("calculateVatUE")]
        public bool CalculateVatUE { get; set; } = false;
        public bool CalculatePit { get; set; } = false;
        public bool CalculateAmortization { get; set; } = false;

        public List<EppFilePayload> Files { get; set; } = new List<EppFilePayload>();
        public List<AttachmentPayload> Attachments { get; set; } = new List<AttachmentPayload>();
        public List<InvoiceMetadata> InvoicesMetadata { get; set; } = new List<InvoiceMetadata>();
    }

    public class EppFilePayload
    {
        public string FileName { get; set; }
        public byte[] Content { get; set; }
    }

    public class AttachmentPayload
    {
        public string DocumentNumber { get; set; }
        public string VendorNip { get; set; }
        public string FileName { get; set; }
        public byte[] Content { get; set; }
    }

    public class InvoiceMetadata
    {
        public string InvoiceNumber { get; set; }
        public string VendorNip { get; set; }
        public string KsefNumber { get; set; }
        public string KsefCode { get; set; }
        public string PdfFileName { get; set; }
    }
}
