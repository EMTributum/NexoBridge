using System.Collections.Generic;

namespace NexoBridge.Models
{
    public sealed class RcpEmployeeMappingsDocument
    {
        public List<RcpEmployeeDatabaseMapping> Databases { get; set; } = new List<RcpEmployeeDatabaseMapping>();
    }

    public sealed class RcpEmployeeDatabaseMapping
    {
        public string DatabaseName { get; set; } = string.Empty;
        public List<RcpEmployeeMapItem> Employees { get; set; } = new List<RcpEmployeeMapItem>();
    }

    public sealed class RcpEmployeeMapItem
    {
        public string EmployeeId { get; set; } = string.Empty;
        public string Pesel { get; set; }
        public string WorkerName { get; set; }
    }

    public sealed class RcpEmployeeUpsertRequest
    {
        public List<RcpEmployeeMapItem> Employees { get; set; } = new List<RcpEmployeeMapItem>();
    }

    public sealed class RcpTimesheetEmployeeSyncResult
    {
        public string DatabaseName { get; set; } = string.Empty;
        public int PayloadEmployeesCount { get; set; }
        public int AddedEmployeesCount { get; set; }
        public List<RcpEmployeeMapItem> AddedEmployees { get; set; } = new List<RcpEmployeeMapItem>();
    }
}
