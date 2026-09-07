using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;

namespace NexoBridge.Services
{
    /// <summary>
    /// Wysyła co minutę zbuforowaną paczkę próbek monitoringu VM do KlasyfikatorFaktur.
    /// Tymczasowa instrumentacja (zbieranie 1-10 września) - patrz VmMetricsBackgroundWorker.
    /// </summary>
    public class VmMetricsReporter
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<VmMetricsReporter> _logger;
        private readonly string _classifierUrl;
        private readonly string _alertToken;

        public VmMetricsReporter(HttpClient httpClient, ILogger<VmMetricsReporter> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _classifierUrl = Environment.GetEnvironmentVariable("CLASSIFIER_URL");
            // Ten sam token co NexoBridgeErrorReporter (NEXO_BRIDGE_ALERT_TOKEN) - NexoBridge już
            // nim mówi do Klasyfikatora, więc ten tymczasowy monitoring nie potrzebuje osobnego sekretu.
            _alertToken = Environment.GetEnvironmentVariable("NEXO_BRIDGE_ALERT_TOKEN");
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task SendBatchAsync(VmMetricsBatch batch, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_classifierUrl))
            {
                _logger.LogDebug("[VM METRICS] Pomijam wysyłkę paczki metryk, bo CLASSIFIER_URL nie jest ustawiony.");
                return;
            }

            if (batch.Samples.Count == 0)
            {
                return;
            }

            try
            {
                Uri endpoint = BuildEndpointUri();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(batch, options: new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    })
                };

                if (!string.IsNullOrWhiteSpace(_alertToken))
                {
                    request.Headers.TryAddWithoutValidation("X-Nexo-Bridge-Alert-Token", _alertToken);
                }

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[VM METRICS] Wysłano {Count} próbek do Klasyfikatora.", batch.Samples.Count);
                }
                else
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("[VM METRICS] Klasyfikator odrzucił paczkę metryk. Status={StatusCode}; odpowiedź={Response}",
                        (int)response.StatusCode,
                        string.IsNullOrWhiteSpace(responseBody) ? "brak" : responseBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VM METRICS] Nie udało się wysłać paczki metryk do Klasyfikatora.");
            }
        }

        private Uri BuildEndpointUri()
        {
            string trimmed = _classifierUrl.TrimEnd('/');
            return new Uri($"{trimmed}/api/integrations/vm-metrics", UriKind.Absolute);
        }
    }
}
