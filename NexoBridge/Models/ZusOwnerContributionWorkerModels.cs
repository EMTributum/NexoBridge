using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    /// <summary>
    /// Kontrakt JSON między procesem głównym NexoBridge a procesem potomnym uruchamianym jako
    /// "NexoBridge.exe --zus-owner-client-worker" (patrz Program.cs). Wysyłany na stdin procesu
    /// potomnego, który odczytuje (BEZ przeliczania) naliczenia ZUS właściciela DOKŁADNIE JEDNEGO
    /// klienta w izolowanym procesie systemowym - ten sam powód izolacji co
    /// RawPayrollCountsService.PoliczDlaKlientaWProcesie (stan statyczny Sfery).
    /// </summary>
    public class ZusOwnerContributionWorkerRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
    }

    /// <summary>Odpowiedź procesu potomnego, wypisana jako JEDNA linia JSON na stdout.</summary>
    public class ZusOwnerContributionWorkerResponse
    {
        public string Status { get; set; } // SUCCESS / FAILED
        public string Error { get; set; }
        public List<ZusOwnerContributionEntry> Entries { get; set; } = new List<ZusOwnerContributionEntry>();
    }
}
