using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class BillingSnapshotJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public string Nip { get; set; }
    }

    public class BillingSnapshotReport
    {
        public string JobId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string DatabaseName { get; set; }
        public string Nip { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
        public ClientBillingSnapshotItem Item { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class ClientBillingSnapshotItem
    {
        public int? ClientId { get; set; }
        public string Nip { get; set; }
        public string Name { get; set; }
        public bool? Active { get; set; }
        public bool? DoFakturowania { get; set; }
        public PaymentConfigurationDto Payment { get; set; }

        /// <summary>Nazwa i kwoty bazowego (księgowego) rozliczenia klienta - stawka stała albo dopasowana pozycja cennika biura.</summary>
        public string BaseFeeName { get; set; }
        public decimal? BaseFeeNet { get; set; }
        public decimal? BaseFeeGross { get; set; }

        /// <summary>Opcjonalna kwota usług kadrowych - null, jeśli klient ich nie ma.</summary>
        public string PayrollFeeName { get; set; }
        public decimal? PayrollFeeNet { get; set; }
        public decimal? PayrollFeeGross { get; set; }
    }

    public class PaymentConfigurationDto
    {
        /// <summary>"Card" albo "Transfer" - patrz PaymentMethodSource co do pewności tej klasyfikacji.</summary>
        public string PaymentMethod { get; set; }

        /// <summary>"Cecha" (jawna cecha "Płatność kartą"), "FormaPlatnosciKeyword" (dopasowanie po nazwie formy płatności) albo "Default" (brak wskazówek - domyślnie przelew).</summary>
        public string PaymentMethodSource { get; set; }

        public bool IsDeferred { get; set; }
        public int? TermDays { get; set; }
        public string Summary { get; set; }
    }

    public class BillingClientsJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
    }

    public class BillingClientsReport
    {
        public string JobId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string DatabaseName { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
        public List<BillingClientListItem> Items { get; set; } = new List<BillingClientListItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class BillingClientListItem
    {
        public string Nip { get; set; }
        public string Name { get; set; }
        public bool? Active { get; set; }
        public bool? DoFakturowania { get; set; }
    }
}
