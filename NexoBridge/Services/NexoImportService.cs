using InsERT.Moria.ModelDanych;
using Microsoft.Extensions.Logging;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class NexoImportService
    {
        private readonly EppParserService _parserService;
        private readonly ImportManifestService _manifestService;
        private readonly AmortizationService _amortizationService;
        private readonly AccountingService _accountingService;
        private readonly PitCalculationService _pitService;
        private readonly VatCalculationService _vatService;
        private readonly AttachmentService _attachmentService;
        private readonly KsefNumberAssignmentService _ksefNumberAssignmentService;
        private readonly ILogger<NexoImportService> _logger;

        public NexoImportService(
            EppParserService parserService,
            ImportManifestService manifestService,
            AmortizationService amortizationService,
            AccountingService accountingService,
            PitCalculationService pitCalculationService,
            VatCalculationService vatCalculationService,
            AttachmentService attachmentService,
            KsefNumberAssignmentService ksefNumberAssignmentService,
            ILogger<NexoImportService> logger)
        {
            _parserService = parserService;
            _manifestService = manifestService;
            _amortizationService = amortizationService;
            _accountingService = accountingService;
            _pitService = pitCalculationService;
            _vatService = vatCalculationService;
            _attachmentService = attachmentService;
            _ksefNumberAssignmentService = ksefNumberAssignmentService;
            _logger = logger;
        }

        public Task<TaxSummaryReport> PrzetworzZadanieAsync(ImportJob job, Func<int, string, Task> raportujPostep)
        {
            var progress = new ProgressTracker(raportujPostep, JobProgressPlan.CalculateTotalUnits(job));
            return PrzetworzZadanieAsync(job, progress);
        }

        public async Task<TaxSummaryReport> PrzetworzZadanieAsync(ImportJob job, ProgressTracker progress)
        {
            var finalReport = new TaxSummaryReport
            {
                JobId = job.JobId,
                Status = "SUCCESS",
                Message = "Proces zakończony."
            };

            DateTime dataRozliczenia = new DateTime(job.BillingYear, job.BillingMonth, 1);
            int zatwierdzoneCount = 0;
            bool maImportFaktur = JobProgressPlan.HasInvoiceImport(job);
            bool amortyzacjaWygenerowalaDokumenty = false;

            _logger.LogInformation("Rozpoczynam zintegrowane przetwarzanie zadania: {JobId}. Baza docelowa: {Database} za {Miesiac}/{Rok}. Flagi: ImportInvoices={ImportInvoices}, Files={Files}, CalculateAmortization={CalculateAmortization}, CalculatePit={CalculatePit}, CalculateVat={CalculateVat}",
                job.JobId,
                job.DatabaseName,
                job.BillingMonth,
                job.BillingYear,
                job.ImportInvoices,
                job.Files?.Count ?? 0,
                job.CalculateAmortization,
                job.CalculatePit,
                job.CalculateVat);

            try
            {
                await progress.AdvanceAsync(JobProgressPlan.ManifestUnits, "Budowanie manifestu dokumentów...");
                finalReport.Documents = _manifestService.ZbudujManifest(job);

                // =========================================================
                // ETAP 1: OBSŁUGA FAKTUR
                // =========================================================
                var importProgress = progress.BeginSegment(maImportFaktur ? JobProgressPlan.ImportInvoicesUnits : JobProgressPlan.SkipImportUnits);
                if (maImportFaktur)
                {
                    await importProgress.ReportAsync(5, "Pobieranie i analiza plików EPP...");
                    await _parserService.ParseAndSyncAsync(job, importProgress.ReportAsync);

                    var oczekujacePoImporcie = _manifestService.PobierzDokumentyWPoczekalni(dataRozliczenia);
                    _manifestService.AktualizujPoPoczekalni(finalReport.Documents, oczekujacePoImporcie);
                    await _ksefNumberAssignmentService.PrzypiszPrzedDekretacjaAsync(job, finalReport.Documents, importProgress.ReportAsync);
                    await importProgress.CompleteAsync("Import faktur i audyt KSeF w Poczekalni zakończone.");
                }
                else
                {
                    _logger.LogInformation("Pomijam moduł importu faktur (ImportInvoices={ImportInvoices}, Files={Files}).", job.ImportInvoices, job.Files?.Count ?? 0);
                    await importProgress.CompleteAsync("Tryb bez faktur. Pomijam pobieranie EPP...");
                }

                // =========================================================
                // ETAP 2: AMORTYZACJA
                // =========================================================
                var amortizationProgress = progress.BeginSegment(job.CalculateAmortization ? JobProgressPlan.AmortizationUnits : JobProgressPlan.SkipAmortizationUnits);
                if (job.CalculateAmortization)
                {
                    await amortizationProgress.ReportAsync(10, "Sprawdzanie środków trwałych i naliczanie amortyzacji...");
                    finalReport.Amortization = await _amortizationService.ObliczAmortyzacjeAsync(dataRozliczenia);
                    amortyzacjaWygenerowalaDokumenty = finalReport.Amortization?.DocumentsGenerated > 0;
                    if (!amortyzacjaWygenerowalaDokumenty)
                    {
                        _logger.LogInformation("Amortyzacja nie wygenerowała nowych dokumentów. Pomijam dekretację amortyzacji (DocumentsGenerated={DocumentsGenerated}).", finalReport.Amortization?.DocumentsGenerated ?? 0);
                    }
                    await amortizationProgress.CompleteAsync("Naliczanie amortyzacji zakończone.");
                }
                else
                {
                    _logger.LogInformation("Pomijam naliczanie amortyzacji (flaga z Frontendu).");
                    await amortizationProgress.CompleteAsync("Pomijam naliczanie amortyzacji.");
                }

                // =========================================================
                // ETAP 3: DEKRETACJA WŁAŚCIWA (cała Poczekalnia)
                // =========================================================
                bool wymagaDekretacji = maImportFaktur || amortyzacjaWygenerowalaDokumenty;
                var decreeProgress = progress.BeginSegment((maImportFaktur || job.CalculateAmortization) ? JobProgressPlan.DecreeUnits : JobProgressPlan.SkipDecreeUnits);
                if (wymagaDekretacji)
                {
                    var oczekujacePrzedDekretacja = _manifestService.PobierzDokumentyWPoczekalni(dataRozliczenia);
                    _manifestService.AktualizujPoPoczekalni(finalReport.Documents, oczekujacePrzedDekretacja);

                    await decreeProgress.ReportAsync(5, "Dekretacja dokumentów...");
                    var (rezultat, zatwierdzone, oczekujace, brakSchematu, bledneSchematy) = await _accountingService.DekretujAsync(dataRozliczenia, decreeProgress.ReportAsync);
                    zatwierdzoneCount = zatwierdzone.Count;
                    AktualizujStatusyDekretacji(finalReport.Documents, rezultat, zatwierdzone, brakSchematu, bledneSchematy);
                    await decreeProgress.CompleteAsync($"Dekretacja zakończona. Zadekretowano {zatwierdzoneCount} dok.");

                    // =========================================================
                    // ETAP 4: ZAŁĄCZNIKI i KSeF (nie blokują procesu)
                    // =========================================================
                    if (maImportFaktur)
                    {
                        var attachmentsProgress = progress.BeginSegment(JobProgressPlan.AttachmentsAndKsefUnits);
                        if (zatwierdzoneCount > 0)
                        {
                            Func<int, string, Task> attachmentsReporter = attachmentsProgress.ReportAsync;
                            await attachmentsProgress.ReportAsync(5, "Podpinanie załączników PDF...");
                            await _attachmentService.PodepnijZalacznikiAsync(job, rezultat, zatwierdzone, finalReport.Documents, attachmentsReporter);
                            await _ksefNumberAssignmentService.ZweryfikujPoDekretacjiAsync(job, rezultat, zatwierdzone, finalReport.Documents, attachmentsReporter);
                            await attachmentsProgress.CompleteAsync("Obsługa załączników i weryfikacja KSeF zakończona.");
                        }
                        else
                        {
                            await attachmentsProgress.CompleteAsync("Brak zadekretowanych dokumentów. Pomijam załączniki i weryfikację KSeF.");
                        }
                    }

                    if (zatwierdzoneCount == 0 && maImportFaktur)
                    {
                        _logger.LogInformation("Brak nowych dokumentów do zaksięgowania z dostarczonego EPP lub Poczekalni.");
                        finalReport.Message = "Brak dokumentów do zaksięgowania. Szczegóły są dostępne w raporcie dokumentów.";
                    }
                }
                else
                {
                    await decreeProgress.CompleteAsync("Brak operacji wymagających dekretacji. Przechodzę do podatków...");
                }

                // =========================================================
                // ETAP 5: WYLICZENIE PODATKÓW PIT i VAT
                // =========================================================
                var pitProgress = progress.BeginSegment(job.CalculatePit ? JobProgressPlan.PitUnits : JobProgressPlan.SkipPitUnits);
                if (job.CalculatePit)
                {
                    await pitProgress.ReportAsync(10, "Wyliczanie zaliczek na podatek PIT...");
                    finalReport.PitTaxes = await _pitService.WyliczZaliczkiWspolnikowAsync(dataRozliczenia);
                    await pitProgress.CompleteAsync("Wyliczanie PIT zakończone.");

                    if (finalReport.PitTaxes.Any(p => !string.IsNullOrEmpty(p.CriticalError)))
                    {
                        finalReport.Status = "FAILED";
                        finalReport.Message += " Proces natrafił na błędy podczas wyliczania PIT.";
                        _logger.LogWarning("Błędy PIT: {Msg}", finalReport.Message);
                    }
                }
                else
                {
                    _logger.LogInformation("Pomijam wyliczanie PIT (flaga z Frontendu).");
                    await pitProgress.CompleteAsync("Pomijam wyliczanie PIT.");
                }

                var vatProgress = progress.BeginSegment(job.CalculateVat ? JobProgressPlan.VatUnits : JobProgressPlan.SkipVatUnits);
                if (job.CalculateVat)
                {
                    await vatProgress.ReportAsync(10, "Generowanie JPK_V7...");
                    finalReport.VatTax = await _vatService.WygenerujJpkVatAsync(dataRozliczenia);
                    await vatProgress.CompleteAsync("Generowanie JPK_V7 zakończone.");

                    if (!string.IsNullOrEmpty(finalReport.VatTax?.ErrorMsg))
                    {
                        finalReport.Status = "FAILED";
                        finalReport.Message += " Wystąpiły błędy podczas generowania JPK.";
                    }
                }
                else
                {
                    _logger.LogInformation("Pomijam wyliczanie VAT (flaga z Frontendu).");
                    await vatProgress.CompleteAsync("Pomijam wyliczanie VAT.");
                }

                // =========================================================
                // ETAP 6: ZAKOŃCZENIE I RAPORTOWANIE
                // =========================================================
                if (finalReport.Status == "SUCCESS" && CzySaOstrzezeniaDokumentow(finalReport))
                {
                    finalReport.Status = "PARTIAL_SUCCESS";
                    finalReport.Message += " Część dokumentów wymaga uwagi - szczegóły w raporcie dokumentów.";
                }

                if (finalReport.Status == "SUCCESS" && !string.IsNullOrEmpty(finalReport.Amortization?.Warning))
                {
                    finalReport.Status = "PARTIAL_SUCCESS";
                }

                string summaryJson = JsonSerializer.Serialize(finalReport, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation("Zadanie {JobId} zakończone. Status: {Status}. Dokumenty={DocumentsCount}; ostrzeżeniaDokumentów={WarningCount}",
                    job.JobId,
                    finalReport.Status,
                    finalReport.Documents?.Count ?? 0,
                    finalReport.Documents?.Count(d => d.Warnings != null && d.Warnings.Count > 0) ?? 0);

                string raportKoncowy = wymagaDekretacji
                    ? $"[ZAKOŃCZONO] Status: {finalReport.Status}. Zadekretowano {zatwierdzoneCount} dok."
                    : $"[ZAKOŃCZONO] Status: {finalReport.Status}. Zakończono kalkulacje podatkowe.";

                await progress.CompleteAsync(raportKoncowy);
            }
            catch (Exception ex)
            {
                finalReport.Status = "FAILED";
                finalReport.Message = $"Błąd krytyczny procesu: {ex.Message}";
                _logger.LogError(ex, "Przerwano procesowanie zadania {JobId}", job.JobId);
                await progress.CompleteAsync($"[BŁĄD KRYTYCZNY] {ex.Message}");
            }

            return finalReport;
        }

        private void AktualizujStatusyDekretacji(
            List<DocumentProcessingReport> manifest,
            dynamic rezultat,
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> zatwierdzone,
            List<DokumentDoKsiegowania> brakSchematu,
            List<DokumentDoKsiegowania> bledneSchematy)
        {
            foreach (var doc in brakSchematu ?? new List<DokumentDoKsiegowania>())
            {
                var raport = ImportManifestService.ZnajdzRaportDlaDokumentu(manifest, doc);
                if (raport == null) continue;
                raport.DecreeStatus = "noSchema";
                ImportManifestService.DodajWarning(raport, "Dokument nie został zadekretowany, bo nie spełnił warunków żadnego schematu dekretacji.");
            }

            foreach (var doc in bledneSchematy ?? new List<DokumentDoKsiegowania>())
            {
                var raport = ImportManifestService.ZnajdzRaportDlaDokumentu(manifest, doc);
                if (raport == null) continue;
                raport.DecreeStatus = "schemaError";
                ImportManifestService.DodajWarning(raport, "Dokument nie został zadekretowany z powodu błędu krytycznego w schemacie albo danych dokumentu.");
            }

            var listaWynikow = PobierzWynikiOperacji(rezultat);
            for (int i = 0; i < (zatwierdzone?.Count ?? 0); i++)
            {
                var dokument = zatwierdzone[i].Item1;
                var schemat = zatwierdzone[i].Item2;
                var raport = ImportManifestService.ZnajdzRaportDlaDokumentu(manifest, dokument);
                if (raport == null) continue;

                raport.DecreeSchema = schemat?.Nazwa;
                if (i >= listaWynikow.Count)
                {
                    raport.DecreeStatus = "resultMissing";
                    ImportManifestService.DodajWarning(raport, "Dokument miał przypisany schemat, ale operacja dekretacji nie zwróciła odpowiadającego wyniku.");
                    _logger.LogWarning("[DEKRETACJA BRAK WYNIKU] Dokument={Numer}; NIP={Nip}; schemat={Schemat}; index={Index}",
                        dokument.NumerDokumentu,
                        dokument.PodmiotHistoria?.NIP,
                        schemat?.Nazwa,
                        i);
                    continue;
                }

                var wynikowe = PobierzWynikoweZapisy(listaWynikow[i]);
                if (wynikowe.Count == 0)
                {
                    raport.DecreeStatus = "noResultEntries";
                    ImportManifestService.DodajWarning(raport, "Operacja dekretacji nie zwróciła wynikowych zapisów księgowych dla dokumentu.");
                    _logger.LogWarning("[DEKRETACJA BEZ ZAPISÓW] Dokument={Numer}; NIP={Nip}; schemat={Schemat}",
                        dokument.NumerDokumentu,
                        dokument.PodmiotHistoria?.NIP,
                        schemat?.Nazwa);
                    continue;
                }

                raport.DecreeStatus = "decreed";
                var resultEntries = new List<DocumentResultEntry>();
                foreach (var wynik in wynikowe)
                {
                    resultEntries.Add(new DocumentResultEntry
                    {
                        ResultType = wynik?.GetType().Name,
                        DocumentId = PobierzDokumentId(wynik)
                    });
                }
                raport.ResultEntries = resultEntries;

                _logger.LogInformation("[SUKCES DEKRETACJI] Dokument={Numer}; NIP={Nip}; Schemat={Schemat}; wyniki={Wyniki}",
                    dokument.NumerDokumentu,
                    dokument.PodmiotHistoria?.NIP,
                    schemat?.Nazwa,
                    string.Join("; ", raport.ResultEntries.Select(e => $"typ={e.ResultType}, dokumentId={e.DocumentId}")));
            }
        }

        private List<dynamic> PobierzWynikiOperacji(dynamic rezultat)
        {
            if (rezultat == null) return new List<dynamic>();
            try { return ((System.Collections.IEnumerable)rezultat).Cast<dynamic>().ToList(); }
            catch { return new List<dynamic>(); }
        }

        private List<dynamic> PobierzWynikoweZapisy(dynamic wynikOperacji)
        {
            try
            {
                var wynikowe = (System.Collections.IEnumerable)wynikOperacji.WynikowePoprawneZapisy;
                return wynikowe?.Cast<dynamic>().ToList() ?? new List<dynamic>();
            }
            catch
            {
                return new List<dynamic>();
            }
        }

        private int? PobierzDokumentId(dynamic wynik)
        {
            try
            {
                object id = wynik?.DokumentId;
                if (id == null) return null;
                return Convert.ToInt32(id);
            }
            catch
            {
                return null;
            }
        }

        private bool CzySaOstrzezeniaDokumentow(TaxSummaryReport report)
        {
            return report.Documents != null && report.Documents.Any(d =>
                (d.Warnings != null && d.Warnings.Count > 0) ||
                d.WaitingRoomStatus == "notFound" ||
                d.WaitingRoomStatus == "ambiguous" ||
                d.KsefStatus == "notFoundInWaitingRoom" ||
                d.KsefStatus == "notConfirmedInWaitingRoom" ||
                d.KsefStatus == "differentInWaitingRoom" ||
                d.KsefStatus == "notConfirmedAfterDecree" ||
                d.AttachmentStatus == "notFound" ||
                d.AttachmentStatus == "ambiguous" ||
                d.AttachmentStatus == "notAttached" ||
                d.DecreeStatus == "noSchema" ||
                d.DecreeStatus == "schemaError" ||
                d.DecreeStatus == "resultMissing" ||
                d.DecreeStatus == "noResultEntries");
        }
    }
}

