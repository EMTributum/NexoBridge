using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    /// <summary>
    /// Retroaktywny backfill 2026 (linki do podglądu faktur zamiast podpiętych PDF-ów).
    ///
    /// WAŻNE - dlaczego każdy klient/baza dostaje WŁASNY, osobny proces systemowy zamiast pętli
    /// logowań w tym samym procesie NexoBridge (nawet z SferaSessionGate): to repo ma już
    /// udokumentowane, produkcyjne doświadczenie (patrz PayrollCountsService, sekcja "DLACZEGO OSOBNY
    /// PROCES, A NIE SAMA BLOKADA"), że powtarzane logowania Sfery w JEDNYM procesie kaskadowo psują
    /// jej stan statyczny (np. WPF Application.Current.Resources dostaje zduplikowany klucz przy
    /// drugim Uruchom()) - sama serializacja sesji NIE wystarcza przy większej liczbie klientów.
    /// Jedyny sprawdzony fix: każdy klient/baza liczony w osobnym, świeżym procesie (ten sam wzorzec
    /// co "--payroll-client-worker").
    ///
    /// (1) EnumerateAsync (orkiestrator, w długo działającym procesie NexoBridge) - dla każdego
    ///     klienta odpala osobny proces potomny ("--backfill-enumerate-client-worker"), zbiera wyniki.
    ///     EnumerateOneClient - rzeczywista logika enumeracji, wołana WEWNĄTRZ procesu potomnego.
    /// (2) WriteCommentsAsync (orkiestrator) - grupuje wiersze z gotowym ViewerUrl po bazie, dla każdej
    ///     bazy odpala osobny proces potomny ("--backfill-write-comments-worker").
    ///     WriteCommentsForDatabase - rzeczywista logika zapisu, wołana WEWNĄTRZ procesu potomnego,
    ///     reużywa AttachmentService.NapiszKomentarzDlaCelu - tę samą, potwierdzoną na żywo ścieżkę
    ///     zapisu, którą na bieżąco stosuje produkcyjny import.
    /// </summary>
    public class BackfillService
    {
        private const int EnumerateClientTimeoutMinutes = 8;
        // Limit procesu zapisującego komentarze rośnie z liczbą wierszy bazy: stałe 5 minut przy kilku tysiącach
        // wierszy (odszukanie zapisu + odczyt komentarzy + zapis na wiersz) kończyło się zabiciem procesu i
        // policzeniem WSZYSTKICH wierszy bazy jako błędnych, mimo że część komentarzy była już zapisana.
        private const int WriteCommentsClientTimeoutMinutes = 5;
        private const double WriteCommentsSecondsPerRow = 1.0;
        private const int WriteCommentsMaxTimeoutMinutes = 180;

        private static TimeSpan WriteCommentsTimeout(int rowCount)
        {
            double minutes = WriteCommentsClientTimeoutMinutes + Math.Max(0, rowCount) * WriteCommentsSecondsPerRow / 60.0;
            return TimeSpan.FromMinutes(Math.Min(WriteCommentsMaxTimeoutMinutes, minutes));
        }

        // Rachmistrz nie ma dekretów - interfejs IDekrety nie istnieje w DLL-ach nexo, więc go nie szukamy.
        private static readonly string[] ManagerInterfaces = { "IZapisyWKPiR", "IZapisyWEwidencjiVAT", "IZapisyWEP" };

        private static readonly Dictionary<string, string> EntityTypeByInterface = new(StringComparer.OrdinalIgnoreCase)
        {
            ["IZapisyWKPiR"] = "KPiR",
            ["IZapisyWEwidencjiVAT"] = "VAT",
            ["IZapisyWEP"] = "EP"
        };

        private static readonly Dictionary<string, string> InterfaceByEntityType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["KPiR"] = "IZapisyWKPiR",
            ["VAT"] = "IZapisyWEwidencjiVAT",
            ["EP"] = "IZapisyWEP"
        };

        // Kolejność ma znaczenie: DokumentDoKsiegowania.NumerDokumentu (i jego Zrodlowy/Docelowy
        // warianty) to ta sama relacja, przez którą produkcyjny InvoiceDocumentMatcher/AttachmentService
        // dopasowuje faktury - to CZYSTY numer faktury. Bezpośrednie pola na samej encji KPiR/VAT/EP
        // (np. "Numer") bywają etykietą złożoną z typu dokumentu + numeru (np. "FS FV/2026/07/1" zamiast
        // "FV/2026/07/1"), co psuje wyszukiwanie w Scanye po numerze - dlatego relacyjne ścieżki mają
        // pierwszeństwo, a pola bezpośrednie są tylko fallbackiem gdy relacji brak.
        private static readonly string[] NumberPaths =
        {
            "DokumentDoKsiegowania.NumerDokumentu",
            "ZrodlowyDokumentDoKsiegowania.NumerDokumentu",
            "DocelowyDokumentDoKsiegowania.NumerDokumentu",
            "NumerDokumentu", "Numer", "NrDokumentu", "NumerWlasny", "NumerPelny"
        };

        private static readonly string[] NipPaths =
        {
            "DokumentDoKsiegowania.PodmiotHistoria.NIP",
            "ZrodlowyDokumentDoKsiegowania.PodmiotHistoria.NIP",
            "DocelowyDokumentDoKsiegowania.PodmiotHistoria.NIP",
            "PodmiotHistoria.NIP", "Podmiot.NIP", "PodmiotZapisu.NIP"
        };

        private static readonly string[] DatePaths =
        {
            "Data", "DataZdarzenia", "DataWpisu", "DataOtrzymania", "DataZakupu", "DataSprzedazy", "DataDokumentu",
            // Zapisy VAT nie mają żadnej z powyższych - bez tych pól filtr roku ich nie obejmował
            // i backfill brał zapisy VAT ze wszystkich lat.
            "DataWystawienia", "DataSprzedazyOtrzymania"
        };

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<BackfillService> _logger;

        public BackfillService(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<BackfillService>();
        }

        // ================== ORKIESTRACJA (długo działający proces NexoBridge) ==================

        public async Task<BackfillEnumerateReport> EnumerateAsync(BackfillEnumerateJob job, Action<int, string> raportujPostep, CancellationToken cancellationToken)
        {
            var report = new BackfillEnumerateReport { JobId = job.JobId };
            int total = Math.Max(1, job.Clients.Count);

            for (int i = 0; i < job.Clients.Count; i++)
            {
                BackfillClientRef client = job.Clients[i];
                raportujPostep?.Invoke((int)(i * 100m / total), $"Klient {i + 1}/{total}: {client.ClientName} ({client.DatabaseName})");

                if (string.IsNullOrWhiteSpace(client.DatabaseName))
                {
                    report.ClientErrors.Add($"{client.Nip}: brak zmapowanej bazy Nexo.");
                    report.ClientsFailed++;
                    continue;
                }

                (List<BackfillManifestRow> rows, int skippedAlreadyLinked, string error) = await EnumerateOneClientInChildProcessAsync(
                    job.Username, job.Password, client, job.Year, cancellationToken);

                if (error != null)
                {
                    _logger.LogError("[BACKFILL ENUMERATE BŁĄD] Klient {Nip} ({Database}): {Message}", client.Nip, client.DatabaseName, error);
                    report.ClientErrors.Add($"{client.Nip} ({client.DatabaseName}): {error}");
                    report.ClientsFailed++;
                    continue;
                }

                report.Rows.AddRange(rows);
                report.SkippedAlreadyLinked += skippedAlreadyLinked;
                report.ClientsOk++;
            }

            raportujPostep?.Invoke(100, $"Zakończono. Klienci OK={report.ClientsOk}, błąd={report.ClientsFailed}, wierszy={report.Rows.Count}.");
            report.Status = report.ClientsFailed > 0 && report.ClientsOk == 0 ? "FAILED" : report.ClientsFailed > 0 ? "PARTIAL_SUCCESS" : "SUCCESS";
            report.Message = $"Klienci OK={report.ClientsOk}, błąd={report.ClientsFailed}, wierszy manifestu={report.Rows.Count}, pominięte (już miały link)={report.SkippedAlreadyLinked}, z PDF={report.Rows.Count(r => r.HasPdf)}.";
            return report;
        }

        public async Task<BackfillWriteCommentsReport> WriteCommentsAsync(BackfillWriteCommentsJob job, Action<int, string> raportujPostep, CancellationToken cancellationToken)
        {
            var report = new BackfillWriteCommentsReport { JobId = job.JobId };
            var groups = job.Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.ViewerUrl))
                .GroupBy(r => r.DatabaseName)
                .ToList();
            int total = Math.Max(1, groups.Count);

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                string dbName = group.Key;
                List<BackfillCommentRow> rows = group.ToList();
                raportujPostep?.Invoke((int)(i * 100m / total), $"Baza {i + 1}/{total}: {dbName}");

                (WriteCommentsResult result, string error) = await WriteCommentsInChildProcessAsync(
                    job.Username, job.Password, dbName, rows, cancellationToken);

                if (error != null)
                {
                    _logger.LogError("[BACKFILL KOMENTARZE BŁĄD] Baza {Database}: {Message}", dbName, error);
                    foreach (var row in rows)
                    {
                        report.Errors.Add($"{dbName}/{row.EntityType}#{row.RachmistrzId}: {error}");
                    }
                    report.Failed += rows.Count;
                    continue;
                }

                report.Written += result.Written;
                report.SkippedAlreadyLinked += result.SkippedAlreadyLinked;
                report.Failed += result.Failed;
                report.Errors.AddRange(result.Errors);
            }

            raportujPostep?.Invoke(100, $"Zakończono. Zapisane={report.Written}, pominięte (już miały link)={report.SkippedAlreadyLinked}, błędy={report.Failed}.");
            report.Status = report.Failed > 0 && report.Written == 0 && report.SkippedAlreadyLinked == 0 ? "FAILED" : report.Failed > 0 ? "PARTIAL_SUCCESS" : "SUCCESS";
            report.Message = $"Zapisane={report.Written}, pominięte (już miały link)={report.SkippedAlreadyLinked}, błędy={report.Failed}.";
            return report;
        }

        // ================== PRACA W IZOLOWANYM PROCESIE POTOMNYM (wołane z Program.cs) ==================

        // Fragment ścieżki linku do podglądu (KLASYFIKATOR_PUBLIC_BASE_URL/faktury/{id}) - niezależny od hosta,
        // więc komentarze zapisane przy innym adresie bazowym też liczą się jako "już podpięte".
        private const string ViewerUrlMarker = "/faktury/";

        public List<BackfillManifestRow> EnumerateOneClient(Uchwyt sfera, BackfillClientRef client, int year)
        {
            return EnumerateOneClient(sfera, client, year, out _);
        }

        public List<BackfillManifestRow> EnumerateOneClient(Uchwyt sfera, BackfillClientRef client, int year, out int skippedAlreadyLinked)
        {
            skippedAlreadyLinked = 0;
            var rows = new List<BackfillManifestRow>();
            object biblioteka = SferaReflectionHelpers.GetManagerByInterfaceName(sfera, "IBibliotekaZalacznikow");

            // Zapisy, które już mają link do podglądu, wypadają tutaj - zanim pójdzie zapytanie do biblioteki
            // załączników, zanim wiersz trafi do Klasyfikatora (dopasowanie w Scanye) i z powrotem do zapisu
            // komentarzy. Jedno zapytanie na bazę. Gdy się nie uda, enumerujemy wszystko jak wcześniej -
            // zapis i tak pominie duplikaty (JuzMaLinkKomentarz).
            object komentarzeManager = SferaReflectionHelpers.GetManagerByInterfaceName(sfera, "IKomentarzeNexo");
            var (podpieteKsiegowe, podpieteVat) = NexoCommentReader.EntityIdsWithLink(komentarzeManager, ViewerUrlMarker, out string bladOdczytu);
            if (bladOdczytu != null)
            {
                _logger.LogWarning("[BACKFILL ENUMERATE] {Database}: nie udało się ustalić zapisów z linkiem ({Blad}) - enumeruję wszystkie.", client.DatabaseName, bladOdczytu);
            }

            foreach (string interfaceName in ManagerInterfaces)
            {
                object manager = SferaReflectionHelpers.GetManagerByInterfaceName(sfera, interfaceName);
                if (manager == null)
                {
                    continue;
                }

                string entityType = EntityTypeByInterface[interfaceName];
                HashSet<int> podpiete = string.Equals(entityType, "VAT", StringComparison.OrdinalIgnoreCase) ? podpieteVat : podpieteKsiegowe;
                foreach (object entity in ReadAllRecords(manager, year))
                {
                    int? id = SferaReflectionHelpers.ReadIntCandidate(entity, "Id");
                    if (podpiete != null && id.HasValue && podpiete.Contains(id.Value))
                    {
                        if (CzyZRoku(entity, year))
                        {
                            skippedAlreadyLinked++;
                        }
                        continue;
                    }

                    BackfillManifestRow row = BuildRow(client, entityType, entity, biblioteka, year);
                    if (row != null)
                    {
                        rows.Add(row);
                    }
                }
            }

            return rows;
        }

        // Ten sam filtr roku co w BuildRow - żeby licznik pominiętych obejmował tylko zapisy z backfillowanego roku.
        private static bool CzyZRoku(object entity, int year)
        {
            int? recordYear = ResolveYear(entity);
            return !recordYear.HasValue || recordYear.Value == year;
        }

        public sealed class WriteCommentsResult
        {
            public int Written { get; set; }
            public int SkippedAlreadyLinked { get; set; }
            public int Failed { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        public WriteCommentsResult WriteCommentsForDatabase(Uchwyt sfera, List<BackfillCommentRow> rows)
        {
            var result = new WriteCommentsResult();
            string dbLabel = rows.FirstOrDefault()?.DatabaseName ?? "?";

            var attachmentLogger = _loggerFactory.CreateLogger<AttachmentService>();
            var attachmentService = new AttachmentService(sfera, attachmentLogger);

            object komentarzeManager = SferaReflectionHelpers.GetManagerByInterfaceName(sfera, "IKomentarzeNexo");
            if (komentarzeManager == null)
            {
                foreach (var row in rows)
                {
                    result.Errors.Add($"{dbLabel}/{row.EntityType}#{row.RachmistrzId}: brak IKomentarzeNexo w tej bazie.");
                }
                result.Failed += rows.Count;
                return result;
            }

            var managersCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (!InterfaceByEntityType.TryGetValue(row.EntityType ?? "", out string interfaceName))
                {
                    result.Errors.Add($"{dbLabel}/{row.EntityType}#{row.RachmistrzId} (numer={row.NumerDokumentu}): nieznany typ encji.");
                    result.Failed++;
                    continue;
                }

                if (!managersCache.TryGetValue(row.EntityType, out object manager))
                {
                    manager = SferaReflectionHelpers.GetManagerByInterfaceName(sfera, interfaceName);
                    managersCache[row.EntityType] = manager;
                }

                object entity = FindEntityById(manager, row.RachmistrzId);
                if (entity == null)
                {
                    result.Errors.Add($"{dbLabel}/{row.EntityType}#{row.RachmistrzId} (numer={row.NumerDokumentu}): nie znaleziono encji.");
                    result.Failed++;
                    continue;
                }

                // Idempotencja przy ponownym uruchomieniu backfillu dla tych samych klientów: jeśli
                // ten sam link już jest w komentarzach tego dokumentu, nie dopisuj drugiego.
                if (JuzMaLinkKomentarz(komentarzeManager, entity, row.ViewerUrl, out string bladOdczytu))
                {
                    result.SkippedAlreadyLinked++;
                    continue;
                }

                if (bladOdczytu != null)
                {
                    result.Errors.Add($"{dbLabel}/{row.EntityType}#{row.RachmistrzId} (numer={row.NumerDokumentu}): ostrzeżenie - nie udało się sprawdzić istniejących komentarzy ({bladOdczytu}), zapisuję link mimo to.");
                }

                var cel = new AttachmentService.AttachmentTargetRef
                {
                    Entity = entity,
                    ManagerKey = row.EntityType,
                    EntityId = row.RachmistrzId,
                    DocumentId = row.RachmistrzId,
                    EntityType = entity.GetType().FullName
                };

                string htmlLink = $"<a href=\"{row.ViewerUrl}\">Podgląd faktury (oryginał)</a>";
                string plainFallback = $"Podgląd faktury: {row.ViewerUrl}";

                bool ok = attachmentService.NapiszKomentarzDlaCelu(
                    komentarzeManager, sfera, htmlLink, plainFallback, cel,
                    "backfill", savedByFreshSession: false,
                    out AttachmentService.AttachmentSaveResult _, out string blad);

                if (ok)
                {
                    result.Written++;
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add($"{dbLabel}/{row.EntityType}#{row.RachmistrzId} (numer={row.NumerDokumentu}): {blad}");
                }
            }

            return result;
        }

        // ================== SPAWNOWANIE PROCESÓW POTOMNYCH (analogiczne do PayrollCountsService) ==================

        private async Task<(List<BackfillManifestRow> Rows, int SkippedAlreadyLinked, string Error)> EnumerateOneClientInChildProcessAsync(
            string username, string password, BackfillClientRef client, int year, CancellationToken cancellationToken)
        {
            var request = new BackfillEnumerateClientWorkerRequest
            {
                Username = username,
                Password = password,
                ClientNip = client.Nip,
                ClientName = client.ClientName,
                DatabaseName = client.DatabaseName,
                Year = year
            };

            (string stdout, string stderr, int exitCode, string spawnError) = await RunChildWorkerAsync(
                "--backfill-enumerate-client-worker", request, TimeSpan.FromMinutes(EnumerateClientTimeoutMinutes), cancellationToken);

            if (spawnError != null)
            {
                return (new List<BackfillManifestRow>(), 0, spawnError);
            }

            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                string error = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"Proces potomny zakończył się kodem {exitCode} bez wyniku.";
                _logger.LogWarning(
                    "Proces enumerujący klienta {Nip} (baza {Database}) zakończył się bez wyniku: {Error}",
                    client.Nip, client.DatabaseName, error);
                return (new List<BackfillManifestRow>(), 0, error);
            }

            string resultJson = ExtractBetweenMarkers(stdout, global::NexoBridge.Program.BackfillWorkerResultStartMarker, global::NexoBridge.Program.BackfillWorkerResultEndMarker);
            if (resultJson == null)
            {
                _logger.LogWarning(
                    "Proces enumerujący klienta {Nip} (baza {Database}): brak znaczników wyniku w stdout. stdout={Stdout} stderr={Stderr}",
                    client.Nip, client.DatabaseName, Truncate(stdout, 2000), Truncate(stderr, 2000));
                return (new List<BackfillManifestRow>(), 0, "Proces potomny nie zwrócił rozpoznawalnego wyniku (brak znaczników w stdout).");
            }

            BackfillEnumerateClientWorkerResponse response;
            try
            {
                response = JsonSerializer.Deserialize<BackfillEnumerateClientWorkerResponse>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Proces enumerujący klienta {Nip} (baza {Database}): niepoprawny JSON wyniku: {ResultJson}",
                    client.Nip, client.DatabaseName, Truncate(resultJson, 2000));
                return (new List<BackfillManifestRow>(), 0, $"Nie udało się rozebrać wyniku procesu potomnego: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogWarning("Proces enumerujący klienta {Nip} (baza {Database}) zgłosił ostrzeżenia: {Stderr}",
                    client.Nip, client.DatabaseName, stderr.Trim());
            }

            if (!string.Equals(response.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return (new List<BackfillManifestRow>(), 0, response.Error ?? "Nieznany błąd procesu potomnego.");
            }

            return (response.Rows ?? new List<BackfillManifestRow>(), response.SkippedAlreadyLinked, null);
        }

        private async Task<(WriteCommentsResult Result, string Error)> WriteCommentsInChildProcessAsync(
            string username, string password, string databaseName, List<BackfillCommentRow> rows, CancellationToken cancellationToken)
        {
            var request = new BackfillWriteCommentsClientWorkerRequest
            {
                Username = username,
                Password = password,
                DatabaseName = databaseName,
                Rows = rows
            };

            (string stdout, string stderr, int exitCode, string spawnError) = await RunChildWorkerAsync(
                "--backfill-write-comments-worker", request, WriteCommentsTimeout(rows.Count), cancellationToken);

            if (spawnError != null)
            {
                return (null, spawnError);
            }

            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                string error = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"Proces potomny zakończył się kodem {exitCode} bez wyniku.";
                _logger.LogWarning("Proces zapisujący komentarze dla bazy {Database} zakończył się bez wyniku: {Error}", databaseName, error);
                return (null, error);
            }

            string resultJson = ExtractBetweenMarkers(stdout, global::NexoBridge.Program.BackfillWorkerResultStartMarker, global::NexoBridge.Program.BackfillWorkerResultEndMarker);
            if (resultJson == null)
            {
                _logger.LogWarning(
                    "Proces zapisujący komentarze dla bazy {Database}: brak znaczników wyniku w stdout. stdout={Stdout} stderr={Stderr}",
                    databaseName, Truncate(stdout, 2000), Truncate(stderr, 2000));
                return (null, "Proces potomny nie zwrócił rozpoznawalnego wyniku (brak znaczników w stdout).");
            }

            BackfillWriteCommentsClientWorkerResponse response;
            try
            {
                response = JsonSerializer.Deserialize<BackfillWriteCommentsClientWorkerResponse>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Proces zapisujący komentarze dla bazy {Database}: niepoprawny JSON wyniku: {ResultJson}",
                    databaseName, Truncate(resultJson, 2000));
                return (null, $"Nie udało się rozebrać wyniku procesu potomnego: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogWarning("Proces zapisujący komentarze dla bazy {Database} zgłosił ostrzeżenia: {Stderr}", databaseName, stderr.Trim());
            }

            if (!string.Equals(response.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return (null, response.Error ?? "Nieznany błąd procesu potomnego.");
            }

            return (new WriteCommentsResult
            {
                Written = response.Written,
                SkippedAlreadyLinked = response.SkippedAlreadyLinked,
                Failed = response.Failed,
                Errors = response.Errors ?? new List<string>()
            }, null);
        }

        /// <summary>Odpala ponownie ten sam plik wykonywalny NexoBridge jako proces potomny z podaną
        /// flagą trybu, wysyła `request` jako JSON na stdin, czeka z limitem czasu i zwraca surowe
        /// stdout/stderr/kod wyjścia - identyczny wzorzec co
        /// PayrollCountsService.PoliczDlaKlientaWProcesie, wyniesiony tutaj żeby obsłużyć oba tryby
        /// procesu potomnego backfillu jedną implementacją.</summary>
        private static async Task<(string Stdout, string Stderr, int ExitCode, string SpawnError)> RunChildWorkerAsync<TRequest>(
            string workerFlag, TRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                (string fileName, string argsPrefix) = ResolveSelfLaunchCommand();
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = argsPrefix + workerFlag,
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

                    // Twardy limit na JEDNEGO klienta/bazę - proces potomny nie może wisieć bez końca
                    // (np. zawieszony na oczekiwaniu na licencję); po przekroczeniu zabijamy go, reszta
                    // klientów/baz idzie dalej.
                    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutCts.CancelAfter(timeout);
                        try
                        {
                            await process.WaitForExitAsync(timeoutCts.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            TryKill(process);
                            return (null, null, -1, $"Przekroczono czas oczekiwania na osobny proces ({timeout.TotalMinutes:0} min).");
                        }
                    }

                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;
                    return (stdout, stderr, process.ExitCode, null);
                }
            }
            catch (Exception ex)
            {
                return (null, null, -1, ex.GetBaseException().Message);
            }
        }

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

        /// <summary>Identyczna logika co PayrollCountsService.ResolveSelfLaunchCommand - obsługuje
        /// zarówno wdrożenie jako samodzielny apphost (NexoBridge.exe), jak i uruchomienie przez
        /// "dotnet NexoBridge.dll".</summary>
        private static (string FileName, string ArgsPrefix) ResolveSelfLaunchCommand()
        {
            string processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                processPath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            string fileNameOnly = System.IO.Path.GetFileNameWithoutExtension(processPath ?? string.Empty);
            if (string.Equals(fileNameOnly, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                return (processPath, $"\"{dllPath}\" ");
            }

            return (processPath, string.Empty);
        }

        // ================== POMOCNICZE (odczyt/refleksja Sfery) ==================

        // Odczytuje istniejące komentarze na encji (IKomentarzeNexo.Dane.Wszystkie(encja) - patrz
        // NexoCommentReader) i sprawdza, czy któryś aktywny już zawiera ten sam link - zabezpieczenie przed
        // duplikatami przy ponownym uruchomieniu backfillu albo backfillu dokumentów, które dostały link przy
        // imporcie. Jeśli odczyt się nie uda, celowo NIE blokuje zapisu (lepszy możliwy duplikat niż cichy
        // brak linku), ale błąd odczytu trafia do wyniku.
        private static bool JuzMaLinkKomentarz(object komentarzeManager, object entity, string viewerUrl, out string bladOdczytu)
        {
            bladOdczytu = null;
            if (komentarzeManager == null || entity == null || string.IsNullOrWhiteSpace(viewerUrl))
            {
                return false;
            }

            var komentarze = NexoCommentReader.ForEntity(komentarzeManager, entity, out bladOdczytu);
            return komentarze.Any(k => !k.Deleted && k.ContainsUrl(viewerUrl));
        }

        private static BackfillManifestRow BuildRow(BackfillClientRef client, string entityType, object entity, object biblioteka, int year)
        {
            List<string> kandydaci = ZbudujKandydatowNumeru(entity, entityType);
            string numer = kandydaci.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(numer))
            {
                return null;
            }

            int? recordYear = ResolveYear(entity);
            if (recordYear.HasValue && recordYear.Value != year)
            {
                return null;
            }

            string vendorNip = SferaReflectionHelpers.ReadStringCandidate(entity, NipPaths);
            int rachmistrzId = SferaReflectionHelpers.ReadIntCandidate(entity, "Id") ?? 0;

            bool hasPdf = false;
            string pdfBase64 = null;
            if (biblioteka != null)
            {
                try
                {
                    object result = InvokeBestMethod(biblioteka, "PodajZalaczniki", entity);
                    object firstAttachment = (result as IEnumerable)?.Cast<object>().FirstOrDefault();
                    if (firstAttachment != null)
                    {
                        byte[] bytes = ExtractAttachmentBytes(firstAttachment);
                        if (bytes != null && bytes.Length > 0)
                        {
                            pdfBase64 = Convert.ToBase64String(bytes);
                            hasPdf = true;
                        }
                    }
                }
                catch
                {
                    // Brak dostępu do załącznika nie powinien wykluczać wiersza z manifestu.
                }
            }

            return new BackfillManifestRow
            {
                ClientNip = client.Nip,
                DatabaseName = client.DatabaseName,
                EntityType = entityType,
                RachmistrzId = rachmistrzId,
                VendorNip = vendorNip,
                NumerDokumentu = numer,
                NumerKandydaci = kandydaci,
                RecordYear = recordYear,
                HasPdf = hasPdf,
                PdfBase64 = pdfBase64
            };
        }

        // Numer dokumentu bywa zapisany w Rachmistrzu na dwa sposoby, zależnie od tego, jak dokument trafił do
        // poczekalni (sprawdzone na żywych bazach w NexoBridgeKonsola --backfill-number-dump):
        // - czysty numer na samym zapisie: KPiR/EP NumerDowoduKsiegowego ("16446"), VAT NumerDokumentu
        //   ("FV/GD/24/01-00869") - to jest numer widoczny w kolumnie "Numer" i numer faktury w Scanye;
        // - etykieta na dokumencie do księgowania: "<typ> [<typ>] <lp> <numer>", np. "FZ 2 16446",
        //   "FZ FZ 1 FV/GD/24/01-00869", "FZ 7 FS 113/2026"; w innych bazach to bywa czysty numer.
        // Dlatego do Klasyfikatora idą wszystkie warianty (od najbardziej prawdopodobnego), a on sprawdza je
        // po kolei w indeksie faktur Scanye. Samo StripKnownDocumentTypePrefix nie wystarczało: nie ruszało
        // numerów bez "/" (paragony "FZ 2 16446") i odcinało prefiksy będące częścią numeru ("FS 113/2026").
        private static readonly System.Text.RegularExpressions.Regex EtykietaNumeruRegex = new(
            @"^(?:\p{L}{1,4}\s+){1,2}\d+\s+(?<numer>.+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static List<string> ZbudujKandydatowNumeru(object entity, string entityType)
        {
            var surowe = new List<string>();
            bool vat = string.Equals(entityType, "VAT", StringComparison.OrdinalIgnoreCase);
            surowe.Add(SferaReflectionHelpers.ReadStringCandidate(entity, vat ? "NumerDokumentu" : "NumerDowoduKsiegowego"));

            foreach (string sciezka in NumberPaths)
            {
                string wartosc = SferaReflectionHelpers.ReadStringCandidate(entity, sciezka);
                if (string.IsNullOrWhiteSpace(wartosc))
                {
                    continue;
                }

                surowe.Add(wartosc);
                var etykieta = EtykietaNumeruRegex.Match(wartosc.Trim());
                if (etykieta.Success)
                {
                    surowe.Add(etykieta.Groups["numer"].Value);
                }
            }

            // Dotychczasowa reguła (pierwsza niepusta ścieżka + obcięcie prefiksu) jako ostatni wariant.
            surowe.Add(StripKnownDocumentTypePrefix(SferaReflectionHelpers.ReadStringCandidate(entity, NumberPaths)));

            var widziane = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kandydaci = new List<string>();
            foreach (string wariant in surowe)
            {
                string czysty = wariant?.Trim();
                if (!string.IsNullOrWhiteSpace(czysty) && widziane.Add(string.Concat(czysty.Where(c => !char.IsWhiteSpace(c)))))
                {
                    kandydaci.Add(czysty);
                }
            }

            return kandydaci;
        }

        // Zabezpieczenie na wypadek gdyby żadna ze ścieżek DokumentDoKsiegowania nie była wypełniona
        // (np. dla Dekretów) i ReadStringCandidate spadł na pole encji zawierające etykietę złożoną
        // z typu dokumentu + numeru, np. "FS FV/2026/07/1" albo "FZ 1 FV/05/2026/6" zamiast czystego
        // "FV/2026/07/1" - obserwowane empirycznie, psuje wyszukiwanie po numerze w Scanye. Jeśli
        // ostatni token zawiera "/" (wygląda jak prawdziwy numer faktury), a wcześniejsze tokeny są
        // krótkie (typ dokumentu/kolejny numer), odrzucamy je i zostawiamy tylko ostatni token.
        private static string StripKnownDocumentTypePrefix(string numer)
        {
            if (string.IsNullOrWhiteSpace(numer))
            {
                return numer;
            }

            string[] parts = numer.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return numer;
            }

            string tail = parts[^1];
            if (tail.Contains('/') && parts.Take(parts.Length - 1).All(p => p.Length <= 4))
            {
                // "RF" nie jest etykietą typu dokumentu, tylko częścią numeru raportu fiskalnego nadawanego
                // przez Klasyfikator ("RF 3/05/2026") - odcięcie zostawiało "3/05/2026", którego nie ma
                // w Scanye, więc żaden raport fiskalny nie dostawał linku w backfillu.
                string ostatniPrefiks = parts[^2];
                if (NumberPrefixesKeptWithNumber.Contains(ostatniPrefiks))
                {
                    return $"{ostatniPrefiks} {tail}";
                }

                return tail;
            }

            return numer;
        }

        private static readonly HashSet<string> NumberPrefixesKeptWithNumber = new(StringComparer.OrdinalIgnoreCase) { "RF" };

        private static int? ResolveYear(object entity)
        {
            foreach (string path in DatePaths)
            {
                if (SferaReflectionHelpers.TryReadPropertyPath(entity, path, out object value))
                {
                    if (value is DateTime dt)
                    {
                        return dt.Year;
                    }

                    if (value != null && DateTime.TryParse(value.ToString(), out DateTime parsed))
                    {
                        return parsed.Year;
                    }
                }
            }

            return null;
        }

        private static byte[] ExtractAttachmentBytes(object attachment)
        {
            object dane = SferaReflectionHelpers.TryReadPropertyPath(attachment, "Dane", out object d) ? d : attachment;
            return SferaReflectionHelpers.TryReadPropertyPath(dane, "Zawartosc", out object content) ? content as byte[] : null;
        }

        private static object FindEntityById(object manager, int id)
        {
            if (manager == null)
            {
                return null;
            }

            object dane = SferaReflectionHelpers.TryReadPropertyPath(manager, "Dane", out object d) ? d : manager;

            // Dane menedżerów zapisów (ZapisyWKPiRDane, ZapisyWEwidencjiVATDane, ZapisyWEPDane) mają
            // FindById(int) - jedno zapytanie zamiast skanu całej tabeli (potwierdzone w NexoBridgeKonsola
            // --comment-read-test). Znajdz(int) i bezparametrowego Wszystkie() te klasy nie mają.
            object znalezionaPoId = TryInvoke(() => dane.GetType().GetMethod("FindById", new[] { typeof(int) })?.Invoke(dane, new object[] { id }));
            if (znalezionaPoId != null)
            {
                return znalezionaPoId;
            }

            try
            {
                object result = InvokeBestMethod(dane, "Znajdz", id);
                if (result != null)
                {
                    return result;
                }
            }
            catch
            {
            }

            // Niektóre menedżery (np. IZapisyWEP, IZapisyWEwidencjiVAT) nie mają bezparametrowego
            // Wszystkie() - tylko Wszystkie(string[] razemZ) - dokładnie ten sam przypadek co w
            // ReadAllRecords poniżej. Próbujemy obu wariantów, inaczej enumeracja fallbackowa
            // milcząco zwraca pustą listę i encja "nie zostaje znaleziona" mimo że istnieje.
            foreach (object entity in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "Wszystkie"))))
            {
                if (SferaReflectionHelpers.ReadIntCandidate(entity, "Id") == id)
                {
                    return entity;
                }
            }

            foreach (object entity in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "Wszystkie", (object)Array.Empty<string>()))))
            {
                if (SferaReflectionHelpers.ReadIntCandidate(entity, "Id") == id)
                {
                    return entity;
                }
            }

            foreach (object entity in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "WszystkieDostepne", Array.Empty<string>()))))
            {
                if (SferaReflectionHelpers.ReadIntCandidate(entity, "Id") == id)
                {
                    return entity;
                }
            }

            return null;
        }

        private static IEnumerable<object> ReadAllRecords(object manager, int year)
        {
            object dane = SferaReflectionHelpers.TryReadPropertyPath(manager, "Dane", out object d) ? d : null;
            if (dane == null)
            {
                yield break;
            }

            DateTime dataOd = new DateTime(year, 1, 1);
            DateTime dataDo = new DateTime(year, 12, 31);
            var seen = new HashSet<string>();

            foreach (object item in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "PobierzZapisyZOkresu", dataOd, dataDo))))
            {
                if (seen.Add(RecordKey(item))) yield return item;
            }

            foreach (object item in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "PobierzZapisyZOkresuWgDatyZdarzenia", dataOd, dataDo))))
            {
                if (seen.Add(RecordKey(item))) yield return item;
            }

            for (int month = 1; month <= 12; month++)
            {
                DateTime monthStart = new DateTime(year, month, 1);
                foreach (object item in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "PobierzZapisyZMiesiaca", monthStart))))
                {
                    if (seen.Add(RecordKey(item))) yield return item;
                }
            }

            foreach (object item in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "WszystkieDostepne", Array.Empty<string>()))))
            {
                if (seen.Add(RecordKey(item))) yield return item;
            }

            foreach (object item in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "Wszystkie"))))
            {
                if (seen.Add(RecordKey(item))) yield return item;
            }

            foreach (object item in EnumerateAny(TryInvoke(() => InvokeBestMethod(dane, "Wszystkie", (object)Array.Empty<string>()))))
            {
                if (seen.Add(RecordKey(item))) yield return item;
            }
        }

        private static object TryInvoke(Func<object> action)
        {
            try
            {
                return action();
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<object> EnumerateAny(object result)
        {
            if (result is not IEnumerable enumerable)
            {
                yield break;
            }

            foreach (object item in enumerable)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }

        private static string RecordKey(object entity)
        {
            int? id = SferaReflectionHelpers.ReadIntCandidate(entity, "Id");
            return $"{entity?.GetType().FullName}:{id}";
        }

        private static object InvokeBestMethod(object target, string methodName, params object[] args)
        {
            if (target == null)
            {
                return null;
            }

            foreach (MethodInfo method in target.GetType().GetMethods().Where(m => m.Name == methodName && m.GetParameters().Length == args.Length))
            {
                ParameterInfo[] parameters = method.GetParameters();
                bool accepted = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (!CanAccept(parameters[i].ParameterType, args[i]))
                    {
                        accepted = false;
                        break;
                    }
                }

                if (accepted)
                {
                    return method.Invoke(target, args);
                }
            }

            return null;
        }

        private static bool CanAccept(Type parameterType, object value)
        {
            if (value == null)
            {
                return !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;
            }

            return parameterType.IsInstanceOfType(value);
        }
    }
}
