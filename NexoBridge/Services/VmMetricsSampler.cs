using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;

namespace NexoBridge.Services
{
    /// <summary>
    /// Próbkuje metryki systemowe CAŁEJ maszyny (nie tylko procesu NexoBridge), analogicznie do
    /// zakładki Wydajność w Menedżerze Zadań: CPU, RAM, zapis na dysk, upload/download sieci.
    /// Liczniki wydajności (PerformanceCounter) trzeba stworzyć raz i odpytywać cyklicznie -
    /// pierwszy odczyt zaraz po konstrukcji zwraca 0, więc ta klasa jest "rozgrzewana" w konstruktorze.
    /// </summary>
    public class VmMetricsSampler : IDisposable
    {
        private readonly ILogger<VmMetricsSampler> _logger;
        private readonly PerformanceCounter _cpuCounter;
        private readonly PerformanceCounter _availableRamCounter;
        private readonly PerformanceCounter _diskWriteCounter;
        private readonly List<PerformanceCounter> _networkSentCounters = new List<PerformanceCounter>();
        private readonly List<PerformanceCounter> _networkReceivedCounters = new List<PerformanceCounter>();
        private readonly double _totalRamMb;

        public VmMetricsSampler(ILogger<VmMetricsSampler> logger)
        {
            _logger = logger;

            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _availableRamCounter = new PerformanceCounter("Memory", "Available MBytes");
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            _totalRamMb = ReadTotalPhysicalMemoryMb();

            foreach (string instanceName in ReadNetworkInterfaceInstanceNames())
            {
                try
                {
                    _networkSentCounters.Add(new PerformanceCounter("Network Interface", "Bytes Sent/sec", instanceName));
                    _networkReceivedCounters.Add(new PerformanceCounter("Network Interface", "Bytes Received/sec", instanceName));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VM METRICS] Pomijam interfejs sieciowy '{Instance}' - nie udało się utworzyć licznika.", instanceName);
                }
            }

            // "Rozgrzanie" liczników - pierwszy NextValue() po utworzeniu zawsze zwraca 0.
            _cpuCounter.NextValue();
            _diskWriteCounter.NextValue();
            foreach (PerformanceCounter counter in _networkSentCounters.Concat(_networkReceivedCounters))
            {
                counter.NextValue();
            }
        }

        public VmMetricsSample TakeSample()
        {
            double availableRamMb = _availableRamCounter.NextValue();
            return new VmMetricsSample
            {
                Timestamp = DateTimeOffset.Now,
                CpuPercent = _cpuCounter.NextValue(),
                RamUsedMb = Math.Max(0, _totalRamMb - availableRamMb),
                RamTotalMb = _totalRamMb,
                DiskWriteBytesPerSec = _diskWriteCounter.NextValue(),
                NetworkUploadBytesPerSec = _networkSentCounters.Sum(c => SafeNextValue(c)),
                NetworkDownloadBytesPerSec = _networkReceivedCounters.Sum(c => SafeNextValue(c))
            };
        }

        private double SafeNextValue(PerformanceCounter counter)
        {
            try
            {
                return counter.NextValue();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VM METRICS] Nie udało się odczytać licznika sieciowego.");
                return 0;
            }
        }

        private double ReadTotalPhysicalMemoryMb()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                foreach (ManagementBaseObject result in searcher.Get())
                {
                    // TotalVisibleMemorySize jest w kilobajtach.
                    return Convert.ToDouble(result["TotalVisibleMemorySize"]) / 1024.0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VM METRICS] Nie udało się odczytać całkowitej pamięci RAM przez WMI.");
            }

            return 0;
        }

        private IEnumerable<string> ReadNetworkInterfaceInstanceNames()
        {
            try
            {
                var category = new PerformanceCounterCategory("Network Interface");
                return category.GetInstanceNames()
                    .Where(name => !name.Contains("isatap", StringComparison.OrdinalIgnoreCase)
                                && !name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                                && !name.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
                                && !name.Contains("Pseudo", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VM METRICS] Nie udało się wyliczyć interfejsów sieciowych.");
                return Enumerable.Empty<string>();
            }
        }

        public void Dispose()
        {
            _cpuCounter?.Dispose();
            _availableRamCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            foreach (PerformanceCounter counter in _networkSentCounters.Concat(_networkReceivedCounters))
            {
                counter.Dispose();
            }
        }
    }
}
