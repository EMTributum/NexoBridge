using NexoBridge.Models;
using System.Collections.Concurrent;

namespace NexoBridge.Services
{
    public class BackfillWriteCommentsResultStore
    {
        private readonly ConcurrentDictionary<string, bool> _pendingJobs = new();
        private readonly ConcurrentDictionary<string, (int Percent, string Message)> _progress = new();
        private readonly ConcurrentDictionary<string, BackfillWriteCommentsReport> _reports = new();

        public void MarkPending(string jobId)
        {
            _pendingJobs[jobId] = true;
        }

        public void UpdateProgress(string jobId, int percent, string message)
        {
            _progress[jobId] = (percent, message);
        }

        public void Store(BackfillWriteCommentsReport report)
        {
            _reports[report.JobId] = report;
            _pendingJobs.TryRemove(report.JobId, out _);
            _progress.TryRemove(report.JobId, out _);
        }

        public bool TryGet(string jobId, out BackfillWriteCommentsReport report)
        {
            return _reports.TryGetValue(jobId, out report);
        }

        public bool IsPending(string jobId)
        {
            return _pendingJobs.ContainsKey(jobId);
        }

        public bool TryGetProgress(string jobId, out int percent, out string message)
        {
            if (_progress.TryGetValue(jobId, out var value))
            {
                percent = value.Percent;
                message = value.Message;
                return true;
            }

            percent = 0;
            message = null;
            return false;
        }
    }
}
