using NexoBridge.Models;
using System.Collections.Concurrent;

namespace NexoBridge.Services
{
    public class PayrollCountsResultStore
    {
        private readonly ConcurrentDictionary<string, bool> _pendingJobs = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, PayrollCountsBatchReport> _reports = new ConcurrentDictionary<string, PayrollCountsBatchReport>();

        public void MarkPending(string jobId)
        {
            _pendingJobs[jobId] = true;
        }

        public void Store(PayrollCountsBatchReport report)
        {
            _reports[report.JobId] = report;
            _pendingJobs.TryRemove(report.JobId, out _);
        }

        public bool TryGet(string jobId, out PayrollCountsBatchReport report)
        {
            return _reports.TryGetValue(jobId, out report);
        }

        public bool IsPending(string jobId)
        {
            return _pendingJobs.ContainsKey(jobId);
        }
    }
}
