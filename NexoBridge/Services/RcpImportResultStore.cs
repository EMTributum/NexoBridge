using NexoBridge.Models;
using System.Collections.Concurrent;

namespace NexoBridge.Services
{
    public sealed class RcpImportResultStore
    {
        private readonly ConcurrentDictionary<string, bool> _pendingJobs = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, RcpImportReport> _reports = new ConcurrentDictionary<string, RcpImportReport>();

        public void MarkPending(string jobId)
        {
            _pendingJobs[jobId] = true;
        }

        public void Store(RcpImportReport report)
        {
            _reports[report.JobId] = report;
            _pendingJobs.TryRemove(report.JobId, out _);
        }

        public bool TryGet(string jobId, out RcpImportReport report)
        {
            return _reports.TryGetValue(jobId, out report);
        }

        public bool IsPending(string jobId)
        {
            return _pendingJobs.ContainsKey(jobId);
        }
    }
}
