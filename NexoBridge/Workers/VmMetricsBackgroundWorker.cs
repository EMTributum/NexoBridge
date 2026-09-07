using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using NexoBridge.Services;

namespace NexoBridge.Workers
{
    /// <summary>
    /// Tymczasowa instrumentacja: próbkuje metryki wydajności całej VM co 5 sekund i wysyła
    /// zbuforowaną paczkę (~12 próbek) co minutę do KlasyfikatorFaktur. Aktywna wyłącznie
    /// w oknie 1-10 września 2026 - poza oknem tylko tanio sprawdza datę co 30 minut.
    /// Do usunięcia po zakończeniu zbierania danych: ten plik, VmMetricsSampler.cs,
    /// VmMetricsReporter.cs, VmMetricsModels.cs i dwie linie rejestracji w Program.cs.
    /// </summary>
    public class VmMetricsBackgroundWorker : BackgroundService
    {
        private static readonly DateTime WindowStart = new DateTime(2026, 9, 1);
        private static readonly DateTime WindowEnd = new DateTime(2026, 9, 10);
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

        private readonly VmMetricsReporter _reporter;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<VmMetricsBackgroundWorker> _logger;

        public VmMetricsBackgroundWorker(VmMetricsReporter reporter, ILoggerFactory loggerFactory)
        {
            _reporter = reporter;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<VmMetricsBackgroundWorker>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[VM METRICS WORKER] Gotowy. Okno zbierania: {Start:yyyy-MM-dd} - {End:yyyy-MM-dd}.", WindowStart, WindowEnd);

            while (!stoppingToken.IsCancellationRequested)
            {
                DateTime today = DateTime.Now.Date;
                if (today < WindowStart || today > WindowEnd)
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                    continue;
                }

                await RunCollectionWindowAsync(stoppingToken);
            }
        }

        private async Task RunCollectionWindowAsync(CancellationToken stoppingToken)
        {
            using var sampler = new VmMetricsSampler(_loggerFactory.CreateLogger<VmMetricsSampler>());
            var buffer = new VmMetricsBatch();
            DateTimeOffset lastFlush = DateTimeOffset.Now;

            while (!stoppingToken.IsCancellationRequested && DateTime.Now.Date <= WindowEnd)
            {
                try
                {
                    buffer.Samples.Add(sampler.TakeSample());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[VM METRICS WORKER] Nie udało się pobrać próbki metryk.");
                }

                if (DateTimeOffset.Now - lastFlush >= FlushInterval && buffer.Samples.Count > 0)
                {
                    await _reporter.SendBatchAsync(buffer, stoppingToken);
                    buffer.Samples.Clear();
                    lastFlush = DateTimeOffset.Now;
                }

                await Task.Delay(SampleInterval, stoppingToken);
            }

            if (buffer.Samples.Count > 0)
            {
                await _reporter.SendBatchAsync(buffer, stoppingToken);
            }
        }
    }
}
