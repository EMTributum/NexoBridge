using System;
using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class VmMetricsSample
    {
        public DateTimeOffset Timestamp { get; set; }
        public double CpuPercent { get; set; }
        public double RamUsedMb { get; set; }
        public double RamTotalMb { get; set; }
        public double DiskWriteBytesPerSec { get; set; }
        public double NetworkUploadBytesPerSec { get; set; }
        public double NetworkDownloadBytesPerSec { get; set; }
    }

    public class VmMetricsBatch
    {
        public List<VmMetricsSample> Samples { get; set; } = new List<VmMetricsSample>();
    }
}
