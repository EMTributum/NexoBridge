using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class OfficeVatFlagsJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string OfficeDatabaseName { get; set; }
        public string Nip { get; set; }
    }

    public class OfficeVatFlagsReport
    {
        public string JobId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string OfficeDatabaseName { get; set; }
        public string Nip { get; set; }
        public string NormalizedNip { get; set; }
        public string Source { get; set; } = "Biuro";
        public string Precision { get; set; } = "summary";
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
        public int TotalOfficeClients { get; set; }
        public int ClientsWithoutNip { get; set; }
        public OfficeVatFlagsItem Item { get; set; }
        public List<OfficeVatFlagsItem> Items { get; set; } = new List<OfficeVatFlagsItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class OfficeVatFlagsItem
    {
        public int? ClientId { get; set; }
        public string Nip { get; set; }
        public string NormalizedNip { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public bool? Active { get; set; }
        public bool? IsVatPayer { get; set; }
        public bool? IsVatUePayer { get; set; }
        public List<string> GroupNames { get; set; } = new List<string>();
        public string VatUeFlagName { get; set; }
        public string NipUe { get; set; }
        public bool? AlwaysUseNipUe { get; set; }
        public bool? SmeVatPayer { get; set; }
        public string Guardian { get; set; }
        public string AccountingProgram { get; set; }
        public bool? RachmistrzActive { get; set; }
        public bool? RewizorActive { get; set; }
        public bool? GratyfikantActive { get; set; }
        public int? AccountingFormCode { get; set; }
    }
}
