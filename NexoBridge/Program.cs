using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using System;
using DotNetEnv;
using NexoBridge.API;
using NexoBridge.Services;
using NexoBridge.Workers;
using NexoBridge.Hubs;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace NexoBridge
{
    public class Program
    {
        private const long DefaultMaxRequestBodySizeMb = 30;
        private const string MaxRequestBodySizeEnvName = "NEXO_BRIDGE_MAX_REQUEST_BODY_MB";
        private const string LogLevelEnvName = "NEXO_LOG_LEVEL";

        public static void Main(string[] args)
        {
            Env.Load();

            // =====================================================================
            // 1. INICJALIZACJA SERILOGA NA SAMYM POCZĄTKU
            // =====================================================================
            LogEventLevel minimumLogLevel = ReadMinimumLogLevel();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Wycisza spam z ASP.NET
                .Enrich.FromLogContext()
                // Format dla konsoli (kolorowy, czytelny)
                .WriteTo.Console(
                    restrictedToMinimumLevel: minimumLogLevel,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                // Format dla pliku tekstowego (nowy plik codziennie)
                .WriteTo.File(
                    path: "Logs/nexobridge-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    restrictedToMinimumLevel: minimumLogLevel,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .WriteTo.Logger(logger => logger
                    .Filter.ByIncludingOnly(IsAttachmentServiceLog)
                    .WriteTo.File(
                        path: "Logs/nexobridge-attachments-debug-.log",
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        restrictedToMinimumLevel: LogEventLevel.Debug,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                    ))
                .CreateLogger();

            try
            {
                Log.Information("Uruchamianie mikroserwisu NexoBridge...");
                Log.Information("Poziom głównego logowania NexoBridge: {LogLevel}. Pełna diagnostyka załączników trafia do Logs/nexobridge-attachments-debug-.log.", minimumLogLevel);

                long maxRequestBodySizeMb = ReadMaxRequestBodySizeMb();
                long maxRequestBodySizeBytes = maxRequestBodySizeMb * 1024L * 1024L;

                var builder = WebApplication.CreateBuilder(args);

                // =====================================================================
                // 2. PODPIĘCIE SERILOGA DO HOSTA APLIKACJI
                // =====================================================================
                builder.Host.UseSerilog();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Limits.MaxRequestBodySize = maxRequestBodySizeBytes;
                });
                builder.Services.Configure<FormOptions>(options =>
                {
                    options.MultipartBodyLengthLimit = maxRequestBodySizeBytes;
                });

                // 3. Rejestrujemy aplikację jako oficjalną Usługę Windows
                builder.Services.AddWindowsService(options =>
                {
                    options.ServiceName = "NexoBridgeService";
                });

                // 4. Twardy CORS - wpuszczamy TYLKO Twoją aplikację z VM 12
                string allowedOrigin = Environment.GetEnvironmentVariable("ALLOWED_ORIGIN");
                builder.Services.AddCors(options =>
                {
                    options.AddPolicy("StrictPolicy", policy => {
                        policy.WithOrigins(allowedOrigin)
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials(); // Wymagane dla SignalR
                    });
                });

                Log.Information(
                    "Aktywny limit request body w NexoBridge: {MaxRequestBodySizeMb} MB ({MaxRequestBodySizeBytes} B).",
                    maxRequestBodySizeMb,
                    maxRequestBodySizeBytes
                );

                builder.Services.AddSignalR();
                builder.Services.AddSingleton<JobQueue>();
                builder.Services.AddSingleton<OfficeVatFlagsJobQueue>();
                builder.Services.AddSingleton<OfficeVatFlagsResultStore>();
                builder.Services.AddHostedService<NexoBackgroundWorker>();
                builder.Services.AddHostedService<OfficeVatFlagsBackgroundWorker>();

                var app = builder.Build();

                // Używamy nowej, rygorystycznej polityki
                app.UseCors("StrictPolicy");

                // Rejestrujemy trasę dla naszego Huba
                app.MapHub<ProgressHub>("/progressHub");

                app.MapHealthEndpoints();
                app.MapImportEndpoints();
                app.MapOfficeVatFlagsEndpoints();

                Log.Information("NexoBridge nasłuchuje na porcie 5000...");
                app.Run("http://0.0.0.0:5000");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Aplikacja zakończyła działanie z powodu krytycznego błędu");
            }
            finally
            {
                // Zapewnia zrzucenie ostatnich logów z pamięci do pliku przed zamknięciem
                Log.CloseAndFlush();
            }
        }

        private static long ReadMaxRequestBodySizeMb()
        {
            string rawValue = Environment.GetEnvironmentVariable(MaxRequestBodySizeEnvName);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return DefaultMaxRequestBodySizeMb;
            }

            if (long.TryParse(rawValue, out long parsedValue) && parsedValue > 0)
            {
                return parsedValue;
            }

            Log.Warning(
                "Nieprawidłowa wartość zmiennej {EnvName}='{EnvValue}'. Używam domyślnego limitu {DefaultMaxRequestBodySizeMb} MB.",
                MaxRequestBodySizeEnvName,
                rawValue,
                DefaultMaxRequestBodySizeMb
            );
            return DefaultMaxRequestBodySizeMb;
        }

        private static LogEventLevel ReadMinimumLogLevel()
        {
            string rawValue = Environment.GetEnvironmentVariable(LogLevelEnvName);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return LogEventLevel.Information;
            }

            return Enum.TryParse(rawValue.Trim(), ignoreCase: true, out LogEventLevel parsedLevel)
                ? parsedLevel
                : LogEventLevel.Information;
        }

        private static bool IsAttachmentServiceLog(LogEvent logEvent)
        {
            if (logEvent == null || !logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue sourceContext))
            {
                return false;
            }

            string value = sourceContext.ToString().Trim('"');
            return string.Equals(value, typeof(AttachmentService).FullName, StringComparison.Ordinal);
        }
    }
}
