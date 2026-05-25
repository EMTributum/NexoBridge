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

            DateTime dataRozliczenia = new DateTime(job.BillingYear, job.BillingMonth, 1);
            int zatwierdzoneCount = 0;

            _logger.LogInformation("Rozpoczynam zintegrowane przetwarzanie zadania: {JobId}. Baza docelowa: {Database} za {Miesiac}/{Rok}",
                job.JobId, job.DatabaseName, job.BillingMonth, job.BillingYear);

            try
            {
                // =========================================================
                // ETAP 1: OBSŁUGA FAKTUR
                // =========================================================
                if (job.ImportInvoices)
                {
                    await raportujPostep(10, "Pobieranie i analiza plików EPP...");
                    await _parserService.ParseAndSyncAsync(job, raportujPostep);
                }
                else
                {
                    _logger.LogInformation("Pomijam moduł importu faktur (flaga ImportInvoices = false).");
                    await raportujPostep(10, "Tryb bez faktur. Pomijam pobieranie EPP...");
                }

                // =========================================================
                // ETAP 2: AMORTYZACJA
                // =========================================================
                if (job.CalculateAmortization)
                {
                    await raportujPostep(30, "Sprawdzanie środków trwałych i naliczanie amortyzacji...");
                    finalReport.Amortization = await _amortizationService.ObliczAmortyzacjeAsync(dataRozliczenia);
                }
                else
                {
                    _logger.LogInformation("Pomijam naliczanie amortyzacji (flaga z Frontendu).");
                }

                // =========================================================
                // ETAP 3: DEKRETACJA WŁAŚCIWA (Faktury + Amortyzacja)
                // =========================================================
                // Dekretujemy zawsze, jeśli wpadły nowe faktury LUB wygenerowano amortyzację
                if (job.ImportInvoices || job.CalculateAmortization)
                {
                    await raportujPostep(50, "Dekretacja dokumentów...");
                    var (rezultat, zatwierdzone) = await _accountingService.DekretujAsync(raportujPostep);
                    zatwierdzoneCount = zatwierdzone.Count;

                    // =========================================================
                    // ETAP 4: ZAŁĄCZNIKI (Tylko dla faktur!)
                    // =========================================================
                    if (job.ImportInvoices && zatwierdzoneCount > 0)
                    {
                        await raportujPostep(70, "Podpinanie załączników PDF...");
                        await _attachmentService.PodepnijZalacznikiAsync(job, rezultat, zatwierdzone, raportujPostep);
                    }

                    if (zatwierdzoneCount == 0 && job.ImportInvoices)
                    {
                        _logger.LogInformation("Brak nowych dokumentów do zaksięgowania z dostarczonego EPP.");
                        finalReport.Message = "Brak dokumentów do zaksięgowania (EPP był pusty lub duplikaty). ";
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
                if (finalReport.Status == "SUCCESS" && !string.IsNullOrEmpty(finalReport.Amortization?.Warning))
                {
                    finalReport.Status = "PARTIAL_SUCCESS";
                }

                string summaryJson = JsonSerializer.Serialize(finalReport, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation("Zadanie {JobId} zakończone. Status: {Status}", job.JobId, finalReport.Status);

                string raportKoncowy = (job.ImportInvoices || job.CalculateAmortization)
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
    }
}