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

        /// <summary>Opcjonalny okres rozliczeniowy - jeśli podany, pozwala policzyć pozycje kadrowo-płacowe
        /// z licznika obiektów Nexo (patrz PayrollFeeLines). Bez niego liczniki nie są wywoływane.</summary>
        public int? PeriodYear { get; set; }
        public int? PeriodMonth { get; set; }
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

        /// <summary>Aktywność karty klienta biura (null = podmiot nie ma tej karty) - warunek panelu billing.</summary>
        public bool? KlientBiuraAktywny { get; set; }

        public PaymentConfigurationDto Payment { get; set; }

        /// <summary>Nazwa i kwoty bazowego (księgowego) rozliczenia klienta - stawka stała albo dopasowana pozycja cennika biura.</summary>
        public string BaseFeeName { get; set; }
        public decimal? BaseFeeNet { get; set; }
        public decimal? BaseFeeGross { get; set; }

        /// <summary>Płaski fallback z poprzedniej wersji (jedna sztywna stawka) - wypełniany tylko gdy
        /// PayrollFeeLines jest puste (cennik bez podpiętych liczników albo brak PeriodYear/PeriodMonth
        /// w zapytaniu). Docelowo do usunięcia po pełnym rolloucie liczników na wszystkich cennikach.</summary>
        public string PayrollFeeName { get; set; }
        public decimal? PayrollFeeNet { get; set; }
        public decimal? PayrollFeeGross { get; set; }

        /// <summary>Pozycje kadrowo-płacowe wyliczone z wbudowanego w Nexo licznika obiektów (ilość
        /// rachunków do umów pracowniczych / list płac w PeriodYear/PeriodMonth, pomnożona przez cenę
        /// jednostkową/przedziałową z cennika biura) - jedna pozycja na każdą pozycję cennika z podpiętym
        /// licznikiem. Pusta, jeśli cennik klienta nie jest kadrowo-płacowy, żadna pozycja nie ma
        /// podpiętego licznika, albo zapytanie nie podało okresu.</summary>
        public List<PayrollFeeLineDto> PayrollFeeLines { get; set; } = new List<PayrollFeeLineDto>();
    }

    public class PayrollFeeLineDto
    {
        public string Name { get; set; }
        public decimal? Net { get; set; }
        public decimal? Gross { get; set; }
    }

    public class PaymentConfigurationDto
    {
        /// <summary>"Card" albo "Transfer" - patrz PaymentMethodSource co do pewności tej klasyfikacji.</summary>
        public string PaymentMethod { get; set; }

        /// <summary>"Cecha" (jawna cecha "Płatność kartą"), "FormaPlatnosciKeyword" (dopasowanie po nazwie formy płatności) albo "Default" (brak wskazówek - domyślnie karta).</summary>
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

        /// <summary>Opcjonalny okres rozliczeniowy - patrz BillingSnapshotJob.PeriodYear/PeriodMonth.</summary>
        public int? PeriodYear { get; set; }
        public int? PeriodMonth { get; set; }
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

        /// <summary>Aktywność karty klienta biura (null = podmiot nie ma tej karty) - warunek panelu billing.</summary>
        public bool? KlientBiuraAktywny { get; set; }

        public PaymentConfigurationDto Payment { get; set; }

        /// <summary>Nazwa i kwoty bazowego (księgowego) rozliczenia klienta - jak w ClientBillingSnapshotItem.</summary>
        public string BaseFeeName { get; set; }
        public decimal? BaseFeeNet { get; set; }
        public decimal? BaseFeeGross { get; set; }

        /// <summary>Płaski fallback - patrz ClientBillingSnapshotItem.PayrollFeeName.</summary>
        public string PayrollFeeName { get; set; }
        public decimal? PayrollFeeNet { get; set; }
        public decimal? PayrollFeeGross { get; set; }

        /// <summary>Patrz ClientBillingSnapshotItem.PayrollFeeLines.</summary>
        public List<PayrollFeeLineDto> PayrollFeeLines { get; set; } = new List<PayrollFeeLineDto>();
    }
}
