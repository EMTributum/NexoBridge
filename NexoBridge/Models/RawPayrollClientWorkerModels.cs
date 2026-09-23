using System;

namespace NexoBridge.Models
{
    /// <summary>
    /// Kontrakt JSON między procesem głównym NexoBridge a procesem potomnym uruchamianym jako
    /// "NexoBridge.exe --raw-payroll-client-worker" (patrz Program.cs). Wysyłany na stdin procesu
    /// potomnego, który liczy SUROWE rachunki/wypłaty DOKŁADNIE JEDNEGO klienta w izolowanym procesie
    /// systemowym - patrz uzasadnienie w RawPayrollCountsService.PoliczDlaKlientaWProcesie (ten sam
    /// powód izolacji co PayrollCountsService.PoliczDlaKlientaWProcesie: stan statyczny Sfery).
    /// </summary>
    public class RawPayrollClientWorkerRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
    }

    /// <summary>Odpowiedź procesu potomnego, wypisana jako JEDNA linia JSON na stdout.</summary>
    public class RawPayrollClientWorkerResponse
    {
        public string Status { get; set; } // SUCCESS / FAILED
        public string Error { get; set; }
        public int RachunekCount { get; set; }
        public int WyplataCount { get; set; }
    }
}
