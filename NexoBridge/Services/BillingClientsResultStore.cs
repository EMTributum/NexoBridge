using NexoBridge.Models;
using System.Collections.Concurrent;

namespace NexoBridge.Services
{
    public class BillingClientsResultStore
    {
        private readonly ConcurrentDictionary<string, bool> _pendingJobs = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, BillingClientsReport> _reports = new ConcurrentDictionary<string, BillingClientsReport>();

        public void MarkPending(string jobId)
        {
            _pendingJobs[jobId] = true;
        }

        public void Store(BillingClientsReport report)
        {
            _reports[report.JobId] = report;
            _pendingJobs.TryRemove(report.JobId, out _);
        }

        public bool TryGet(string jobId, out BillingClientsReport report)
        {
            return _reports.TryGetValue(jobId, out report);
        }

        public bool IsPending(string jobId)
        {
            return _pendingJobs.ContainsKey(jobId);
        }
    }
}
