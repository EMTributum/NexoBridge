using System.Collections.Generic;

namespace NexoBridge.Models
{
    public sealed class RcpTimesheetPayload
    {
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public List<RcpEmployeeTimesheet> EmployeesTimesheets { get; set; } = new List<RcpEmployeeTimesheet>();
    }

    public sealed class RcpEmployeeTimesheet
    {
        public string EmployeeId { get; set; } = string.Empty;
        public List<RcpShiftPayload> Shifts { get; set; } = new List<RcpShiftPayload>();
    }

    public sealed class RcpShiftPayload
    {
        public string Date { get; set; } = string.Empty;
        public string StartTime { get; set; } = string.Empty;
        public string EndTime { get; set; } = string.Empty;
    }
}
