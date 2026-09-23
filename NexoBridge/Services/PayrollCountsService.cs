using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InsERT.Moria.Klienci;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using static NexoBridge.Services.SferaReflectionHelpers;
using PodmiotyDane = InsERT.Moria.Klienci.IPodmiotyDane;
using PodmiotyManager = InsERT.Mox.ObiektyBiznesowe.IObiektyBiznesowe<InsERT.Moria.Klienci.IPodmiot, InsERT.Moria.ModelDanych.Podmiot, InsERT.Moria.Klienci.IPodmiotyDane>;

namespace NexoBridge.Services
{
    /// <summary>
    /// Liczy realne pozycje kadrowo-płacowe (rachunki do umów pracowniczych, wypłaty wg listy płac)
    /// per klient biura - dwuetapowo, bo konfiguracja cennika i realne dane liczbowe żyją w dwóch
    /// różnych bazach (patrz PayrollLineSpecExtractor - pełne uzasadnienie i historia błędu):
    ///   Faza 1 (ExtractEligibleSpecs): w JUŻ otwartej sesji biura odczytuje konfigurację cennika
    ///           każdego klienta (tanie, bez dodatkowego połączenia) - wołający ZAMYKA tę sesję biura
    ///           zaraz po tej fazie, PRZED rozpoczęciem Fazy 2 (patrz PayrollCountsBackgroundWorker) -
    ///           minimalizuje to okno, w którym sesja biura w ogóle istnieje, i zwalnia jej licencję
    ///           najszybciej jak się da.
    ///   Faza 2 (ComputeAllAsync): dla każdego klienta z co najmniej jedną pozycją kadrowo-płacową I
    ///           znaną własną bazą Nexo, liczy pozycje w KOMPLETNIE OSOBNYM PROCESIE SYSTEMOWYM (patrz
    ///           PoliczDlaKlientaWProcesie) - NIE w tym samym procesie NexoBridge.
    /// Błąd jednego klienta (zła/niedostępna baza, wygasła licencja, brak uprawnień operatora) nigdy
    /// nie przerywa całego batcha - trafia do PayrollCountsItem.Status=FAILED z komunikatem, reszta
    /// klientów jest liczona dalej.
    ///
    /// DLACZEGO OSOBNY PROCES, A NIE SAMA BLOKADA (SferaSessionGate)? Nawet przy pełnej serializacji
    /// (jedna sesja Sfery aktywna naraz w całym procesie NexoBridge) batch na 225 klientach nadal
    /// sypał się kaskadowo: część klientów padała na LicenceController przy Uruchom(), a od pewnego
    /// momentu WSZYSCY kolejni klienci (niezależnie której firmy dotyczyli) padali na
    /// "Brak zalogowanego operatora" przy Zlicz(). To dowód na stan DZIELONY MIĘDZY SESJAMI W RAMACH
    /// JEDNEGO PROCESU .NET, nie na kolizję z innym jobem. Dowód namacalny: SferaEngine (patrz
    /// static ctor) tworzy JEDEN, globalny dla całego procesu obiekt `System.Windows.Application`, a
    /// `WindowManager` (wołany przy KAŻDYM Uruchom()) dopisuje do jego WSPÓLNEGO
    /// `Application.Current.Resources` klucz "TouchScreenMode" - drugie i każde kolejne Uruchom() w
    /// tym samym procesie próbuje dopisać już istniejący klucz, stąd
    /// "Item has already been added. Key in dictionary: 'TouchScreenMode'". Sfera została
    /// zaprojektowana pod JEDNORAZOWE zalogowanie na cały czas życia procesu (jak aplikacja
    /// desktopowa), nie pod wielokrotne logowanie/wylogowanie w pętli w ramach jednego, długo
    /// działającego serwisu. Jedyny solidny fix: każdy klient dostaje WŁASNY, świeży proces systemowy
    /// (NexoBridge.exe uruchomiony ponownie z flagą --payroll-client-worker - patrz Program.cs), który
    /// zaczyna z czystym stanem statycznym i kończy działanie zaraz po policzeniu tego jednego klienta.
    /// </summary>
    public class PayrollCountsService
    {
        private readonly Uchwyt _officeSfera;
        private readonly ILogger _logger;

        public PayrollCountsService(Uchwyt officeSfera, ILogger logger)
        {
            _officeSfera = officeSfera;
            _logger = logger;
        }

        /// <summary>Faza 1 - woła się w JUŻ otwartej i zagatowanej sesji biura. Zwraca tylko klientów
        /// z co najmniej jedną pozycją kadrowo-płacową (reszta nie ma nic do policzenia w Fazie 2).</summary>
        internal List<ClientPayrollSpec> ExtractEligibleSpecs()
        {
            PodmiotyManager podmiotyManager = GetPodmiotyManager(_officeSfera, DateTime.Today);
            PodmiotyDane podmiotyDane = GetManagerDataOrContainer<PodmiotyDane>(_officeSfera, podmiotyManager, "IPodmioty.Dane");
            List<Podmiot> allClients = LoadClients(podmiotyDane);
            List<Podmiot> eligibleClients = FindEligibleClients(allClients);

            // Ekstrakcja per klient w try/catch - jeden klient z nietypową/zepsutą konfiguracją
            // cennika (rzut wyjątku gdzieś w reflekcji ExtractPayrollSpec) nie może wywalić całej
            // reszty batcha jednym niezłapanym wyjątkiem.
            var specs = new List<ClientPayrollSpec>();
            foreach (Podmiot client in eligibleClients)
            {
                try
                {
                    ClientPayrollSpec spec = PayrollLineSpecExtractor.ExtractPayrollSpec(client);
                    if (!string.IsNullOrWhiteSpace(spec.Nip) && spec.Lines.Count > 0)
                    {
                        specs.Add(spec);
                    }
                }
                catch (Exception ex)
                {
                    string clientLabel = BillingConfigurationService.GetDisplayName(client);
                    _logger.LogWarning(ex, "Błąd odczytu konfiguracji cennika kadrowo-płacowego klienta {Client} - pomijam, reszta batcha liczona dalej.",
                        clientLabel);
                }
            }

            return specs;
        }

        /// <summary>Faza 2 - sekwencyjnie, jeden klient na raz, każdy w OSOBNYM procesie systemowym
        /// (patrz PoliczDlaKlientaWProcesie) - dzięki temu jeden zepsuty/zawieszony proces klienta
        /// nigdy nie wpływa na pozostałych.</summary>
        internal async Task<PayrollCountsBatchReport> ComputeAllAsync(
            List<ClientPayrollSpec> specs, PayrollCountsBatchJob job, CancellationToken cancellationToken, Func<int, string, Task> raportujPostep)
        {
            var report = new PayrollCountsBatchReport { JobId = job.JobId, Status = "SUCCESS" };

            Dictionary<string, string> databaseByNip = job.ClientDatabases
                .Where(c => !string.IsNullOrWhiteSpace(c.Nip) && !string.IsNullOrWhiteSpace(c.DatabaseName))
                .GroupBy(c => c.Nip.Trim())
                .ToDictionary(g => g.Key, g => g.First().DatabaseName.Trim());

            (DateTime periodStart, DateTime periodEnd) = PayrollLineSpecExtractor.ResolvePeriodRange(job.PeriodYear, job.PeriodMonth);

            int total = specs.Count;
            int processed = 0;
            int failedCount = 0;

            foreach (ClientPayrollSpec spec in specs)
            {
                if (!databaseByNip.TryGetValue(spec.Nip, out string clientDatabaseName))
                {
                    report.Items.Add(new PayrollCountsItem
                    {
                        Nip = spec.Nip,
                        Name = spec.Name,
                        Status = "SKIPPED_NO_DATABASE",
                        Error = "Brak znanej własnej bazy Nexo tego klienta (clients.nexo_db_name)."
                    });
                    processed++;
                    continue;
                }

                await raportujPostep(
                    5 + (int)(90.0 * processed / Math.Max(1, total)),
                    $"{processed + 1}/{total}: {spec.Name}...");

                PayrollCountsItem item = await PoliczDlaKlientaWProcesie(spec, clientDatabaseName, job, periodStart, periodEnd, cancellationToken);

                report.Items.Add(item);
                if (item.Status == "FAILED")
                {
                    failedCount++;
                }

                processed++;
            }

            report.Status = failedCount == 0 ? "SUCCESS" : (failedCount == report.Items.Count ? "FAILED" : "PARTIAL_SUCCESS");
            report.Message = $"Policzono {report.Items.Count} klientów kadrowo-płacowych ({failedCount} błędów).";
            await raportujPostep(100, report.Message);
            return report;
        }

        /// <summary>Liczy pozycje kadrowo-płacowe JEDNEGO klienta w KOMPLETNIE OSOBNYM procesie
        /// systemowym - odpala ponownie ten sam plik wykonywalny NexoBridge z flagą
        /// "--payroll-client-worker" (patrz Program.cs.RunPayrollClientWorker), przekazuje dane
        /// wejściowe jako JSON na stdin i czyta wynik jako JSON ze stdout. Hasło idzie przez stdin, NIE
        /// przez argumenty procesu - argumenty są widoczne w liście procesów systemowych, stdin nie.
        /// Świeży proces = świeży, nieskażony stan statyczny Sfery - to jest CAŁY sens tego podejścia
        /// (patrz uzasadnienie w komentarzu klasy).</summary>
        private async Task<PayrollCountsItem> PoliczDlaKlientaWProcesie(
            ClientPayrollSpec spec, string clientDatabaseName, PayrollCountsBatchJob job,
            DateTime periodStart, DateTime periodEnd, CancellationToken cancellationToken)
        {
            var item = new PayrollCountsItem { Nip = spec.Nip, Name = spec.Name };

            var request = new PayrollClientWorkerRequest
            {
                Username = job.Username,
                Password = job.Password,
                DatabaseName = clientDatabaseName,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                Lines = spec.Lines.Select(l => new PayrollClientWorkerLineSpec
                {
                    Label = l.Label,
                    CounterGuid = l.CounterGuid,
                    Tiers = l.Tiers.Select(t => new PayrollClientWorkerTierSpec
                    {
                        From = t.From,
                        To = t.To,
                        UnitNet = t.UnitNet,
                        UnitGross = t.UnitGross,
                        CollectiveNet = t.CollectiveNet,
                        CollectiveGross = t.CollectiveGross
                    }).ToList()
                }).ToList()
            };

            try
            {
                (string fileName, string argsPrefix) = ResolveSelfLaunchCommand();
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = argsPrefix + "--payroll-client-worker",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();

                    string requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    await process.StandardInput.WriteAsync(requestJson);
                    process.StandardInput.Close();

                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                    // Twardy limit na JEDNEGO klienta - proces potomny nie może wisieć bez końca
                    // (np. zawieszony na oczekiwaniu na licencję); po przekroczeniu zabijamy go i
                    // zgłaszamy FAILED, reszta batcha idzie dalej.
                    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
                        try
                        {
                            await process.WaitForExitAsync(timeoutCts.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            TryKill(process);
                            item.Status = "FAILED";
                            item.Error = "Przekroczono czas oczekiwania na osobny proces liczący tego klienta (2 min).";
                            return item;
                        }
                    }

                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;

                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    {
                        item.Status = "FAILED";
                        item.Error = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"Proces potomny zakończył się kodem {process.ExitCode} bez wyniku.";
                        _logger.LogWarning("Proces liczący kadrowo-płacowe dla klienta NIP={Nip} (baza {Database}) zakończył się bez wyniku: {Error}",
                            spec.Nip, clientDatabaseName, item.Error);
                        return item;
                    }

                    string resultJson = ExtractBetweenMarkers(stdout, global::NexoBridge.Program.PayrollWorkerResultStartMarker, global::NexoBridge.Program.PayrollWorkerResultEndMarker);
                    if (resultJson == null)
                    {
                        item.Status = "FAILED";
                        item.Error = "Proces potomny nie zwrócił rozpoznawalnego wyniku (brak znaczników w stdout).";
                        _logger.LogWarning(
                            "Proces liczący kadrowo-płacowe dla klienta NIP={Nip} (baza {Database}): nie znaleziono znaczników wyniku w stdout. " +
                            "Surowe stdout ({StdoutLength} znaków): {Stdout} | stderr: {Stderr}",
                            spec.Nip, clientDatabaseName, stdout.Length, Truncate(stdout, 2000), Truncate(stderr, 2000));
                        return item;
                    }

                    PayrollClientWorkerResponse response;
                    try
                    {
                        response = JsonSerializer.Deserialize<PayrollClientWorkerResponse>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (JsonException ex)
                    {
                        item.Status = "FAILED";
                        item.Error = $"Nie udało się rozebrać wyniku procesu potomnego: {ex.Message}";
                        _logger.LogWarning(ex,
                            "Proces liczący kadrowo-płacowe dla klienta NIP={Nip} (baza {Database}): niepoprawny JSON między znacznikami: {ResultJson}",
                            spec.Nip, clientDatabaseName, Truncate(resultJson, 2000));
                        return item;
                    }

                    item.Status = response.Status;
                    item.Error = response.Error;
                    item.Lines = response.Lines ?? new List<PayrollFeeLineDto>();

                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        _logger.LogWarning("Proces liczący kadrowo-płacowe dla klienta NIP={Nip} (baza {Database}) zgłosił ostrzeżenia: {Stderr}",
                            spec.Nip, clientDatabaseName, stderr.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                item.Status = "FAILED";
                item.Error = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "Nie udało się uruchomić osobnego procesu liczącego pozycje kadrowo-płacowe dla klienta NIP={Nip} (baza {Database}).",
                    spec.Nip, clientDatabaseName);
            }

            return item;
        }

        /// <summary>Wycina fragment między pierwszym wystąpieniem startMarker a następującym po nim
        /// endMarker. Zwraca null, jeśli któregoś ze znaczników brakuje - patrz uzasadnienie przy
        /// definicji znaczników w Program.cs.</summary>
        private static string ExtractBetweenMarkers(string text, string startMarker, string endMarker)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            int startIndex = text.IndexOf(startMarker, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                return null;
            }

            startIndex += startMarker.Length;
            int endIndex = text.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
            if (endIndex < 0)
            {
                return null;
            }

            return text.Substring(startIndex, endIndex - startIndex);
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength) + "... (obcięto)";
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Proces mógł już się zakończyć między timeoutem a próbą zabicia - nieistotne.
            }
        }

        /// <summary>Ustala, jak ponownie odpalić TEN SAM plik wykonywalny NexoBridge jako proces
        /// potomny - obsługuje zarówno wdrożenie jako samodzielny apphost (NexoBridge.exe), jak i
        /// uruchomienie przez "dotnet NexoBridge.dll" (wtedy trzeba wywołać dotnet z dll-ką jako
        /// pierwszym argumentem).</summary>
        private static (string FileName, string ArgsPrefix) ResolveSelfLaunchCommand()
        {
            string processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                processPath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            string fileNameOnly = Path.GetFileNameWithoutExtension(processPath ?? string.Empty);
            if (string.Equals(fileNameOnly, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                return (processPath, $"\"{dllPath}\" ");
            }

            return (processPath, string.Empty);
        }
    }
}
