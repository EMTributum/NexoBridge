using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    // ClientDatabaseRef (Nip, DatabaseName) reużywany z PayrollCountsModels.cs - identyczny kształt.

    public class ZusOwnerContributionBatchJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }

        /// <summary>Klienci z ustawioną własną bazą Nexo (clients.nexo_db_name) - tak jak
        /// RawPayrollCountsBatchJob, KAŻDY klient z tej listy dostaje próbę odczytu; klient bez
        /// skonfigurowanego ZUS-u właścicielskiego (np. spółka) po prostu wróci z pustą listą wpisów.</summary>
        public List<ClientDatabaseRef> ClientDatabases { get; set; } = new List<ClientDatabaseRef>();
    }

    public class ZusOwnerContributionBatchReport
    {
        public string JobId { get; set; }
        public string Status { get; set; } // SUCCESS / PARTIAL_SUCCESS / FAILED
        public string Message { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
        public List<ZusOwnerContributionItem> Items { get; set; } = new List<ZusOwnerContributionItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class ZusOwnerContributionItem
    {
        public string Nip { get; set; }

        /// <summary>SUCCESS / FAILED (błąd połączenia z bazą klienta, brak licencji, timeout itp.).</summary>
        public string Status { get; set; }
        public string Error { get; set; }
        public List<ZusOwnerContributionEntry> Entries { get; set; } = new List<ZusOwnerContributionEntry>();
    }

    /// <summary>Jedno naliczenie ZUS właściciela (INaliczenieSkladekZus) dopasowane do żądanego
    /// okresu - zwykle jeden wpis na klienta (właściciel JDG), potencjalnie więcej przy osobie
    /// współpracującej. Odczytane WYŁĄCZNIE z już istniejących, policzonych w Nexo naliczeń
    /// (WszystkieWgUprawnien) - nigdy nie liczone/zapisywane na nowo przez ten kod.</summary>
    public class ZusOwnerContributionEntry
    {
        public string PersonName { get; set; }

        /// <summary>Suma wszystkich składek pomniejszona o składki finansowane z budżetu państwa -
        /// to jest kwota, jaką klient faktycznie ma zapłacić do ZUS.</summary>
        public decimal? DoZaplaty { get; set; }
        public decimal? SumaSkladek { get; set; }
        public decimal? UbezpieczenieSpoleczne { get; set; }
        public decimal? UbezpieczenieZdrowotne { get; set; }
    }
}
