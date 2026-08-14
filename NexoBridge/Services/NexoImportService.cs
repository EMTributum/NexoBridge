using InsERT.Moria.ModelDanych;
using Microsoft.Extensions.Logging;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using System;
using System.Collections;
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
        private readonly VatUeCalculationService _vatUeService;
        private readonly AttachmentService _attachmentService;
        private readonly KsefNumberAssignmentService _ksefNumberAssignmentService;
        private readonly InvoiceDuplicateDetectionService _duplicateDetectionService;
        private readonly VatStatusVerificationService _vatStatusVerificationService;
        private readonly ILogger<NexoImportService> _logger;

        public NexoImportService(
            EppParserService parserService,
            ImportManifestService manifestService,
            AmortizationService amortizationService,
            AccountingService accountingService,
            PitCalculationService pitCalculationService,
            VatCalculationService vatCalculationService,
            VatUeCalculationService vatUeCalculationService,
            AttachmentService attachmentService,
            KsefNumberAssignmentService ksefNumberAssignmentService,
            InvoiceDuplicateDetectionService duplicateDetectionService,
            VatStatusVerificationService vatStatusVerificationService,
            ILogger<NexoImportService> logger)
        {
            _parserService = parserService;
            _manifestService = manifestService;
            _amortizationService = amortizationService;
            _accountingService = accountingService;
            _pitService = pitCalculationService;
            _vatService = vatCalculationService;
            _vatUeService = vatUeCalculationService;
            _attachmentService = attachmentService;
            _ksefNumberAssignmentService = ksefNumberAssignmentService;
            _duplicateDetectionService = duplicateDetectionService;
            _vatStatusVerificationService = vatStatusVerificationService;
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
            EppImportResult importResult = null;
            ImportPackageContext packageContext = null;
            object rezultatDekretacji = null;
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> zatwierdzoneDekretacji = new List<Tuple<DokumentDoKsiegowania, SchematImportu>>();

            _logger.LogInformation("Rozpoczynam zintegrowane przetwarzanie zadania: {JobId}. Baza docelowa: {Database} za {Miesiac}/{Rok}. Flagi: ImportInvoices={ImportInvoices}, Files={Files}, CalculateAmortization={CalculateAmortization}, CalculatePit={CalculatePit}, CalculateVat={CalculateVat}, CalculateVatUE={CalculateVatUE}",
                job.JobId,
                job.DatabaseName,
                job.BillingMonth,
                job.BillingYear,
                job.ImportInvoices,
                job.Files?.Count ?? 0,
                job.CalculateAmortization,
                job.CalculatePit,
                job.CalculateVat,
                job.CalculateVatUE);

            try
            {
                // Zrzut poczekalni SPRZED importu EPP tego zlecenia - potrzebny wyłącznie do bootstrapu
                // baseline'u (patrz PobierzWyborDokumentowWPoczekalni/DekretujAsync), żeby dokument
                // zaimportowany przez TO zlecenie nie został pomylony ze starym zaległym backlogiem.
                var poolNumeryPrzedZleceniem = _manifestService.PobierzNumeryOczekujaceTeraz();

                await progress.AdvanceAsync(JobProgressPlan.ManifestUnits, "Budowanie manifestu dokumentów...");
                finalReport.Documents = _manifestService.ZbudujManifest(job);

                // =========================================================
                // ETAP 1: OBSŁUGA FAKTUR
                // =========================================================
                var importProgress = progress.BeginSegment(maImportFaktur ? JobProgressPlan.ImportInvoicesUnits : JobProgressPlan.SkipImportUnits);
                if (maImportFaktur)
                {
                    await importProgress.ReportAsync(5, "Pobieranie i analiza plików EPP...");
                    importResult = await _parserService.ParseAndSyncAsync(job, importProgress.ReportAsync);
                    packageContext = ImportPackageContext.FromJob(job, importResult);
                    _logger.LogInformation("[EPP IMPORT CONTEXT] JobId={JobId}; obiekty={Objects}; naglowki={Headers}; numerySkorygowane={CorrectedNumbers}; ksefDopisane={KsefAssigned}", job.JobId, importResult.ObjectsCount, importResult.Headers.Count, importResult.InvoiceNumberCorrectedCount, importResult.KsefAssignedCount);

                    var wyborPoImporcie = await _manifestService.PobierzWyborDokumentowWPoczekalni(dataRozliczenia, packageContext, job, poolNumeryPrzedZleceniem);
                    _manifestService.AktualizujPoPoczekalni(finalReport.Documents, wyborPoImporcie);
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
                    var wyborPrzedDekretacja = await _manifestService.PobierzWyborDokumentowWPoczekalni(dataRozliczenia, packageContext, job, poolNumeryPrzedZleceniem);
                    _manifestService.AktualizujPoPoczekalni(finalReport.Documents, wyborPrzedDekretacja);

                    await decreeProgress.ReportAsync(5, "Dekretacja dokumentów...");
                    (dynamic Rezultat, List<Tuple<DokumentDoKsiegowania, SchematImportu>> Zatwierdzone, List<DokumentDoKsiegowania> Oczekujace, List<DokumentDoKsiegowania> BrakSchematu, List<DokumentDoKsiegowania> BledneSchematy) wynikDekretacji;
                    try
                    {
                        wynikDekretacji = await _accountingService.DekretujAsync(dataRozliczenia, packageContext, job, poolNumeryPrzedZleceniem, decreeProgress.ReportAsync);
                    }
                    catch (ArgumentNullException ex) when (string.Equals(ex.ParamName, "okresObrachunkowy", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError(ex, "[DEKRETACJA BŁĄD OKRESU] JobId={JobId}; baza={Database}; okresRozliczenia={Okres}; " +
                            "Sfera nie znalazła okresu obrachunkowego dla daty jednego z dokumentów w Poczekalni. " +
                            "Sprawdź, czy w bazie klienta są skonfigurowane okresy obrachunkowe obejmujące datę zdarzenia tego dokumentu " +
                            "(np. dokument z datą z innego roku/miesiąca niż otwarte okresy).",
                            job.JobId,
                            job.DatabaseName,
                            dataRozliczenia);
                        throw;
                    }

                    var rezultat = wynikDekretacji.Rezultat;
                    var zatwierdzone = wynikDekretacji.Zatwierdzone;
                    var oczekujace = wynikDekretacji.Oczekujace;
                    var brakSchematu = wynikDekretacji.BrakSchematu;
                    var bledneSchematy = wynikDekretacji.BledneSchematy;
                    rezultatDekretacji = (object)rezultat;
                    zatwierdzoneDekretacji = zatwierdzone ?? new List<Tuple<DokumentDoKsiegowania, SchematImportu>>();
                    zatwierdzoneCount = zatwierdzoneDekretacji.Count;
                    AktualizujStatusyDekretacji(finalReport.Documents, rezultatDekretacji, zatwierdzoneDekretacji, brakSchematu, bledneSchematy);
                    await decreeProgress.CompleteAsync($"Dekretacja zakończona. Zadekretowano {zatwierdzoneCount} dok.");

                    // =========================================================
                    // ETAP 4: Dane KSeF po dekretacji (przed VAT/JPK)
                    // =========================================================
                    if (maImportFaktur)
                    {
                        var ksefProgress = progress.BeginSegment(JobProgressPlan.KsefPostDecreeUnits);
                        if (zatwierdzoneCount > 0)
                        {
                            try
                            {
                                Func<int, string, Task> ksefReporter = ksefProgress.ReportAsync;
                                await ksefProgress.ReportAsync(10, "Uzupełnianie i weryfikacja danych KSeF...");
                                await _ksefNumberAssignmentService.PrzypiszKodyPoDekretacjiAsync(job, rezultatDekretacji, zatwierdzoneDekretacji, finalReport.Documents, ksefReporter);
                                await _ksefNumberAssignmentService.ZweryfikujPoDekretacjiAsync(job, rezultatDekretacji, zatwierdzoneDekretacji, finalReport.Documents, ksefReporter);
                                await ksefProgress.CompleteAsync("Obsługa danych KSeF zakończona.");
                            }
                            catch (Exception ex)
                            {
                                string warning = $"Nie udało się zakończyć obsługi danych KSeF po dekretacji: {ex.GetBaseException().Message}";
                                OznaczBladEtapuKsef(finalReport.Documents, warning);
                                _logger.LogError(ex, "[KSEF PO DEKRETACJI BŁĄD] JobId={JobId}; baza={Database}; zadekretowane={Count}; {Warning}",
                                    job.JobId,
                                    job.DatabaseName,
                                    zatwierdzoneCount,
                                    warning);
                                await ksefProgress.CompleteAsync("Obsługa danych KSeF zakończona z błędem.");
                            }
                        }
                        else
                        {
                            await ksefProgress.CompleteAsync("Brak zadekretowanych dokumentów. Pomijam dane KSeF po dekretacji.");
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
                    _logger.LogInformation("Pomijam wyliczanie VAT/JPK (flaga z Frontendu).");
                    await vatProgress.CompleteAsync("Pomijam wyliczanie VAT/JPK.");
                }

                var vatUeProgress = progress.BeginSegment(job.CalculateVatUE ? JobProgressPlan.VatUeUnits : JobProgressPlan.SkipVatUeUnits);
                if (job.CalculateVatUE)
                {
                    await vatUeProgress.ReportAsync(10, "Generowanie VAT-UE...");
                    finalReport.VatUeTax = await _vatUeService.WygenerujVatUeAsync(dataRozliczenia);
                    if (finalReport.VatUeTax != null &&
                        !finalReport.VatUeTax.IsVatUePayer &&
                        string.IsNullOrWhiteSpace(finalReport.VatUeTax.ErrorMsg) &&
                        string.IsNullOrWhiteSpace(finalReport.VatUeTax.Warning))
                    {
                        finalReport.VatUeTax.ErrorMsg = "Frontend wskazał calculateVatUE=true na podstawie flagi VAT-UE z Biura, ale w bazie klienta nie znaleziono aktywnej konfiguracji rozliczeń VAT-UE dla tego okresu.";
                        _logger.LogWarning("[VAT-UE ODRZUCONO] {Msg}", finalReport.VatUeTax.ErrorMsg);
                    }

                    await vatUeProgress.CompleteAsync("Generowanie VAT-UE zakończone.");

                    if (!string.IsNullOrEmpty(finalReport.VatUeTax?.ErrorMsg))
                    {
                        finalReport.Status = "FAILED";
                        finalReport.Message += " Wystąpiły błędy podczas generowania VAT-UE.";
                    }
                }
                else
                {
                    _logger.LogInformation("Pomijam wyliczanie VAT-UE (flaga z Frontendu).");
                    await vatUeProgress.CompleteAsync("Pomijam wyliczanie VAT-UE.");
                }

                // =========================================================
                // ETAP 6: ZAŁĄCZNIKI PDF (po podatkach, żeby nie wpływały na PIT/VAT)
                // =========================================================
                if (maImportFaktur)
                {
                    var attachmentsProgress = progress.BeginSegment(JobProgressPlan.AttachmentsUnits);
                    if (zatwierdzoneCount > 0)
                    {
                        try
                        {
                            await attachmentsProgress.ReportAsync(5, "Podpinanie załączników PDF...");
                            await _attachmentService.PodepnijZalacznikiAsync(job, rezultatDekretacji, zatwierdzoneDekretacji, finalReport.Documents, attachmentsProgress.ReportAsync);
                            await attachmentsProgress.CompleteAsync("Obsługa załączników PDF zakończona.");
                        }
                        catch (Exception ex)
                        {
                            string warning = $"Nie udało się zakończyć obsługi załączników PDF: {ex.GetBaseException().Message}";
                            OznaczBladEtapuZalacznikow(finalReport.Documents, warning);
                            _logger.LogError(ex, "[ZAŁĄCZNIKI ETAP BŁĄD] JobId={JobId}; baza={Database}; zadekretowane={Count}; {Warning}",
                                job.JobId,
                                job.DatabaseName,
                                zatwierdzoneCount,
                                warning);
                            await attachmentsProgress.CompleteAsync("Obsługa załączników PDF zakończona z błędem.");
                        }
                    }
                    else
                    {
                        await attachmentsProgress.CompleteAsync("Brak zadekretowanych dokumentów. Pomijam załączniki PDF.");
                    }
                }

                // =========================================================
                // ETAP 7: AUDYT POTENCJALNYCH DUPLIKATÓW
                // =========================================================
                var duplicateProgress = progress.BeginSegment(JobProgressPlan.DuplicateAuditUnits);
                await duplicateProgress.ReportAsync(10, "Sprawdzanie potencjalnych duplikatów faktur...");
                finalReport.DuplicateInvoices = await _duplicateDetectionService.SprawdzDuplikatyAsync(dataRozliczenia);
                await duplicateProgress.CompleteAsync("Audyt potencjalnych duplikatów zakończony.");

                // =========================================================
                // ETAP 8: AUDYT STATUSU VAT KONTRAHENTÓW
                // =========================================================
                var vatStatusProgress = progress.BeginSegment(JobProgressPlan.VatStatusAuditUnits);
                await vatStatusProgress.ReportAsync(10, "Sprawdzanie statusu VAT kontrahentów...");
                finalReport.VatStatusVerifications = await _vatStatusVerificationService.SprawdzStatusyVatAsync(dataRozliczenia);
                await vatStatusProgress.CompleteAsync("Audyt statusu VAT kontrahentów zakończony.");

                // =========================================================
                // ETAP 9: ZAKOŃCZENIE I RAPORTOWANIE
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

                if (finalReport.Status == "SUCCESS" && CzySaProblemyStatusuVat(finalReport))
                {
                    finalReport.Status = "PARTIAL_SUCCESS";
                    finalReport.Message += " Część kontrahentów wymaga uwagi w audycie statusu VAT.";
                }

                string summaryJson = JsonSerializer.Serialize(finalReport, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation("Zadanie {JobId} zakończone. Status: {Status}. Dokumenty={DocumentsCount}; ostrzeżeniaDokumentów={WarningCount}; uwagiStatusuVAT={VatStatusWarnings}",
                    job.JobId,
                    finalReport.Status,
                    finalReport.Documents?.Count ?? 0,
                    finalReport.Documents?.Count(d => d.Warnings != null && d.Warnings.Count > 0) ?? 0,
                    finalReport.VatStatusVerifications?.Count(v => !v.VerificationOk || v.ActiveVatPayer == false) ?? 0);

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

        private void OznaczBladEtapuKsef(List<DocumentProcessingReport> manifest, string warning)
        {
            foreach (var raport in manifest ?? new List<DocumentProcessingReport>())
            {
                bool oczekiwanoKsef = !string.IsNullOrWhiteSpace(raport.KsefNumber);
                bool oczekiwanoKod = !string.IsNullOrWhiteSpace(raport.KsefCode);
                if (!oczekiwanoKsef && !oczekiwanoKod)
                {
                    continue;
                }

                if (oczekiwanoKsef)
                {
                    raport.KsefStatus = "verificationFailed";
                }

                if (oczekiwanoKod)
                {
                    raport.KsefCodeStatus = "verificationFailed";
                }

                ImportManifestService.DodajWarning(raport, warning);
            }
        }

        private void OznaczBladEtapuZalacznikow(List<DocumentProcessingReport> manifest, string warning)
        {
            foreach (var raport in manifest ?? new List<DocumentProcessingReport>())
            {
                if (string.IsNullOrWhiteSpace(raport.PdfFileName))
                {
                    continue;
                }

                raport.AttachmentStatus = "verificationFailed";
                ImportManifestService.DodajWarning(raport, warning);
            }
        }

        private void AktualizujStatusyDekretacji(
            List<DocumentProcessingReport> manifest,
            object rezultat,
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
                    object wynikObj = wynik;
                    resultEntries.Add(new DocumentResultEntry
                    {
                        ResultType = wynikObj?.GetType().Name,
                        DocumentId = PobierzDokumentId(wynikObj)
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

        private List<object> PobierzWynikiOperacji(object rezultat)
        {
            if (rezultat == null) return new List<object>();
            try { return ((IEnumerable)rezultat).Cast<object>().ToList(); }
            catch { return new List<object>(); }
        }

        private List<object> PobierzWynikoweZapisy(object wynikOperacji)
        {
            try
            {
                dynamic wynikOperacjiDyn = wynikOperacji;
                var wynikowe = (IEnumerable)wynikOperacjiDyn.WynikowePoprawneZapisy;
                return wynikowe?.Cast<object>().ToList() ?? new List<object>();
            }
            catch
            {
                return new List<object>();
            }
        }

        private int? PobierzDokumentId(object wynik)
        {
            try
            {
                dynamic wynikDyn = wynik;
                object id = wynikDyn?.DokumentId;
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
                d.AttachmentStatus == "attachedPartial" ||
                d.AttachmentStatus == "attachedPendingVerification" ||
                d.AttachmentStatus == "attachedUnverified" ||
                d.AttachmentStatus == "notVisibleAfterSave" ||
                d.AttachmentStatus == "verificationFailed" ||
                d.DecreeStatus == "noSchema" ||
                d.DecreeStatus == "schemaError" ||
                d.DecreeStatus == "skippedNotNew" ||
                d.DecreeStatus == "skippedEmployeeBillWithSubject" ||
                d.DecreeStatus == "skippedPartialAmortization" ||
                d.DecreeStatus == "skippedAmbiguousMatch" ||
                d.DecreeStatus == "resultMissing" ||
                d.DecreeStatus == "noResultEntries");
        }

        private bool CzySaProblemyStatusuVat(TaxSummaryReport report)
        {
            return report.VatStatusVerifications != null && report.VatStatusVerifications.Any(v =>
                !v.VerificationOk ||
                v.ActiveVatPayer == false);
        }
    }
}

