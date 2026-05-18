using InsERT.Mox.Telemetry;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using NLog;
using NPOI.POIFS.Properties;
using SQLitePCL;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static Dropbox.Api.TeamLog.LoginMethod;
using static InsERT.Moria.Wspolne.Tagi.Consts.TagConsts;

namespace NexoBridge.Services
{
    public class NexoImportService
    {
        private readonly EppParserService _parserService;
        private readonly AmortizationService _amortizationService;
        private readonly AccountingService _accountingService;
        private readonly PitCalculationService _pitService;
        private readonly VatCalculationService _vatService;
        private readonly AttachmentService _attachmentService;
        private readonly ILogger<NexoImportService> _logger;

        public NexoImportService(
            EppParserService parserService,
            AmortizationService amortizationService,
            AccountingService accountingService,
            PitCalculationService pitCalculationService,
            VatCalculationService vatCalculationService,
            AttachmentService attachmentService,
            ILogger<NexoImportService> logger)
        {
            _parserService = parserService;
            _amortizationService = amortizationService;
            _accountingService = accountingService;
            _pitService = pitCalculationService;
            _vatService = vatCalculationService;
            _attachmentService = attachmentService;
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

            DateTime dataRozliczenia = DateTime.Now.AddMonths(-1);

            _logger.LogInformation("Rozpoczynam zintegrowane przetwarzanie zadania: {JobId}. Baza docelowa: {Database}", job.JobId, job.DatabaseName);

            try
            {
                // =========================================================
                // ETAP 1: ODCZYT FAKTUR (Rozpakowanie i Poczekalnia)
                // =========================================================
                await _parserService.ParseAndSyncAsync(job, raportujPostep);

                // =========================================================
                // ETAP 2: AMORTYZACJA (Opcjonalna)
                // =========================================================
                if (job.CalculateAmortization)
                {
                    await raportujPostep(65, "Sprawdzanie środków trwałych i naliczanie amortyzacji...");
                    finalReport.Amortization = await _amortizationService.ObliczAmortyzacjeAsync(dataRozliczenia);
                }
                else
                {
                    _logger.LogInformation("Pomijam naliczanie amortyzacji (flaga z Frontendu).");
                }

                // =========================================================
                // ETAP 3: DEKRETACJA WŁAŚCIWA (Sędzia)
                // =========================================================
                var (rezultat, zatwierdzone) = await _accountingService.DekretujAsync(raportujPostep);

                if (zatwierdzone.Count > 0)
                {
                    // =========================================================
                    // ETAP 4: ZAŁĄCZNIKI PDF (Podpinanie do zadekretowanych)
                    // =========================================================
                    await _attachmentService.PodepnijZalacznikiAsync(job, rezultat, zatwierdzone, raportujPostep);

                    // =========================================================
                    // ETAP 5: WYLICZENIE PODATKÓW (PIT i VAT - Opcjonalne)
                    // =========================================================

                    if (job.CalculatePit)
                    {
                        await raportujPostep(96, "Wyliczanie zaliczek na podatek PIT...");
                        finalReport.PitTaxes = await _pitService.WyliczZaliczkiWspolnikowAsync(dataRozliczenia);

                        if (finalReport.PitTaxes.Any(p => !string.IsNullOrEmpty(p.CriticalError)))
                        {
                            finalReport.Status = "FAILED";
                            finalReport.Message = "Proces natrafił na krytyczne błędy podczas wyliczania PIT. Sprawdź konfigurację wspólników w Nexo.";
                            _logger.LogWarning(finalReport.Message);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Pomijam wyliczanie PIT (flaga z Frontendu).");
                    }

                    if (job.CalculateVat)
                    {
                        await raportujPostep(98, "Generowanie JPK_V7...");
                        finalReport.VatTax = await _vatService.WygenerujJpkVatAsync(dataRozliczenia);

                        // Dodano zabezpieczenie null-conditional (?.), na wypadek gdyby VatTax był null
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

                    // Zabezpieczenie przed błędem, gdy amortyzacja była pominięta
                    if (finalReport.Status == "SUCCESS" && !string.IsNullOrEmpty(finalReport.Amortization?.Warning))
                    {
                        finalReport.Status = "PARTIAL_SUCCESS";
                    }

                    // =========================================================
                    // ETAP 6: RAPORTOWANIE
                    // =========================================================
                    string summaryJson = JsonSerializer.Serialize(finalReport, new JsonSerializerOptions { WriteIndented = true });
                    _logger.LogInformation("Zadanie {JobId} zakończone. Status: {Status}", job.JobId, finalReport.Status);

                    await raportujPostep(100, $"[ZAKOŃCZONO] Status: {finalReport.Status}. Zadekretowano {zatwierdzone.Count} dok.");
                }
                else
                {
                    await raportujPostep(100, "Zakończono! (Brak nowych dokumentów do zadekretowania).");
                    finalReport.Message = "Brak dokumentów do zaksięgowania.";
                }
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
    }
}