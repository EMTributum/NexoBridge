using System;
using System.Collections.Concurrent;
using System.Linq;

namespace NexoBridge.Services
{
    /// <summary>
    /// Raporty zakończonych zleceń trzymane w pamięci tylko przez określony czas - wcześniej store'y backfillu
    /// trzymały wszystko do restartu procesu (a raport enumeracji zawierał PDF-y w base64). Klasyfikator odbiera
    /// raport w ciągu sekund od zakończenia, więc 24 h to duży zapas. NexoBridge pozostaje bezstanowy.
    /// </summary>
    internal sealed class WygasajaceRaporty<TReport>
    {
        private readonly ConcurrentDictionary<string, (TReport Report, DateTime StoredAtUtc)> _reports = new();
        private readonly TimeSpan _czasZycia;

        public WygasajaceRaporty(TimeSpan czasZycia)
        {
            _czasZycia = czasZycia;
        }

        public void Store(string jobId, TReport report)
        {
            UsunPrzeterminowane();
            _reports[jobId] = (report, DateTime.UtcNow);
        }

        public bool TryGet(string jobId, out TReport report)
        {
            UsunPrzeterminowane();
            if (_reports.TryGetValue(jobId, out var wpis))
            {
                report = wpis.Report;
                return true;
            }

            report = default;
            return false;
        }

        private void UsunPrzeterminowane()
        {
            DateTime granica = DateTime.UtcNow - _czasZycia;
            foreach (string jobId in _reports.Where(kv => kv.Value.StoredAtUtc < granica).Select(kv => kv.Key).ToList())
            {
                _reports.TryRemove(jobId, out _);
            }
        }
    }
}
