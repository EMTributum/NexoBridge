using InsERT.Moria.ModelDanych;
using Microsoft.Extensions.Logging;
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

        public async Task<TaxSummaryReport> PrzetworzZadanieAsync(ImportJob job, Func<int, string, Task> raportujPostep)
        {
            var finalReport = new TaxSummaryReport
            {
                JobId = job.JobId,
                Status = "SUCCESS",
                Message = "Proces zakończony."
            };

            DateTime dataRozliczenia = new DateTime(job.BillingYear, job.BillingMonth, 1);
            int zatwierdzoneCount = 0;
            bool maImportFaktur = job.ImportInvoices && job.Files != null && job.Files.Count > 0;
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
                finalReport.Documents = _manifestService.ZbudujManifest(job);

                // =========================================================
                // ETAP 1: OBSŁUGA FAKTUR
                // =========================================================
                if (maImportFaktur)
                {
                    await raportujPostep(10, "Pobieranie i analiza plików EPP...");
                    await _parserService.ParseAndSyncAsync(job, raportujPostep);

                    var oczekujacePoImporcie = _manifestService.PobierzDokumentyWPoczekalni();
                    _manifestService.AktualizujPoPoczekalni(finalReport.Documents, oczekujacePoImporcie);
                    await _ksefNumberAssignmentService.PrzypiszPrzedDekretacjaAsync(job, finalReport.Documents, raportujPostep);
                }
                else
                {
                    _logger.LogInformation("Pomijam moduł importu faktur (ImportInvoices={ImportInvoices}, Files={Files}).", job.ImportInvoices, job.Files?.Count ?? 0);
                    await raportujPostep(10, "Tryb bez faktur. Pomijam pobieranie EPP...");
                }

                // =========================================================
                // ETAP 2: AMORTYZACJA
                // =========================================================
                if (job.CalculateAmortization)
                {
                    await raportujPostep(30, "Sprawdzanie środków trwałych i naliczanie amortyzacji...");
                    finalReport.Amortization = await _amortizationService.ObliczAmortyzacjeAsync(dataRozliczenia);
                    amortyzacjaWygenerowalaDokumenty = finalReport.Amortization?.DocumentsGenerated > 0;
                    if (!amortyzacjaWygenerowalaDokumenty)
                    {
                        _logger.LogInformation("Amortyzacja nie wygenerowała nowych dokumentów. Pomijam dekretację amortyzacji (DocumentsGenerated={DocumentsGenerated}).", finalReport.Amortization?.DocumentsGenerated ?? 0);
                    }
                }
                else
                {
                    _logger.LogInformation("Pomijam naliczanie amortyzacji (flaga z Frontendu).");
                }

                // =========================================================
                // ETAP 3: DEKRETACJA WŁAŚCIWA (cała Poczekalnia)
                // =========================================================
                bool wymagaDekretacji = maImportFaktur || amortyzacjaWygenerowalaDokumenty;
                if (wymagaDekretacji)
                {
                    var oczekujacePrzedDekretacja = _manifestService.PobierzDokumentyWPoczekalni();
                    _manifestService.AktualizujPoPoczekalni(finalReport.Documents, oczekujacePrzedDekretacja);

                    await raportujPostep(50, "Dekretacja dokumentów...");
                    var (rezultat, zatwierdzone, oczekujace, brakSchematu, bledneSchematy) = await _accountingService.DekretujAsync(raportujPostep);
                    zatwierdzoneCount = zatwierdzone.Count;
                    AktualizujStatusyDekretacji(finalReport.Documents, rezultat, zatwierdzone, brakSchematu, bledneSchematy);

                    // =========================================================
                    // ETAP 4: ZAŁĄCZNIKI i KSeF (nie blokują procesu)
                    // =========================================================
                    if (maImportFaktur && zatwierdzoneCount > 0)
                    {
                        await raportujPostep(70, "Podpinanie załączników PDF...");
                        await _attachmentService.PodepnijZalacznikiAsync(job, rezultat, zatwierdzone, finalReport.Documents, raportujPostep);
                        await _ksefNumberAssignmentService.ZweryfikujPoDekretacjiAsync(job, rezultat, zatwierdzone, finalReport.Documents, raportujPostep);
                    }

                    if (zatwierdzoneCount == 0 && maImportFaktur)
                    {
                        _logger.LogInformation("Brak nowych dokumentów do zaksięgowania z dostarczonego EPP lub Poczekalni.");
                        finalReport.Message = "Brak dokumentów do zaksięgowania. Szczegóły są dostępne w raporcie dokumentów.";
                    }
                }
                else
                {
                    await raportujPostep(50, "Brak operacji wymagających dekretacji. Przechodzę do podatków...");
                }

                // =========================================================
                // ETAP 5: WYLICZENIE PODATKÓW PIT i VAT
                // =========================================================
                if (job.CalculatePit)
                {
                    await raportujPostep(85, "Wyliczanie zaliczek na podatek PIT...");
                    finalReport.PitTaxes = await _pitService.WyliczZaliczkiWspolnikowAsync(dataRozliczenia);

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
                }

                if (job.CalculateVat)
                {
                    await raportujPostep(95, "Generowanie JPK_V7...");
                    finalReport.VatTax = await _vatService.WygenerujJpkVatAsync(dataRozliczenia);

                    if (!string.IsNullOrEmpty(finalReport.VatTax?.ErrorMsg))
                    {
                        finalReport.Status = "FAILED";
                        finalReport.Message += " Wystąpiły błędy podczas generowania JPK.";
                    }
                }
                else
                {
                    _logger.LogInformation("Pomijam wyliczanie VAT (flaga z Frontendu).");
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

                await raportujPostep(100, raportKoncowy);
            }
            catch (Exception ex)
            {
                finalReport.Status = "FAILED";
                finalReport.Message = $"Błąd krytyczny procesu: {ex.Message}";
                _logger.LogError(ex, "Przerwano procesowanie zadania {JobId}", job.JobId);
                await raportujPostep(100, $"[BŁĄD KRYTYCZNY] {ex.Message}");
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

