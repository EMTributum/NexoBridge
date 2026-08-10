using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public sealed class RcpSourceClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<RcpSourceClient> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public RcpSourceClient(HttpClient httpClient, ILogger<RcpSourceClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<RcpSourceFetchResult> FetchTimesheetAsync(
            string sourceUrl,
            string databaseName,
            int periodYear,
            int periodMonth,
            CancellationToken cancellationToken)
        {
            Uri requestUri = BuildUri(sourceUrl, databaseName, periodYear, periodMonth);

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return new RcpSourceFetchResult
                {
                    IsReady = false,
                    Message = "Zrodlo zwrocilo 204 No Content.",
                    EffectiveUrl = requestUri.ToString()
                };
            }

            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new RcpSourceFetchResult
                {
                    IsReady = false,
                    Message = "Zrodlo zwrocilo pusty response body.",
                    EffectiveUrl = requestUri.ToString()
                };
            }

            using JsonDocument json = JsonDocument.Parse(content);
            JsonElement root = json.RootElement;

            if (IndicatesNotReady(root, out string notReadyMessage))
            {
                return new RcpSourceFetchResult
                {
                    IsReady = false,
                    Message = notReadyMessage,
                    EffectiveUrl = requestUri.ToString()
                };
            }

            JsonElement payloadElement = root;
            if (TryGetPropertyIgnoreCase(root, "payload", out JsonElement payloadProperty) &&
                payloadProperty.ValueKind == JsonValueKind.Object)
            {
                payloadElement = payloadProperty;
            }

            RcpTimesheetPayload payload = payloadElement.Deserialize<RcpTimesheetPayload>(_jsonOptions)
                ?? throw new InvalidOperationException("Nie udalo sie zdeserializowac payloadu RCP.");

            payload.PeriodYear = payload.PeriodYear > 0 ? payload.PeriodYear : periodYear;
            payload.PeriodMonth = payload.PeriodMonth is >= 1 and <= 12 ? payload.PeriodMonth : periodMonth;
            payload.EmployeesTimesheets ??= new System.Collections.Generic.List<RcpEmployeeTimesheet>();

            string payloadHash = ComputePayloadHash(payload);

            _logger.LogInformation(
                "Pobrano payload RCP z {RequestUri} dla bazy {DatabaseName} i okresu {PeriodYear}-{PeriodMonth:00}. Pracownicy: {EmployeesCount}.",
                requestUri,
                databaseName,
                payload.PeriodYear,
                payload.PeriodMonth,
                payload.EmployeesTimesheets.Count);

            return new RcpSourceFetchResult
            {
                IsReady = true,
                Message = "Pobrano payload RCP.",
                EffectiveUrl = requestUri.ToString(),
                PayloadHash = payloadHash,
                Payload = payload
            };
        }

        public static string ComputePayloadHash(RcpTimesheetPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);

            var normalizedPayload = new RcpTimesheetPayload
            {
                PeriodYear = payload.PeriodYear,
                PeriodMonth = payload.PeriodMonth,
                EmployeesTimesheets = (payload.EmployeesTimesheets ?? new System.Collections.Generic.List<RcpEmployeeTimesheet>())
                    .Where(employee => employee != null && !string.IsNullOrWhiteSpace(employee.EmployeeId))
                    .OrderBy(employee => employee.EmployeeId, StringComparer.OrdinalIgnoreCase)
                    .Select(employee => new RcpEmployeeTimesheet
                    {
                        EmployeeId = employee.EmployeeId?.Trim(),
                        Shifts = (employee.Shifts ?? new System.Collections.Generic.List<RcpShiftPayload>())
                            .Where(shift => shift != null)
                            .OrderBy(shift => shift.Date, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(shift => shift.StartTime, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(shift => shift.EndTime, StringComparer.OrdinalIgnoreCase)
                            .Select(shift => new RcpShiftPayload
                            {
                                Date = shift.Date?.Trim(),
                                StartTime = shift.StartTime?.Trim(),
                                EndTime = shift.EndTime?.Trim()
                            })
                            .ToList()
                    })
                    .ToList()
            };

            string json = JsonSerializer.Serialize(normalizedPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hash);
        }

        private static Uri BuildUri(string sourceUrl, string databaseName, int periodYear, int periodMonth)
        {
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri baseUri))
            {
                throw new InvalidOperationException("Nieprawidlowy adres RCP_SOURCE_URL.");
            }

            string separator = string.IsNullOrEmpty(baseUri.Query) ? "?" : "&";
            string url = string.Create(
                CultureInfo.InvariantCulture,
                $"{baseUri}{separator}databaseName={Uri.EscapeDataString(databaseName)}&periodYear={periodYear}&periodMonth={periodMonth}");

            return new Uri(url);
        }

        private static bool IndicatesNotReady(JsonElement root, out string message)
        {
            if (TryGetPropertyIgnoreCase(root, "ready", out JsonElement readyElement) &&
                readyElement.ValueKind == JsonValueKind.False)
            {
                message = TryGetStringPropertyIgnoreCase(root, "message")
                    ?? "Zrodlo zwrocilo ready=false.";
                return true;
            }

            string status = TryGetStringPropertyIgnoreCase(root, "status");
            if (string.Equals(status, "not_ready", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                message = TryGetStringPropertyIgnoreCase(root, "message")
                    ?? $"Zrodlo zwrocilo status={status}.";
                return true;
            }

            message = null;
            return false;
        }

        private static string TryGetStringPropertyIgnoreCase(JsonElement root, string propertyName)
        {
            return TryGetPropertyIgnoreCase(root, propertyName, out JsonElement property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement property)
        {
            foreach (JsonProperty candidate in root.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }

            property = default;
            return false;
        }
    }
}
