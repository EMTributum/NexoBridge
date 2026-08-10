using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NexoBridge.Services;
using System;
using System.Text;

namespace NexoBridge.API
{
    public static class LogEndpoints
    {
        public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/logs/nexo-bridge", (HttpRequest request, NexoBridgeLogReader logReader) =>
                ReadLogWindow(request, logReader));

            // Alias pod integracje, gdyby FE wolał spójny prefiks /api/integrations.
            app.MapGet("/api/integrations/nexo-bridge/log", (HttpRequest request, NexoBridgeLogReader logReader) =>
                ReadLogWindow(request, logReader));

            return app;
        }

        private static IResult ReadLogWindow(HttpRequest request, NexoBridgeLogReader logReader)
        {
            DateTimeOffset? before = null;
            string beforeRaw = request.Query["before"];
            if (!string.IsNullOrWhiteSpace(beforeRaw))
            {
                if (!DateTimeOffset.TryParse(beforeRaw, out DateTimeOffset parsedBefore))
                {
                    return Results.BadRequest("Nieprawidłowy parametr before. Oczekiwany format ISO-8601, np. 2026-08-04T10:15:30.000000+00:00.");
                }

                before = parsedBefore;
            }

            int? windowSeconds = null;
            string windowRaw = request.Query["windowSeconds"];
            if (!string.IsNullOrWhiteSpace(windowRaw) && int.TryParse(windowRaw, out int parsedWindow))
            {
                windowSeconds = parsedWindow;
            }

            string activity = request.Query["activity"];
            string log = logReader.ReadWindow(before, windowSeconds, activity);
            return Results.Text(log, "text/plain; charset=utf-8", Encoding.UTF8);
        }
    }
}
