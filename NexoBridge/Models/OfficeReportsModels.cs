using System.Collections.Generic;

namespace NexoBridge.Models
{
    // Modele synchronicznych odczytów z WŁASNEJ bazy biura (Subiekt nexo) - patrz OfficeReportsSqlService.
    // Kwoty jako decimal (JSON number, bez zaokrąglania), daty jako "yyyy-MM-dd".

    /// <summary>POST /api/office-reports/payroll-costs</summary>
    public class OfficePayrollCostsRequest
    {
        public string DatabaseName { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }
    }

    /// <summary>POST /api/office-reports/sales-invoices (rok/miesiąc WYSTAWIENIA faktur; przypisanie do
    /// miesiąca księgowego robi wywołujący).</summary>
    public class OfficeSalesInvoicesRequest
    {
        public string DatabaseName { get; set; }
        public int IssueYear { get; set; }
        public int IssueMonth { get; set; }
    }

    /// <summary>Własna firma bazy (ModelDanychContainer.Firmy z dyskryminatorem MojaFirma).
    /// CompanyId = Firmy.Id, EntityId = Firmy.Firma_Podmiot_Id (to samo co Dokumenty.MojaFirmaId).</summary>
    public class OfficeCompanyInfo
    {
        public int CompanyId { get; set; }
        public int EntityId { get; set; }
        public string Name { get; set; }
        public string Nip { get; set; }
    }

    /// <summary>Jeden wiersz ModelDanychContainer.Wynagrodzenia (wypłata z listy płac albo rachunek do umowy).</summary>
    public class OfficePayrollDocument
    {
        public int DocumentId { get; set; }
        public int ListId { get; set; }
        public int? ContractId { get; set; }
        public int? EmployeeId { get; set; }
        public int? PersonId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string ListMonth { get; set; }
        public string ListTitle { get; set; }
        public string ListNumber { get; set; }
        public string Number { get; set; }

        /// <summary>"payroll" (Wynagrodzenia_Wyplata), "bill" (Wynagrodzenia_RachunekDoUmowyPracowniczej) albo "unknown".</summary>
        public string Kind { get; set; }
        public bool Approved { get; set; }
        public bool NeedsCorrection { get; set; }
        public bool OutsidePayrollSystem { get; set; }
        public decimal? Gross { get; set; }

        /// <summary>Wynagrodzenia.ObciazeniePracodawcy - zawiera już składki pracodawcy.</summary>
        public decimal? EmployerCost { get; set; }
    }

    public class OfficePayrollCostsResponse
    {
        public string DatabaseName { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }
        public string PeriodFrom { get; set; }
        public string PeriodTo { get; set; }

        /// <summary>null, gdy w bazie nie ma dokładnie jednej własnej firmy (patrz CompanyCount).</summary>
        public OfficeCompanyInfo Company { get; set; }
        public int CompanyCount { get; set; }
        public int DocumentCount { get; set; }
        public List<OfficePayrollDocument> Documents { get; set; } = new List<OfficePayrollDocument>();
    }

    public class OfficeSalesInvoicePosition
    {
        public int PositionId { get; set; }
        public int? LineNumber { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public decimal? Quantity { get; set; }
        public string Unit { get; set; }
        public decimal? UnitNetPrice { get; set; }
        public decimal? Net { get; set; }
        public decimal? Vat { get; set; }
        public decimal? Gross { get; set; }
    }

    public class OfficeSalesInvoice
    {
        public int DocumentId { get; set; }
        public string Number { get; set; }
        public string IssueDate { get; set; }
        public string SaleDate { get; set; }
        public string Status { get; set; }
        public string BuyerName { get; set; }
        public string BuyerNip { get; set; }
        public string Currency { get; set; }
        public decimal? NetTotal { get; set; }
        public decimal? VatTotal { get; set; }
        public decimal? GrossTotal { get; set; }
        public List<OfficeSalesInvoicePosition> Positions { get; set; } = new List<OfficeSalesInvoicePosition>();
    }

    public class OfficeSalesInvoicesResponse
    {
        public string DatabaseName { get; set; }
        public int IssueYear { get; set; }
        public int IssueMonth { get; set; }
        public string IssueDateFrom { get; set; }
        public string IssueDateTo { get; set; }
        public OfficeCompanyInfo Issuer { get; set; }
        public int DocumentCount { get; set; }
        public int PositionCount { get; set; }
        public List<OfficeSalesInvoice> Documents { get; set; } = new List<OfficeSalesInvoice>();
    }

    /// <summary>POST /api/office-reports/client-emails</summary>
    public class OfficeClientEmailsRequest
    {
        public string DatabaseName { get; set; }
    }

    /// <summary>Jeden kontakt rodzaju "e-mail" (ModelDanychContainer.Kontakty) podmiotu będącego klientem biura.
    /// Value to surowa wartość z nexo - bywa kilka adresów rozdzielonych przecinkiem/średnikiem (dzieli wywołujący).</summary>
    public class OfficeClientEmail
    {
        public int ContactId { get; set; }
        public int ClientId { get; set; }
        public int EntityId { get; set; }
        public string Nip { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public bool Active { get; set; }
        public string Value { get; set; }
        public bool IsPrimary { get; set; }
        public string Comment { get; set; }
    }

    public class OfficeClientEmailsResponse
    {
        public string DatabaseName { get; set; }
        public int ContactCount { get; set; }
        public List<OfficeClientEmail> Contacts { get; set; } = new List<OfficeClientEmail>();
    }

    /// <summary>POST /api/office-reports/client-features</summary>
    public class OfficeClientFeaturesRequest
    {
        public string DatabaseName { get; set; }
    }

    /// <summary>Klient biura (KlienciBiura) z nazwami cech podmiotu (Cechy przez CechaPodmiot); pusta lista = brak cech.</summary>
    public class OfficeClientFeatures
    {
        public int ClientId { get; set; }
        public string Nip { get; set; }
        public string Name { get; set; }
        public bool Active { get; set; }
        public List<string> Features { get; set; } = new List<string>();
    }

    public class OfficeClientFeaturesResponse
    {
        public string DatabaseName { get; set; }
        public int ClientCount { get; set; }
        public List<OfficeClientFeatures> Clients { get; set; } = new List<OfficeClientFeatures>();
    }
}
