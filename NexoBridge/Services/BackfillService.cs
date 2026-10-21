using InsERT.Moria.Sfera;
using InsERT.Mox.Product;
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

        // Rachmistrz (JDG): zapisy KPiR / ewidencji VAT / EP. Rewizor (spółki, pełna księgowość): dekrety
        // (DokumentKsiegowy, IDokumentyKsiegowe) - tam wisiały dawne PDF-y i tam księgowa ogląda dokument; "IDekrety"
        // w Sferze nie istnieje. Dekret i jego zapis VAT to ta sama faktura, więc w Rewizorze linkujemy tylko dekret.
        private static readonly string[] RachmistrzManagerInterfaces = { "IZapisyWKPiR", "IZapisyWEwidencjiVAT", "IZapisyWEP" };
        private static readonly string[] RewizorManagerInterfaces = { "IDokumentyKsiegowe" };

        // Logowanie do bazy Rewizora produktem Rachmistrz (i odwrotnie) kończy się tym wyjątkiem już przy Uruchom()
        // (sprawdzone w NexoBridgeKonsola na Nexo_EMTRIBUTUM) - wtedy ponawiamy z drugim produktem.
        private const string ZlyProduktKomunikat = "Operation is not valid due to the current state of the object";

        private static readonly Dictionary<string, string> EntityTypeByInterface = new(StringComparer.OrdinalIgnoreCase)
        {
            ["IZapisyWKPiR"] = "KPiR",
            ["IZapisyWEwidencjiVAT"] = "VAT",
            ["IZapisyWEP"] = "EP",
            ["IDokumentyKsiegowe"] = "DK"
        };

        private static readonly Dictionary<string, string> InterfaceByEntityType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["KPiR"] = "IZapisyWKPiR",
            ["VAT"] = "IZapisyWEwidencjiVAT",
            ["EP"] = "IZapisyWEP",
            ["DK"] = "IDokumentyKsiegowe"
        };

        /// <summary>Produkt sesji Sfery dla bazy klienta: Rewizor tylko gdy wprost podany, domyślnie Rachmistrz.</summary>
        public static ProductId ResolveProduct(string product)
        {
            return string.Equals(product?.Trim(), nameof(ProductId.Rewizor), StringComparison.OrdinalIgnoreCase)
                ? ProductId.Rewizor
                : ProductId.Rachmistrz;
        }

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

        // Dekret Rewizora: DokumentDoKsiegowania.PodmiotHistoria bywa pusty (np. dekrety z wyciągów/karty), NIP jest
        // wtedy na samym dekrecie (Podmiot) albo na powiązanym zapisie VAT.
        private static readonly string[] NipPaths =
        {
            "DokumentDoKsiegowania.PodmiotHistoria.NIP",
            "ZrodlowyDokumentDoKsiegowania.PodmiotHistoria.NIP",
            "DocelowyDokumentDoKsiegowania.PodmiotHistoria.NIP",
            "PodmiotHistoria.NIP", "Podmiot.NIP", "PodmiotZapisu.NIP",
            "ZapisWEwidencjiVAT.Podmiot.NIP", "ZapisWEwidencjiVAT.PreviewNIP", "PreviewNIP"
        };

        private static readonly string[] DatePaths =
        {
            "Data", "DataZdarzenia", "DataWpisu", "DataOtrzymania", "DataZakupu", "DataSprzedazy", "DataDokumentu",
            // Zapisy VAT nie mają żadnej z powyższych - bez tych pól filtr roku ich nie obejmował
            // i backfill brał zapisy VAT ze wszystkich lat.
            "DataWystawienia", "DataSprzedazyOtrzymania",
            // Dekret Rewizora (DokumentKsiegowy) - data dowodu (NOT NULL), ta sama co w filtrze SferaZapisyQueries.
            "DataDowodu"
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
            var report = new BackfillEnumerateReport { JobId = job.JobId, LinkedIncluded = job.IncludeLinked };
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

                ProductId produkt = ResolveProduct(client.Product);
                (List<BackfillManifestRow> rows, int skippedAlreadyLinked, string error) = await EnumerateOneClientInChildProcessAsync(
                    job.Username, job.Password, client, produkt, job.Year, job.IncludeLinked, cancellationToken);

                if (error != null && error.Contains(ZlyProduktKomunikat, StringComparison.OrdinalIgnoreCase))
                {
                    // Program klienta w Klasyfikatorze bywa nieustawiony albo nieaktualny - baza sama mówi, że to nie ten produkt.
                    ProductId drugi = produkt == ProductId.Rewizor ? ProductId.Rachmistrz : ProductId.Rewizor;
                    _logger.LogWarning("[BACKFILL ENUMERATE] Klient {Nip} ({Database}): logowanie jako {Produkt} odrzucone - ponawiam jako {Drugi}.",
                        client.Nip, client.DatabaseName, produkt, drugi);
                    (rows, skippedAlreadyLinked, error) = await EnumerateOneClientInChildProcessAsync(
                        job.Username, job.Password, client, drugi, job.Year, job.IncludeLinked, cancellationToken);
                }

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
            report.Message = $"Klienci OK={report.ClientsOk}, błąd={report.ClientsFailed}, wierszy manifestu={report.Rows.Count}, pominięte (już miały link)={report.SkippedAlreadyLinked}, z istniejącym linkiem={report.Rows.Count(r => r.ExistingLinks?.Count > 0)}.";
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

                // Produkt ustalony przy enumeracji (wiersze go niosą) - wszystkie wiersze jednej bazy mają ten sam.
                ProductId produkt = ResolveProduct(rows.Select(r => r.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)));
                (WriteCommentsResult result, string error) = await WriteCommentsInChildProcessAsync(
                    job.Username, job.Password, dbName, produkt, rows, cancellationToken);

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
                report.Replaced += result.Replaced;
                report.SkippedAlreadyLinked += result.SkippedAlreadyLinked;
                report.Failed += result.Failed;
                report.Verified += result.Verified;
                report.Unverified += result.Unverified;
                report.Errors.AddRange(result.Errors);
            }

            string podsumowanie = $"Zapisane={report.Written}, podmienione={report.Replaced}, pominięte (już miały link)={report.SkippedAlreadyLinked}, błędy={report.Failed}, zweryfikowane={report.Verified}, niezweryfikowane={report.Unverified}.";
            raportujPostep?.Invoke(100, "Zakończono. " + podsumowanie);
            bool cokolwiekOk = report.Written + report.Replaced + report.SkippedAlreadyLinked > 0;
            report.Status = report.Failed > 0 && !cokolwiekOk ? "FAILED" : report.Failed > 0 || report.Unverified > 0 ? "PARTIAL_SUCCESS" : "SUCCESS";
            report.Message = podsumowanie;
            return report;
        }

        // ================== PRACA W IZOLOWANYM PROCESIE POTOMNYM (wołane z Program.cs) ==================

        public List<BackfillManifestRow> EnumerateOneClient(Uchwyt sfera, BackfillClientRef client, ProductId produkt, int year, bool includeLinked, out int skippedAlreadyLinked)
        {
            skippedAlreadyLinked = 0;
            var rows = new List<BackfillManifestRow>();

            // Komentarze z linkiem do podglądu - jedno zapytanie na bazę. W trybie zwykłym zapisy z linkiem wypadają
            // tutaj (zanim trafią do Klasyfikatora); przy includeLinked wracają z ExistingLinks do weryfikacji.
            object komentarzeManager = SferaReflectionHelpers.GetManagerByInterfaceName(sfera, "IKomentarzeNexo");
            NexoCommentReader.LinkedCommentIndex linki = NexoCommentReader.LinkedComments(komentarzeManager, out string bladOdczytu);
            if (linki == null)
            {
                if (includeLinked)
                {
                    // Bez listy istniejących linków weryfikacja/naprawa uznałaby podlinkowane zapisy za puste
                    // i dopisała drugi link - lepiej oddać błąd klienta.
                    throw new InvalidOperationException($"Nie udało się odczytać istniejących linków do podglądu: {bladOdczytu}");
                }

                _logger.LogWarning("[BACKFILL ENUMERATE] {Database}: nie udało się ustalić zapisów z linkiem ({Blad}) - enumeruję wszystkie, zapis i tak pominie duplikaty.", client.DatabaseName, bladOdczytu);
            }

            foreach (string interfaceName in produkt == ProductId.Rewizor ? RewizorManagerInterfaces : RachmistrzManagerInterfaces)
            {
                object manager = SferaReflectionHelpers.GetManagerByInterfaceName(sfera, interfaceName);
                if (manager == null)
                {
                    _logger.LogWarning("[BACKFILL ENUMERATE] {Database}: brak menedżera {Interface} w sesji {Produkt}.", client.DatabaseName, interfaceName, produkt);
                    continue;
                }

                string entityType = EntityTypeByInterface[interfaceName];
                Dictionary<int, List<NexoCommentReader.CommentInfo>> podpiete = linki?.ForEntityType(entityType);

                foreach (object entity in ReadRecordsFromYear(manager, entityType, year, client.DatabaseName))
                {
                    int? id = SferaReflectionHelpers.ReadIntCandidate(entity, "Id");
                    List<NexoCommentReader.CommentInfo> istniejace = null;
                    bool maLink = podpiete != null && id.HasValue && podpiete.TryGetValue(id.Value, out istniejace);
                    if (maLink && !includeLinked)
                    {
                        if (CzyZRoku(entity, year))
                        {
                            skippedAlreadyLinked++;
                        }
                        continue;
                    }

                    BackfillManifestRow row = BuildRow(client, entityType, entity, year);
                    if (row == null)
                    {
                        continue;
                    }

                    row.Product = produkt.ToString();

                    if (maLink)
                    {
                        row.ExistingLinks = istniejace
                            .Where(k => k.Id.HasValue)
                            .Select(k => new BackfillExistingLink { CommentId = k.Id.Value, ViewerUrl = k.ViewerUrl, PureLink = k.PureLink })
                            .ToList();
                    }

                    rows.Add(row);
                }
            }

            _logger.LogInformation("[BACKFILL ENUMERATE] {Database}: produkt={Produkt}; rok={Year}; wierszy={Rows}; z linkiem={Linked}; pominiętych z linkiem={Skipped}; includeLinked={IncludeLinked}",
                client.DatabaseName, produkt, year, rows.Count, rows.Count(r => r.ExistingLinks?.Count > 0), skippedAlreadyLinked, includeLinked);
            return rows;
        }

        // Ten sam filtr roku co w BuildRow - żeby licznik pominiętych obejmował tylko zapisy z backfillowanego roku.
        private static bool CzyZRoku(object entity, int year)
        {
            int? recordYear = ResolveYear(entity);
            return !recordYear.HasValue || recordYear.Value == year;
        }

        private IEnumerable<object> ReadRecordsFromYear(object manager, string entityType, int year, string dbLabel)
        {
            try
            {
                List<object> zRoku = SferaZapisyQueries.ZapisyZRoku(manager, entityType, year);
                if (zRoku != null)
                {
                    return zRoku;
                }

                _logger.LogWarning("[BACKFILL ENUMERATE] {Database}/{EntityType}: brak typowanego zapytania - czytam całą tabelę.", dbLabel, entityType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BACKFILL ENUMERATE] {Database}/{EntityType}: zapytanie z filtrem roku nie powiodło się - czytam całą tabelę.", dbLabel, entityType);
            }

            return SferaZapisyQueries.Wszystkie(manager);
        }

        public sealed class WriteCommentsResult
        {
            public int Written { get; set; }
            public int Replaced { get; set; }
            public int SkippedAlreadyLinked { get; set; }
            public int Failed { get; set; }
            public int Verified { get; set; }
            public int Unverified { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        /// <summary>Zapisuje (albo przy ReplaceCommentIds podmienia) linki dla wszystkich wierszy jednej bazy,
        /// a na końcu sprawdza je odczytem kontrolnym z bazy. <paramref name="onRow"/> dostaje wynik każdego
        /// wiersza zaraz po jego przetworzeniu (proces potomny wypisuje go na stdout).</summary>
        public WriteCommentsResult WriteCommentsForDatabase(Uchwyt sfera, List<BackfillCommentRow> rows, Action<BackfillWriteCommentsRowResult> onRow = null)
        {
            var result = new WriteCommentsResult();
            string dbLabel = rows.FirstOrDefault()?.DatabaseName ?? "?";
            var attachmentService = new AttachmentService(sfera, _loggerFactory.CreateLogger<AttachmentService>());

            object komentarzeManager = SferaReflectionHelpers.GetManagerByInterfaceName(sfera, "IKomentarzeNexo");
            var doWeryfikacji = new List<(BackfillCommentRow Row, int CommentId)>();

            for (int i = 0; i < rows.Count; i++)
            {
                BackfillCommentRow row = rows[i];
                string etykieta = $"{dbLabel}/{row.EntityType}#{row.RachmistrzId} (numer={row.NumerDokumentu})";
                var wynikWiersza = new BackfillWriteCommentsRowResult { Index = i, Status = "failed" };

                try
                {
                    if (komentarzeManager == null)
                    {
                        wynikWiersza.Error = "brak IKomentarzeNexo w tej bazie.";
                    }
                    else if (!InterfaceByEntityType.ContainsKey(row.EntityType ?? ""))
                    {
                        wynikWiersza.Error = "nieznany typ encji.";
                    }
                    else if (string.IsNullOrWhiteSpace(row.ViewerUrl))
                    {
                        wynikWiersza.Error = "brak ViewerUrl.";
                    }
                    else if (row.ReplaceCommentIds?.Count > 0)
                    {
                        PodmienLinki(attachmentService, komentarzeManager, row, wynikWiersza, doWeryfikacji);
                    }
                    else
                    {
                        ZapiszLink(attachmentService, komentarzeManager, sfera, row, wynikWiersza, doWeryfikacji);
                    }
                }
                catch (Exception ex)
                {
                    wynikWiersza.Status = "failed";
                    wynikWiersza.Error = $"wyjątek: {ex.GetBaseException().Message}";
                }

                switch (wynikWiersza.Status)
                {
                    case "written": result.Written++; break;
                    case "replaced": result.Replaced++; break;
                    case "alreadyLinked": result.SkippedAlreadyLinked++; break;
                    default:
                        result.Failed++;
                        result.Errors.Add($"{etykieta}: {wynikWiersza.Error}");
                        break;
                }

                if (wynikWiersza.Status != "failed" && !wynikWiersza.CommentId.HasValue)
                {
                    result.Unverified++;
                    result.Errors.Add($"{etykieta}: zapis przyjęty, ale bez Id komentarza - nie da się go zweryfikować.");
                }

                onRow?.Invoke(wynikWiersza);
            }

            ZweryfikujZapisane(komentarzeManager, doWeryfikacji, result, dbLabel);
            _logger.LogInformation("[BACKFILL KOMENTARZE] {Database}: wierszy={Rows}; zapisane={Written}; podmienione={Replaced}; pominięte={Skipped}; błędy={Failed}; zweryfikowane={Verified}; niezweryfikowane={Unverified}",
                dbLabel, rows.Count, result.Written, result.Replaced, result.SkippedAlreadyLinked, result.Failed, result.Verified, result.Unverified);
            return result;
        }

        // Nowy komentarz - ta sama ścieżka co import (AttachmentService.NapiszKomentarzDlaCelu: odszukanie zapisu po Id,
        // idempotencja po URL, ponowienie przy zamkniętym kontekście).
        private void ZapiszLink(
            AttachmentService attachmentService,
            object komentarzeManager,
            Uchwyt sfera,
            BackfillCommentRow row,
            BackfillWriteCommentsRowResult wynikWiersza,
            List<(BackfillCommentRow Row, int CommentId)> doWeryfikacji)
        {
            var cel = new AttachmentService.AttachmentTargetRef
            {
                ManagerKey = row.EntityType,
                EntityId = row.RachmistrzId,
                DocumentId = row.RachmistrzId
            };

            var (htmlLink, plainText) = AttachmentService.ZbudujTresciLinku(row.ViewerUrl);
            bool ok = attachmentService.NapiszKomentarzDlaCelu(
                komentarzeManager, sfera, htmlLink, plainText, cel,
                "backfill", savedByFreshSession: false,
                out AttachmentService.AttachmentSaveResult zapis, out string blad,
                viewerUrl: row.ViewerUrl);

            if (!ok)
            {
                wynikWiersza.Error = blad;
                return;
            }

            wynikWiersza.Status = zapis?.AlreadyLinked == true ? "alreadyLinked" : "written";
            wynikWiersza.CommentId = zapis?.AttachmentId;
            if (wynikWiersza.CommentId.HasValue)
            {
                doWeryfikacji.Add((row, wynikWiersza.CommentId.Value));
            }
        }

        // Naprawa: w każdym wskazanym komentarzu zły URL zastępujemy ViewerUrl (bez usuwania komentarza).
        private void PodmienLinki(
            AttachmentService attachmentService,
            object komentarzeManager,
            BackfillCommentRow row,
            BackfillWriteCommentsRowResult wynikWiersza,
            List<(BackfillCommentRow Row, int CommentId)> doWeryfikacji)
        {
            var bledy = new List<string>();
            foreach (int commentId in row.ReplaceCommentIds.Distinct())
            {
                if (PodmienLinkWKomentarzu(attachmentService, komentarzeManager, commentId, row, out string blad))
                {
                    doWeryfikacji.Add((row, commentId));
                    wynikWiersza.CommentId ??= commentId;
                }
                else
                {
                    bledy.Add($"komentarz #{commentId}: {blad}");
                }
            }

            if (bledy.Count == 0)
            {
                wynikWiersza.Status = "replaced";
                return;
            }

            wynikWiersza.Error = $"podmiana nieudana ({bledy.Count}/{row.ReplaceCommentIds.Distinct().Count()}): {string.Join("; ", bledy)}";
        }

        // Wzorzec sprawdzony w NexoBridgeKonsola --comment-relink-test (WARSZTATOWNIA): FindById -> IKomentarzeNexo.Znajdz(encja)
        // -> nowa Tresc/ZserializowanaTresc -> Zapisz. BO celowo bez Dispose (patrz AttachmentService.NapiszKomentarzDlaCelu).
        // Podmieniamy tylko "czysty" komentarz z linkiem, wiszący na zapisie z wiersza.
        private bool PodmienLinkWKomentarzu(AttachmentService attachmentService, object komentarzeManager, int commentId, BackfillCommentRow row, out string blad)
        {
            blad = null;
            NexoCommentReader.CommentInfo stan = NexoCommentReader.ReadFromDatabase(komentarzeManager, new[] { commentId }, out string bladOdczytu)?.FirstOrDefault();
            if (stan == null)
            {
                blad = bladOdczytu ?? "komentarza nie ma w bazie";
                return false;
            }

            if (stan.Deleted)
            {
                blad = "komentarz jest usunięty";
                return false;
            }

            int? zapisKomentarza = stan.LinkedEntityId(row.EntityType);
            if (zapisKomentarza != row.RachmistrzId)
            {
                blad = $"komentarz wisi na innym zapisie (Id={zapisKomentarza?.ToString() ?? "brak"})";
                return false;
            }

            if (stan.ContainsUrl(row.ViewerUrl))
            {
                // Ponowienie po wcześniejszej, udanej podmianie.
                return true;
            }

            if (!stan.PureLink)
            {
                blad = "komentarz zawiera coś więcej niż sam link - wymaga ręcznej poprawki";
                return false;
            }

            object komentarzBO = null;
            try
            {
                object dane = SferaReflectionHelpers.TryReadPropertyPath(komentarzeManager, "Dane", out object d) ? d : null;
                object encja = dane?.GetType().GetMethod("FindById", new[] { typeof(int) })?.Invoke(dane, new object[] { commentId });
                if (encja == null)
                {
                    blad = $"FindById({commentId}) zwrócił null";
                    return false;
                }

                MethodInfo znajdz = komentarzeManager.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "Znajdz" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(encja));
                komentarzBO = znajdz?.Invoke(komentarzeManager, new[] { encja });
                if (komentarzBO == null)
                {
                    blad = "IKomentarzeNexo.Znajdz(encja) nie zwrócił obiektu biznesowego";
                    return false;
                }

                object daneBO = SferaReflectionHelpers.TryReadPropertyPath(komentarzBO, "Dane", out object db) ? db : null;
                var (htmlLink, plainText) = AttachmentService.ZbudujTresciLinku(row.ViewerUrl);
                if (!SferaReflectionHelpers.TrySetPropertyPath(daneBO, "Tresc", plainText)
                    || !SferaReflectionHelpers.TrySetPropertyPath(daneBO, "ZserializowanaTresc", htmlLink))
                {
                    blad = "nie udało się ustawić treści komentarza";
                    return false;
                }

                bool zapisano = ((dynamic)komentarzBO).Zapisz();
                if (!zapisano)
                {
                    blad = $"Zapisz=false; invalidData={attachmentService.WyciagnijBledySfery(komentarzBO)}";
                }

                return zapisano;
            }
            catch (Exception ex)
            {
                blad = $"wyjątek: {ex.GetBaseException().Message}; invalidData={attachmentService.WyciagnijBledySfery(komentarzBO)}";
                return false;
            }
        }

        // Odczyt kontrolny projekcją (wartości prosto z bazy, nie z kontekstu sesji): komentarz istnieje, nie jest
        // usunięty, wisi na zapisie z wiersza i zawiera link. Drugiej sesji Sfery w procesie nie otwieramy
        // (ponowne Uruchom() psuje stan statyczny - patrz PayrollCountsService).
        private void ZweryfikujZapisane(object komentarzeManager, List<(BackfillCommentRow Row, int CommentId)> zapisane, WriteCommentsResult result, string dbLabel)
        {
            if (zapisane.Count == 0)
            {
                return;
            }

            List<NexoCommentReader.CommentInfo> komentarze = NexoCommentReader.ReadFromDatabase(komentarzeManager, zapisane.Select(z => z.CommentId), out string blad);
            if (komentarze == null)
            {
                result.Unverified += zapisane.Count;
                result.Errors.Add($"{dbLabel}: odczyt kontrolny niemożliwy ({blad}) - {zapisane.Count} komentarzy niezweryfikowanych.");
                return;
            }

            var poId = komentarze.Where(k => k.Id.HasValue).GroupBy(k => k.Id.Value).ToDictionary(g => g.Key, g => g.First());
            foreach (var (row, commentId) in zapisane)
            {
                string problem;
                if (!poId.TryGetValue(commentId, out NexoCommentReader.CommentInfo komentarz))
                {
                    problem = "komentarza nie ma w bazie";
                }
                else if (komentarz.Deleted)
                {
                    problem = "komentarz jest usunięty";
                }
                else if (komentarz.LinkedEntityId(row.EntityType) != row.RachmistrzId)
                {
                    problem = $"komentarz wisi na zapisie Id={komentarz.LinkedEntityId(row.EntityType)?.ToString() ?? "brak"}";
                }
                else if (!komentarz.ContainsUrl(row.ViewerUrl))
                {
                    problem = "komentarz nie zawiera oczekiwanego linku";
                }
                else
                {
                    result.Verified++;
                    continue;
                }

                result.Unverified++;
                result.Errors.Add($"{dbLabel}/{row.EntityType}#{row.RachmistrzId} (numer={row.NumerDokumentu}): weryfikacja komentarza #{commentId}: {problem}.");
            }
        }

        // ================== SPAWNOWANIE PROCESÓW POTOMNYCH (analogiczne do PayrollCountsService) ==================

        private async Task<(List<BackfillManifestRow> Rows, int SkippedAlreadyLinked, string Error)> EnumerateOneClientInChildProcessAsync(
            string username, string password, BackfillClientRef client, ProductId produkt, int year, bool includeLinked, CancellationToken cancellationToken)
        {
            var request = new BackfillEnumerateClientWorkerRequest
            {
                Username = username,
                Password = password,
                ClientNip = client.Nip,
                ClientName = client.ClientName,
                DatabaseName = client.DatabaseName,
                Year = year,
                IncludeLinked = includeLinked,
                Product = produkt.ToString()
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
            string username, string password, string databaseName, ProductId produkt, List<BackfillCommentRow> rows, CancellationToken cancellationToken)
        {
            var request = new BackfillWriteCommentsClientWorkerRequest
            {
                Username = username,
                Password = password,
                DatabaseName = databaseName,
                Product = produkt.ToString(),
                Rows = rows
            };

            (string stdout, string stderr, int exitCode, string spawnError) = await RunChildWorkerAsync(
                "--backfill-write-comments-worker", request, WriteCommentsTimeout(rows.Count), cancellationToken);

            string resultJson = ExtractBetweenMarkers(stdout, global::NexoBridge.Program.BackfillWorkerResultStartMarker, global::NexoBridge.Program.BackfillWorkerResultEndMarker);
            if (resultJson == null)
            {
                // Proces przerwany (limit czasu, awaria) - z wyników wierszy wypisywanych na bieżąco odtwarzamy, co
                // już zapisano, zamiast liczyć całą bazę jako błędną.
                string przyczyna = spawnError
                    ?? (!string.IsNullOrWhiteSpace(stderr) ? Truncate(stderr.Trim(), 500) : $"kod wyjścia {exitCode}, brak wyniku");
                WriteCommentsResult czesciowy = OdtworzZWynikowWierszy(stdout, rows, databaseName, przyczyna);
                if (czesciowy != null)
                {
                    _logger.LogWarning("Proces zapisujący komentarze dla bazy {Database} przerwany ({Przyczyna}) - odtworzono wyniki {Count}/{Total} wierszy.",
                        databaseName, przyczyna, czesciowy.Written + czesciowy.Replaced + czesciowy.SkippedAlreadyLinked, rows.Count);
                    return (czesciowy, null);
                }

                if (spawnError != null)
                {
                    return (null, spawnError);
                }

                _logger.LogWarning(
                    "Proces zapisujący komentarze dla bazy {Database}: brak wyniku (kod {ExitCode}). stdout={Stdout} stderr={Stderr}",
                    databaseName, exitCode, Truncate(stdout, 2000), Truncate(stderr, 2000));
                return (null, !string.IsNullOrWhiteSpace(stderr)
                    ? stderr.Trim()
                    : $"Proces potomny zakończył się kodem {exitCode} bez rozpoznawalnego wyniku.");
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
                Replaced = response.Replaced,
                SkippedAlreadyLinked = response.SkippedAlreadyLinked,
                Failed = response.Failed,
                Verified = response.Verified,
                Unverified = response.Unverified,
                Errors = response.Errors ?? new List<string>()
            }, null);
        }

        // Wyniki wierszy z linii BackfillWorkerRowMarker. Wiersze bez wyniku = nieprzetworzone (błąd; ponowne
        // uruchomienie je dopisze), a zapisane przed przerwaniem nie przeszły odczytu kontrolnego.
        private static WriteCommentsResult OdtworzZWynikowWierszy(string stdout, List<BackfillCommentRow> rows, string databaseName, string przyczyna)
        {
            var wyniki = new Dictionary<int, BackfillWriteCommentsRowResult>();
            foreach (string linia in (stdout ?? string.Empty).Split('\n'))
            {
                string tresc = linia.Trim();
                if (!tresc.StartsWith(global::NexoBridge.Program.BackfillWorkerRowMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var wynik = JsonSerializer.Deserialize<BackfillWriteCommentsRowResult>(
                        tresc.Substring(global::NexoBridge.Program.BackfillWorkerRowMarker.Length),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (wynik != null && wynik.Index >= 0 && wynik.Index < rows.Count)
                    {
                        wyniki[wynik.Index] = wynik;
                    }
                }
                catch (JsonException)
                {
                    // Ucięta ostatnia linia przy zabiciu procesu - pomijamy.
                }
            }

            if (wyniki.Count == 0)
            {
                return null;
            }

            var result = new WriteCommentsResult();
            for (int i = 0; i < rows.Count; i++)
            {
                BackfillCommentRow row = rows[i];
                if (!wyniki.TryGetValue(i, out BackfillWriteCommentsRowResult wynik))
                {
                    result.Failed++;
                    continue;
                }

                switch (wynik.Status)
                {
                    case "written": result.Written++; result.Unverified++; break;
                    case "replaced": result.Replaced++; result.Unverified++; break;
                    case "alreadyLinked": result.SkippedAlreadyLinked++; break;
                    default:
                        result.Failed++;
                        result.Errors.Add($"{databaseName}/{row.EntityType}#{row.RachmistrzId} (numer={row.NumerDokumentu}): {wynik.Error}");
                        break;
                }
            }

            result.Errors.Add($"{databaseName}: proces przerwany ({przyczyna}) - przetworzono {wyniki.Count}/{rows.Count} wierszy; " +
                $"{rows.Count - wyniki.Count} nieprzetworzonych liczonych jako błąd (ponowne uruchomienie je dopisze), zapisane nie przeszły odczytu kontrolnego.");
            return result;
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
                            // To, co proces zdążył wypisać (np. wyniki zapisanych już wierszy), nadal się przydaje.
                            return (await DokonczOdczyt(stdoutTask), await DokonczOdczyt(stderrTask), -1,
                                $"Przekroczono czas oczekiwania na osobny proces ({timeout.TotalMinutes:0} min).");
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

        private static async Task<string> DokonczOdczyt(Task<string> odczyt)
        {
            try
            {
                return await Task.WhenAny(odczyt, Task.Delay(TimeSpan.FromSeconds(10))) == odczyt ? await odczyt : null;
            }
            catch
            {
                return null;
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

        // Bez PDF-u z biblioteki załączników: plik do podglądu Klasyfikator bierze wyłącznie ze Scanye (PDF z
        // Rachmistrza zapisany pod UUID dopasowanej faktury ukrywał błędne dopasowania i ważył cały raport).
        private static BackfillManifestRow BuildRow(BackfillClientRef client, string entityType, object entity, int year)
        {
            int? recordYear = ResolveYear(entity);
            if (recordYear.HasValue && recordYear.Value != year)
            {
                return null;
            }

            List<string> kandydaci = ZbudujKandydatowNumeru(entity, entityType);
            string numer = kandydaci.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(numer))
            {
                return null;
            }

            return new BackfillManifestRow
            {
                ClientNip = client.Nip,
                DatabaseName = client.DatabaseName,
                EntityType = entityType,
                RachmistrzId = SferaReflectionHelpers.ReadIntCandidate(entity, "Id") ?? 0,
                VendorNip = SferaReflectionHelpers.ReadStringCandidate(entity, NipPaths),
                NumerDokumentu = numer,
                NumerKandydaci = kandydaci,
                RecordYear = recordYear
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
            // Własny numer zapisu: VAT - NumerDokumentu, dekret Rewizora - NumerWlasny (numer faktury; Dowod to
            // sygnatura rejestru typu "RKK-521"), KPiR/EP - NumerDowoduKsiegowego.
            string polaNumeru = entityType?.ToUpperInvariant() switch
            {
                "VAT" => "NumerDokumentu",
                "DK" => "NumerWlasny",
                _ => "NumerDowoduKsiegowego"
            };
            surowe.Add(SferaReflectionHelpers.ReadStringCandidate(entity, polaNumeru));

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
    }
}
