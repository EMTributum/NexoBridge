using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class NexoImportService
    {
        private readonly EppParserService _parserService;
        private readonly AccountingService _accountingService;
        private readonly AttachmentService _attachmentService;
        private readonly ILogger<NexoImportService> _logger;

        public NexoImportService(
            EppParserService parserService,
            AccountingService accountingService,
            AttachmentService attachmentService,
            ILogger<NexoImportService> logger)
        {
            _parserService = parserService;
            _accountingService = accountingService;
            _attachmentService = attachmentService;
            _logger = logger;
        }

        public async Task PrzetworzZadanieAsync(ImportJob job, Func<int, string, Task> raportujPostep)
        {
            _logger.LogInformation("Rozpoczynam przetwarzanie zadania: {JobId}. Baza docelowa: {Database}", job.JobId, job.DatabaseName);

            // 1. Deserializacja i wrzucenie do Poczekalni
            await _parserService.ParseAndSyncAsync(job, raportujPostep);

            // 2. Dekretacja i weryfikacja schematów
            var (rezultat, zatwierdzone) = await _accountingService.DekretujAsync(raportujPostep);

            // 3. Ostatni etap: wpinanie załączników
            if (zatwierdzone.Count > 0)
            {
                await _attachmentService.PodepnijZalacznikiAsync(job, rezultat, zatwierdzone, raportujPostep);
                _logger.LogInformation("Zadanie {JobId} zakończone pełnym sukcesem. Zadekretowano {Count} faktur.", job.JobId, zatwierdzone.Count);
                await raportujPostep(100, $"[SUKCES] Proces zakończony. Pomyślnie zadekretowano {zatwierdzone.Count} faktur.");
            }
            else
            {
                // To wyłapanie zapobiegawcze - AccountingService wyrzuci wcześniej błąd, gdy nie będzie faktur.
                await raportujPostep(100, "Zakończono! (Brak nowych dokumentów do zadekretowania).");
            }
        }
    }
}