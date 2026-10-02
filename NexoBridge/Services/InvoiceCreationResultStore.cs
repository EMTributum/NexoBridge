using NexoBridge.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NexoBridge.Services
{
    public class InvoiceCreationResultStore
    {
        private readonly ConcurrentDictionary<string, bool> _pendingJobs = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, InvoiceCreationReport> _reports = new ConcurrentDictionary<string, InvoiceCreationReport>();

        // Klucz idempotencji -> JobId pierwszego zlecenia, które go użyło. Chroni przed podwójnym
        // utworzeniem faktury przy retry (np. po timeoutcie wywołującego), bez konieczności otwierania
        // sesji Sfery żeby to sprawdzić.
        private readonly ConcurrentDictionary<string, string> _idempotencyKeys = new ConcurrentDictionary<string, string>();

        public void MarkPending(string jobId)
        {
            _pendingJobs[jobId] = true;
        }

        public bool TryReserveIdempotencyKey(string idempotencyKey, string jobId, out string existingJobId)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                existingJobId = null;
                return true;
            }

            existingJobId = _idempotencyKeys.GetOrAdd(idempotencyKey, jobId);
            return existingJobId == jobId;
        }

        /// <summary>
        /// Zwalnia klucz po nieudanym zleceniu, które NIE zapisało dokumentu - bez tego ponowienie (np. po
        /// poprawce albo przejściowym błędzie Sfery) dostawało w kółko ten sam zapamiętany błąd aż do restartu
        /// NexoBridge. Usuwa tylko wpis należący do tego zlecenia, nie cudzy.
        /// </summary>
        public void ReleaseIdempotencyKey(string idempotencyKey, string jobId)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return;
            }

            _idempotencyKeys.TryRemove(new KeyValuePair<string, string>(idempotencyKey, jobId));
        }

        public void Store(InvoiceCreationReport report)
        {
            _reports[report.JobId] = report;
            _pendingJobs.TryRemove(report.JobId, out _);
        }

        public bool TryGet(string jobId, out InvoiceCreationReport report)
        {
            return _reports.TryGetValue(jobId, out report);
        }

        public bool IsPending(string jobId)
        {
            return _pendingJobs.ContainsKey(jobId);
        }
    }
}
