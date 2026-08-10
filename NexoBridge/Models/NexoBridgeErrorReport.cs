using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class NexoBridgeErrorReport
    {
        public string JobId { get; set; }
        public string BridgeJobId { get; set; }
        public string Source { get; set; } = "NexoBridge";
        public string Component { get; set; }
        public string Severity { get; set; } = "error";
        public string Activity { get; set; }
        public string Operation { get; set; }
        public string Message { get; set; }
        public string ExceptionType { get; set; }
        public string StackTrace { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string Log { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
    }
}
