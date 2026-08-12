using NexoBridge.Models;
using System.Collections.Concurrent;

namespace NexoBridge.Services
{
    public class BillingResultStore
    {
        private readonly ConcurrentDictionary<string, bool> _pendingJobs = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, BillingSnapshotReport> _reports = new ConcurrentDictionary<string, BillingSnapshotReport>();

        public void MarkPending(string jobId)
        {
            _pendingJobs[jobId] = true;
        }

        public void Store(BillingSnapshotReport report)
        {
            _reports[report.JobId] = report;
            _pendingJobs.TryRemove(report.JobId, out _);
        }

        public bool TryGet(string jobId, out BillingSnapshotReport report)
        {
            return _reports.TryGetValue(jobId, out report);
        }

        public bool IsPending(string jobId)
        {
            return _pendingJobs.ContainsKey(jobId);
        }
    }
}
