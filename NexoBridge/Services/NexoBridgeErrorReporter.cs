using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class NexoBridgeErrorReporter
    {
        private const int DefaultLogWindowSeconds = 180;
        private readonly HttpClient _httpClient;
        private readonly NexoBridgeLogReader _logReader;
        private readonly ILogger<NexoBridgeErrorReporter> _logger;
        private readonly string _classifierUrl;
        private readonly string _alertToken;
        private readonly int _logWindowSeconds;

        public NexoBridgeErrorReporter(
            HttpClient httpClient,
            NexoBridgeLogReader logReader,
            ILogger<NexoBridgeErrorReporter> logger)
        {
            _httpClient = httpClient;
            _logReader = logReader;
            _logger = logger;
            _classifierUrl = Environment.GetEnvironmentVariable("CLASSIFIER_URL");
            _alertToken = Environment.GetEnvironmentVariable("NEXO_BRIDGE_ALERT_TOKEN");
            _logWindowSeconds = ReadLogWindowSeconds();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public async Task ReportJobFailureAsync(
            ImportJob job,
            string component,
            string activity,
            string operation,
            string message,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_classifierUrl))
            {
                _logger.LogDebug("[ERROR REPORT] Pomijam zgłoszenie błędu {JobId}, bo CLASSIFIER_URL nie jest ustawiony.", job?.JobId);
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string logFragment = _logReader.ReadWindow(now, _logWindowSeconds, activity);
            var report = new NexoBridgeErrorReport
            {
                JobId = job?.JobId,
                BridgeJobId = job?.JobId,
                Source = "NexoBridge",
                Component = component,
                Severity = "error",
                Activity = activity,
                Operation = operation,
                Message = message,
                ExceptionType = exception?.GetBaseException().GetType().FullName,
                StackTrace = exception?.ToString(),
                Timestamp = now,
                Log = logFragment,
                Context = BuildContext(job)
            };

            try
            {
                Uri endpoint = BuildEndpointUri();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(report, options: new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    })
                };

                if (!string.IsNullOrWhiteSpace(_alertToken))
                {
                    request.Headers.TryAddWithoutValidation("X-Nexo-Bridge-Alert-Token", _alertToken);
                }

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[ERROR REPORT] Zgłoszono błąd joba {JobId} do Klasyfikatora. Status={StatusCode}; odpowiedź={Response}",
                        job?.JobId,
                        (int)response.StatusCode,
                        string.IsNullOrWhiteSpace(responseBody) ? "brak" : responseBody);
                }
                else
                {
                    _logger.LogWarning("[ERROR REPORT] Klasyfikator odrzucił zgłoszenie błędu joba {JobId}. Status={StatusCode}; odpowiedź={Response}",
                        job?.JobId,
                        (int)response.StatusCode,
                        string.IsNullOrWhiteSpace(responseBody) ? "brak" : responseBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ERROR REPORT] Nie udało się wysłać zgłoszenia błędu joba {JobId} do Klasyfikatora.", job?.JobId);
            }
        }

        private Uri BuildEndpointUri()
        {
            string trimmed = _classifierUrl.TrimEnd('/');
            return new Uri($"{trimmed}/api/integrations/nexo-bridge/error-report", UriKind.Absolute);
        }

        private Dictionary<string, object> BuildContext(ImportJob job)
        {
            var context = new Dictionary<string, object>();
            if (job == null)
            {
                return context;
            }

            context["databaseName"] = job.DatabaseName;
            context["billingMonth"] = job.BillingMonth;
            context["billingYear"] = job.BillingYear;
            context["importInvoices"] = job.ImportInvoices;
            context["calculateVat"] = job.CalculateVat;
            context["calculatePit"] = job.CalculatePit;
            context["calculateAmortization"] = job.CalculateAmortization;
            context["eppFilesCount"] = job.Files?.Count ?? 0;
            context["attachmentsCount"] = job.Attachments?.Count ?? 0;
            context["invoicesMetadataCount"] = job.InvoicesMetadata?.Count ?? 0;
            context["ksefNumbersCount"] = job.InvoicesMetadata?.Count(m => !string.IsNullOrWhiteSpace(m.KsefNumber)) ?? 0;
            context["ksefCodesCount"] = job.InvoicesMetadata?.Count(m => !string.IsNullOrWhiteSpace(m.KsefCode)) ?? 0;

            var firstInvoice = job.InvoicesMetadata?.FirstOrDefault();
            if (firstInvoice != null)
            {
                context["invoiceNumber"] = firstInvoice.InvoiceNumber;
                context["vendorNip"] = firstInvoice.VendorNip;
            }

            return context;
        }

        private int ReadLogWindowSeconds()
        {
            string raw = Environment.GetEnvironmentVariable("NEXO_BRIDGE_ERROR_LOG_WINDOW_SECONDS");
            if (int.TryParse(raw, out int value) && value > 0)
            {
                return Math.Min(3600, value);
            }

            return DefaultLogWindowSeconds;
        }
    }
}
