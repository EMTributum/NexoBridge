using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    // ClientDatabaseRef (Nip, DatabaseName) reużywany z PayrollCountsModels.cs - identyczny kształt.

    public class RawPayrollCountsBatchJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }

        /// <summary>Klienci z ustawioną własną bazą Nexo (clients.nexo_db_name) - w odróżnieniu od
        /// PayrollCountsBatchJob NIE ma tu żadnej fazy filtrowania po cenniku: KAŻDY klient z tej listy
        /// dostaje próbę policzenia.</summary>
        public List<ClientDatabaseRef> ClientDatabases { get; set; } = new List<ClientDatabaseRef>();
    }

    public class RawPayrollCountsBatchReport
    {
        public string JobId { get; set; }
        public string Status { get; set; } // SUCCESS / PARTIAL_SUCCESS / FAILED
        public string Message { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
        public List<RawPayrollCountsItem> Items { get; set; } = new List<RawPayrollCountsItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class RawPayrollCountsItem
    {
        public string Nip { get; set; }

        /// <summary>SUCCESS / FAILED (błąd połączenia z bazą klienta, brak licencji, timeout itp.).</summary>
        public string Status { get; set; }
        public string Error { get; set; }
        public int RachunekCount { get; set; }
        public int WyplataCount { get; set; }
    }
}
