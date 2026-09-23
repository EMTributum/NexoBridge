using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    /// <summary>
    /// Kontrakt JSON między procesem głównym NexoBridge a procesem potomnym uruchamianym jako
    /// "NexoBridge.exe --payroll-client-worker" (patrz Program.cs). Wysyłany na stdin procesu
    /// potomnego, który liczy pozycje kadrowo-płacowe DOKŁADNIE JEDNEGO klienta w izolowanym procesie
    /// systemowym - patrz uzasadnienie w PayrollCountsService.PoliczDlaKlientaWProcesie.
    /// </summary>
    public class PayrollClientWorkerRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public List<PayrollClientWorkerLineSpec> Lines { get; set; } = new List<PayrollClientWorkerLineSpec>();
    }

    public class PayrollClientWorkerLineSpec
    {
        public string Label { get; set; }
        public Guid CounterGuid { get; set; }
        public List<PayrollClientWorkerTierSpec> Tiers { get; set; } = new List<PayrollClientWorkerTierSpec>();
    }

    public class PayrollClientWorkerTierSpec
    {
        public int From { get; set; }
        public int To { get; set; }
        public decimal? UnitNet { get; set; }
        public decimal? UnitGross { get; set; }
        public decimal? CollectiveNet { get; set; }
        public decimal? CollectiveGross { get; set; }
    }

    /// <summary>Odpowiedź procesu potomnego, wypisana jako JEDNA linia JSON na stdout.</summary>
    public class PayrollClientWorkerResponse
    {
        public string Status { get; set; } // SUCCESS / FAILED
        public string Error { get; set; }
        public List<PayrollFeeLineDto> Lines { get; set; } = new List<PayrollFeeLineDto>();
    }
}
