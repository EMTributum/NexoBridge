using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class RcpImportJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public string SourceMode { get; set; }
        public string SourceUrl { get; set; }
        public string PayloadHash { get; set; }
        public RcpTimesheetPayload Payload { get; set; } = new RcpTimesheetPayload();
    }

    public class RcpImportRequest
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public RcpTimesheetPayload Payload { get; set; } = new RcpTimesheetPayload();
    }

    public class RcpImportFromSourceRequest
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public string SourceUrl { get; set; }
        public bool Force { get; set; }
    }

    public class RcpImportReport
    {
        public string JobId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string DatabaseName { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public string SourceMode { get; set; }
        public string SourceUrl { get; set; }
        public string PayloadHash { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public int EmployeesReceivedCount { get; set; }
        public int EmployeesImportedCount { get; set; }
        public int EmployeesSkippedCount { get; set; }
        public int ShiftsReceivedCount { get; set; }
        public int ShiftsImportedCount { get; set; }
        public int ShiftsFailedCount { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<RcpEmployeeImportResult> Employees { get; set; } = new List<RcpEmployeeImportResult>();
    }

    public class RcpEmployeeImportResult
    {
        public string EmployeeId { get; set; }
        public string Pesel { get; set; }
        public string WorkerName { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public int ShiftsReceivedCount { get; set; }
        public int ShiftsImportedCount { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<RcpShiftImportResult> Shifts { get; set; } = new List<RcpShiftImportResult>();
    }

    public class RcpShiftImportResult
    {
        public string Date { get; set; }
        public string StartTime { get; set; }
        public string EndTime { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
    }

    public class RcpImportStateDocument
    {
        public List<RcpImportStateEntry> Imports { get; set; } = new List<RcpImportStateEntry>();
    }

    public class RcpImportStateEntry
    {
        public string DatabaseName { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public string PayloadHash { get; set; }
        public string JobId { get; set; }
        public string SourceMode { get; set; }
        public DateTimeOffset ImportedAtUtc { get; set; }
    }

    public class RcpSourceFetchResult
    {
        public bool IsReady { get; set; }
        public string Message { get; set; }
        public string EffectiveUrl { get; set; }
        public string PayloadHash { get; set; }
        public RcpTimesheetPayload Payload { get; set; }
    }
}
