using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class InvoiceCreationJob
    {
        public string JobId { get; set; }

        /// <summary>Klucz idempotencji dostarczony przez wywołującego (np. "{clientId}-{rrrr-MM}"). Powtórne zlecenie z tym samym kluczem jest odrzucane bez łączenia się ze Sferą.</summary>
        public string IdempotencyKey { get; set; }

        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public string Nip { get; set; }
        public int ServiceYear { get; set; }
        public int ServiceMonth { get; set; }

        /// <summary>
        /// "Card" albo "Transfer" - dostarczone jawnie przez wywołującego (który już to ustalił przy
        /// odczycie billing-snapshot), zamiast odczytywane samodzielnie z domyślnej formy płatności
        /// klienta w nexo. Gwarantuje spójność z tym, co widział księgowy, i eliminuje ryzyko rozjazdu,
        /// gdyby domyślna forma płatności klienta zmieniła się w nexo między dwoma wywołaniami.
        /// </summary>
        public string PaymentMethod { get; set; }

        public List<InvoiceLineRequest> Lines { get; set; } = new List<InvoiceLineRequest>();
    }

    public class InvoiceLineRequest
    {
        public string Description { get; set; }
        public decimal? NetAmount { get; set; }
        public decimal? GrossAmount { get; set; }

        /// <summary>Stawka VAT pozycji w procentach (0/5/8/23), ustalona przez wywołującego z jego netto/brutto.
        /// Gdy podana - pozycja dostaje tę stawkę ze słownika nexo i tylko cenę netto (brutto liczy Subiekt),
        /// żeby faktura zgadzała się z kwotą pobraną z karty. Brak = stare zachowanie (domyślna stawka Subiekta).</summary>
        public int? VatRate { get; set; }
    }

    public class InvoiceCreationReport
    {
        public string JobId { get; set; }

        /// <summary>SUCCESS | FAILED | DUPLICATE</summary>
        public string Status { get; set; }

        public string Message { get; set; }
        public string DatabaseName { get; set; }
        public string Nip { get; set; }
        public string InvoiceNumber { get; set; }
        public int? InvoiceId { get; set; }

        /// <summary>Czy dokument został już zapisany w nexo. Tylko przy false wolno zwolnić klucz idempotencji
        /// po błędzie - inaczej ponowienie mogłoby utworzyć drugą fakturę za ten sam okres.</summary>
        public bool InvoiceSaved { get; set; }

        /// <summary>Wartość brutto zapisanej faktury odczytana z nexo - wywołujący porównuje ją z kwotą, którą
        /// pobrał z karty / wyliczył, i przy różnicy oznacza fakturę do weryfikacji. Null = nie udało się odczytać.</summary>
        public decimal? InvoiceGross { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
