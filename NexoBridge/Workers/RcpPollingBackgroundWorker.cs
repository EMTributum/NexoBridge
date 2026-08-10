using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using NexoBridge.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Workers
{
    public sealed class RcpPollingBackgroundWorker : BackgroundService
    {
        private readonly RcpImportJobQueue _jobQueue;
        private readonly RcpImportResultStore _resultStore;
        private readonly RcpImportStateStore _stateStore;
        private readonly RcpEmployeeMappingStore _mappingStore;
        private readonly RcpSourceClient _sourceClient;
        private readonly RcpRuntimeSettings _runtimeSettings;
        private readonly ILogger<RcpPollingBackgroundWorker> _logger;

        public RcpPollingBackgroundWorker(
            RcpImportJobQueue jobQueue,
            RcpImportResultStore resultStore,
            RcpImportStateStore stateStore,
            RcpEmployeeMappingStore mappingStore,
            RcpSourceClient sourceClient,
            RcpRuntimeSettings runtimeSettings,
            ILogger<RcpPollingBackgroundWorker> logger)
        {
            _jobQueue = jobQueue;
            _resultStore = resultStore;
            _stateStore = stateStore;
            _mappingStore = mappingStore;
            _sourceClient = sourceClient;
            _runtimeSettings = runtimeSettings;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[RCP POLLING WORKER] Harmonogram aktywny. Polling uruchamiam codziennie od 1 do 10 dnia miesiąca o 06:00 i 18:00 dla poprzedniego miesiąca.");

            while (!stoppingToken.IsCancellationRequested)
            {
                DateTimeOffset now = DateTimeOffset.Now;
                DateTimeOffset nextRun = GetNextPollTime(now);
                TimeSpan delay = nextRun - now;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                _logger.LogInformation("Następny polling RCP zaplanowany na {NextRunLocal}.", nextRun.LocalDateTime);
                await Task.Delay(delay, stoppingToken);
                await ExecutePollRunAsync(stoppingToken);
            }
        }

        private async Task ExecutePollRunAsync(CancellationToken cancellationToken)
        {
            string sourceUrl = _runtimeSettings.GetResolvedSourceUrl();
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                _logger.LogInformation("Pomijam polling RCP, ponieważ nie ustawiono zmiennej RCP_SOURCE_URL.");
                return;
            }

            string username = _runtimeSettings.GetResolvedUsername();
            string password = _runtimeSettings.GetResolvedPassword();
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning(
                    "Pomijam polling RCP, ponieważ brakuje poświadczeń nexo. Ustaw RCP_NEXO_USERNAME/RCP_NEXO_PASSWORD albo NEXO_USERNAME/NEXO_PASSWORD.");
                return;
            }

            RcpEmployeeMappingsDocument mappingDocument = await _mappingStore.GetAsync(cancellationToken);
            List<RcpEmployeeDatabaseMapping> databases = mappingDocument.Databases
                .Where(database => database != null && !string.IsNullOrWhiteSpace(database.DatabaseName))
                .ToList();

            if (databases.Count == 0)
            {
                _logger.LogInformation("Pomijam polling RCP, ponieważ mapa pracowników nie zawiera jeszcze żadnej bazy.");
                return;
            }

            DateTime today = DateTime.Today;
            DateTime targetMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
            int periodYear = targetMonth.Year;
            int periodMonth = targetMonth.Month;

            foreach (RcpEmployeeDatabaseMapping database in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    RcpSourceFetchResult fetchResult = await _sourceClient.FetchTimesheetAsync(
                        sourceUrl,
                        database.DatabaseName,
                        periodYear,
                        periodMonth,
                        cancellationToken);

                    if (!fetchResult.IsReady)
                    {
                        _logger.LogInformation(
                            "Źródło RCP dla bazy {DatabaseName} nie ma jeszcze danych za {PeriodYear}-{PeriodMonth:00}: {Message}",
                            database.DatabaseName,
                            periodYear,
                            periodMonth,
                            fetchResult.Message);
                        continue;
                    }

                    if (fetchResult.Payload.PeriodYear != periodYear || fetchResult.Payload.PeriodMonth != periodMonth)
                    {
                        _logger.LogWarning(
                            "Pomijam payload RCP dla bazy {DatabaseName}, bo zwrócony okres {ReturnedYear}-{ReturnedMonth:00} nie zgadza się z oczekiwanym {ExpectedYear}-{ExpectedMonth:00}.",
                            database.DatabaseName,
                            fetchResult.Payload.PeriodYear,
                            fetchResult.Payload.PeriodMonth,
                            periodYear,
                            periodMonth);
                        continue;
                    }

                    await _mappingStore.EnsureEmployeesFromTimesheetAsync(database.DatabaseName, fetchResult.Payload, cancellationToken);

                    bool alreadyImported = await _stateStore.HasSuccessfulImportAsync(
                        database.DatabaseName,
                        periodYear,
                        periodMonth,
                        fetchResult.PayloadHash,
                        cancellationToken);

                    if (alreadyImported)
                    {
                        _logger.LogInformation(
                            "Pomijam kolejkę RCP dla bazy {DatabaseName} i okresu {PeriodYear}-{PeriodMonth:00}, bo identyczny payload został już zaimportowany.",
                            database.DatabaseName,
                            periodYear,
                            periodMonth);
                        continue;
                    }

                    var job = new RcpImportJob
                    {
                        JobId = Guid.NewGuid().ToString("N").Substring(0, 8),
                        Username = username,
                        Password = password,
                        DatabaseName = database.DatabaseName,
                        PeriodYear = periodYear,
                        PeriodMonth = periodMonth,
                        SourceMode = "automatic-polling",
                        SourceUrl = fetchResult.EffectiveUrl,
                        PayloadHash = fetchResult.PayloadHash,
                        Payload = fetchResult.Payload
                    };

                    _resultStore.MarkPending(job.JobId);
                    await _jobQueue.QueueJobAsync(job);

                    _logger.LogInformation(
                        "Dodano do kolejki automatyczny import RCP {JobId} dla bazy {DatabaseName} i okresu {PeriodYear}-{PeriodMonth:00}.",
                        job.JobId,
                        job.DatabaseName,
                        job.PeriodYear,
                        job.PeriodMonth);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Błąd podczas pollingu RCP dla bazy {DatabaseName} za okres {PeriodYear}-{PeriodMonth:00}.",
                        database.DatabaseName,
                        periodYear,
                        periodMonth);
                }
            }
        }

        private static DateTimeOffset GetNextPollTime(DateTimeOffset now)
        {
            DateTime localNow = now.LocalDateTime;
            DateTime currentMonth = new DateTime(localNow.Year, localNow.Month, 1);

            for (int monthOffset = 0; monthOffset < 24; monthOffset++)
            {
                DateTime monthBase = currentMonth.AddMonths(monthOffset);
                for (int day = 1; day <= 10; day++)
                {
                    DateTime morning = new DateTime(monthBase.Year, monthBase.Month, day, 6, 0, 0, DateTimeKind.Local);
                    if (morning > localNow)
                    {
                        return new DateTimeOffset(morning);
                    }

                    DateTime evening = new DateTime(monthBase.Year, monthBase.Month, day, 18, 0, 0, DateTimeKind.Local);
                    if (evening > localNow)
                    {
                        return new DateTimeOffset(evening);
                    }
                }
            }

            return now.AddHours(12);
        }
    }
}
