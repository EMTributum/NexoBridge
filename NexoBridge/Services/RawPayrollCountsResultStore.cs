using NexoBridge.Models;
using System.Collections.Concurrent;

namespace NexoBridge.Services
{
    public class RawPayrollCountsResultStore
    {
        private readonly ConcurrentDictionary<string, bool> _pendingJobs = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, RawPayrollCountsBatchReport> _reports = new ConcurrentDictionary<string, RawPayrollCountsBatchReport>();

        public void MarkPending(string jobId)
        {
            _pendingJobs[jobId] = true;
        }

        public void Store(RawPayrollCountsBatchReport report)
        {
            _reports[report.JobId] = report;
            _pendingJobs.TryRemove(report.JobId, out _);
        }

        public bool TryGet(string jobId, out RawPayrollCountsBatchReport report)
        {
            return _reports.TryGetValue(jobId, out report);
        }

        public bool IsPending(string jobId)
        {
            return _pendingJobs.ContainsKey(jobId);
        }
    }
}
