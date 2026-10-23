using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    // Podgląd podatków (tax-preview): VAT / VAT-UE / PIT / amortyzacja / ZUS właściciela liczone
    // ŚWIEŻO i WYŁĄCZNIE DO ODCZYTU (bez Zapisz()) dla podanych klientów i okresów. Nazwa celowo inna
    // niż TaxSummaryReport (raport po eksporcie). Logika sprawdzona w NexoBridgeKonsola (--tax-preview).

    public class TaxPreviewBatchJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        /// <summary>Po tej chwili rodzic NIE uruchamia kolejnych klientów (zwraca SKIPPED_DEADLINE) -
        /// żeby nocna paczka nie wchodziła na inne nocne zadania. Null = bez limitu.</summary>
        public DateTimeOffset? NotAfterUtc { get; set; }

        public List<TaxPreviewClientRequest> Clients { get; set; } = new List<TaxPreviewClientRequest>();
    }

    public class TaxPreviewClientRequest
    {
        public string Nip { get; set; }
        public string DatabaseName { get; set; }
        public List<TaxPreviewPeriodRequest> Periods { get; set; } = new List<TaxPreviewPeriodRequest>();
    }

    public class TaxPreviewPeriodRequest
    {
        public int Year { get; set; }
        public int Month { get; set; }

        /// <summary>Czy doliczyć podgląd niezaksięgowanych rat amortyzacji (NaliczAmortyzacje bez
        /// Zapisz). Działa tylko, gdy w .env włączono TAX_PREVIEW_PENDING_AMORTIZATION=true.</summary>
        public bool IncludePendingAmortization { get; set; }
    }

    public class TaxPreviewBatchReport
    {
        public string JobId { get; set; }
        public string Status { get; set; } // SUCCESS / PARTIAL_SUCCESS / FAILED
        public string Message { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
        public List<TaxPreviewClientItem> Items { get; set; } = new List<TaxPreviewClientItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class TaxPreviewClientItem
    {
        public string Nip { get; set; }

        /// <summary>SUCCESS / PARTIAL_SUCCESS / FAILED / SKIPPED_DEADLINE.</summary>
        public string Status { get; set; }
        public string Error { get; set; }
        public List<TaxPreviewPeriodResult> Periods { get; set; } = new List<TaxPreviewPeriodResult>();
    }

    public class TaxPreviewPeriodResult
    {
        public int Year { get; set; }
        public int Month { get; set; }

        /// <summary>SUCCESS / PARTIAL_SUCCESS / FAILED / SKIPPED_DEADLINE.</summary>
        public string Status { get; set; }
        public string Error { get; set; }
        public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.Now;
        public TaxPreviewVat Vat { get; set; }
        public TaxPreviewVatUe VatUe { get; set; }
        public List<TaxPreviewPit> Pit { get; set; } = new List<TaxPreviewPit>();
        public TaxPreviewAmortization Amortization { get; set; }
        public TaxPreviewZus Zus { get; set; }
    }

    public class TaxPreviewVat
    {
        public string Status { get; set; } = "SUCCESS"; // SUCCESS / FAILED
        public string Error { get; set; }
        public string Warning { get; set; }

        /// <summary>monthly / quarterly / exempt / none (brak konfiguracji VAT krajowego).</summary>
        public string Settlement { get; set; }

        /// <summary>False dla rozliczenia kwartalnego w miesiącu, który nie kończy kwartału.</summary>
        public bool? DueInPeriod { get; set; }
        public bool? CashMethod { get; set; }
        public decimal? AmountToPay { get; set; }
        public decimal? AmountToCarryOver { get; set; }

        /// <summary>Zwrot na rachunek (P_54) z ZAPISANEJ deklaracji JPK za okres - Wylicz bez zapisu nie zna
        /// podziału nadwyżki (całą wpisuje do przeniesienia), decyduje o nim księgowa. null - brak zapisanego JPK.</summary>
        public decimal? AmountToRefund { get; set; }
        public string TemplateName { get; set; }
    }

    public class TaxPreviewVatUe
    {
        public string Status { get; set; } = "SUCCESS";
        public string Error { get; set; }
        public bool IsVatUePayer { get; set; }

        /// <summary>monthly / quarterly / "kod N".</summary>
        public string SettlementMethod { get; set; }
        public bool? DueInPeriod { get; set; }
        public bool? DeclarationExists { get; set; }
    }

    public class TaxPreviewPit
    {
        public string Status { get; set; } = "SUCCESS"; // SUCCESS / FAILED / SKIPPED
        public string Error { get; set; }
        public string Warning { get; set; }

        /// <summary>Ta sama nazwa co PitResult.PartnerName z eksportu (PobierzNazwe) - klucz scalania.</summary>
        public string PartnerName { get; set; }

        /// <summary>liniowy / skala / ryczałt.</summary>
        public string Form { get; set; }

        /// <summary>monthly / quarterly.</summary>
        public string Method { get; set; }
        public bool? DueInPeriod { get; set; }
        public decimal? AmountDue { get; set; }
        public string TemplateName { get; set; }
    }

    public class TaxPreviewAmortization
    {
        public string Status { get; set; } = "SUCCESS";
        public string Error { get; set; }
        public string Warning { get; set; }

        /// <summary>Odpisy (typ "Podatkowy") z datą w miesiącu, których dokument jest zaksięgowany.</summary>
        public decimal Booked { get; set; }

        /// <summary>Odpisy zapisane w Nexo, ale jeszcze niezaksięgowane (nie są w KPiR/PIT).</summary>
        public decimal SavedNotBooked { get; set; }

        /// <summary>Raty policzone na podgląd (NaliczAmortyzacje bez Zapisz) dla środków bez odpisu w miesiącu.</summary>
        public decimal Pending { get; set; }
        public decimal Total { get; set; }
        public bool PendingComputed { get; set; }
        public int OperationsInMonth { get; set; }
    }

    public class TaxPreviewZus
    {
        public string Status { get; set; } = "SUCCESS";
        public string Error { get; set; }
        public List<ZusOwnerContributionEntry> Entries { get; set; } = new List<ZusOwnerContributionEntry>();
    }

    /// <summary>Kontrakt JSON rodzic -> proces potomny "NexoBridge.exe --tax-preview-client-worker"
    /// (stdin). Jeden klient, wiele okresów w JEDNEJ sesji Sfery.</summary>
    public class TaxPreviewWorkerRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public List<TaxPreviewPeriodRequest> Periods { get; set; } = new List<TaxPreviewPeriodRequest>();

        /// <summary>Miękki limit: proces potomny nie zaczyna nowego okresu po tej chwili i zwraca wynik
        /// częściowy - żeby nie zostać zabitym twardym timeoutem (TryKill nie zwalnia licencji Sfery).</summary>
        public DateTimeOffset? SoftDeadlineUtc { get; set; }
    }

    /// <summary>Odpowiedź procesu potomnego (stdout, między znacznikami).</summary>
    public class TaxPreviewWorkerResponse
    {
        public string Status { get; set; } // SUCCESS / PARTIAL_SUCCESS / FAILED
        public string Error { get; set; }
        public List<TaxPreviewPeriodResult> Periods { get; set; } = new List<TaxPreviewPeriodResult>();
    }
}
