using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using System;
using System.IO;

namespace NexoBridge.Infrastructure
{
    /// <summary>
    /// Logi procesów potomnych backfillu. Wcześniej dostawały LoggerFactory bez żadnego providera, więc wszystko,
    /// co BackfillService/AttachmentService logowały w środku procesu, przepadało. Tylko plik - stdout to kanał
    /// wyniku (znaczniki ###BACKFILL_...), a stderr rodzic loguje jako ostrzeżenie.
    /// Wiele procesów potomnych pisze naraz, stąd shared: true.
    /// </summary>
    internal static class ChildProcessLogging
    {
        public static ILoggerFactory Utworz(string tryb, LogEventLevel minimumLevel)
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.WithProperty("Tryb", tryb)
                .Enrich.WithProperty("Pid", Environment.ProcessId)
                .WriteTo.File(
                    path: Path.Combine(logDirectory, "nexobridge-backfill-worker-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Tryb} pid={Pid}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            return new SerilogLoggerFactory(Log.Logger);
        }
    }
}
