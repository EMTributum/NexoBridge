using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    /// <summary>Para NIP + własna baza Nexo/Gratyfikant klienta - Python już zna te pary (kolumna
    /// clients.nexo_db_name, uzupełniana ręcznie albo przez istniejącą synchronizację nazw baz), więc
    /// NexoBridge nie musi ich tu od nowa odszyfrowywać z KlientBiura.BazaDanych.</summary>
    public class ClientDatabaseRef
    {
        public string Nip { get; set; }
        public string DatabaseName { get; set; }
    }

    public class PayrollCountsBatchJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        /// <summary>Baza BIURA (nie klienta) - tu żyje konfiguracja cennika (KlientBiura.CennikUslug).</summary>
        public string OfficeDatabaseName { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }

        /// <summary>Klienci, dla których w ogóle warto próbować liczyć (mają znaną własną bazę Nexo) -
        /// klienci spoza tej listy dostaną w raporcie status SKIPPED_NO_DATABASE.</summary>
        public List<ClientDatabaseRef> ClientDatabases { get; set; } = new List<ClientDatabaseRef>();
    }

    public class PayrollCountsBatchReport
    {
        public string JobId { get; set; }
        public string Status { get; set; } // SUCCESS / PARTIAL_SUCCESS / FAILED
        public string Message { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
        public List<PayrollCountsItem> Items { get; set; } = new List<PayrollCountsItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class PayrollCountsItem
    {
        public string Nip { get; set; }
        public string Name { get; set; }

        /// <summary>SUCCESS / SKIPPED_NO_PRICING (cennik klienta nie jest kadrowo-płacowy, nic do
        /// policzenia) / SKIPPED_NO_DATABASE (brak znanej własnej bazy Nexo klienta) / FAILED.</summary>
        public string Status { get; set; }
        public string Error { get; set; }
        public List<PayrollFeeLineDto> Lines { get; set; } = new List<PayrollFeeLineDto>();
    }
}
