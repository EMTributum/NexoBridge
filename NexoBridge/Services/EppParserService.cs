using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ImportKsiegowy;
using InsERT.Moria.Narzedzia.EPP;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class EppParserService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<EppParserService> _logger;

        public EppParserService(Uchwyt sfera, ILogger<EppParserService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task ParseAndSyncAsync(ImportJob job, Func<int, string, Task> raportujPostep)
        {
            await raportujPostep(20, "Deserializacja plików EPP...");
            var serializator = _sfera.PodajObiektTypu<ISerializatorEPP>();
            var wszystkieObiekty = new List<object>();

            foreach (var plik in job.Files)
            {
                string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".epp");
                File.WriteAllBytes(tempFile, plik.Content);
                var obiektyZPliku = serializator.DeserializujObiektyZPliku(tempFile);

                int licznikLokalny = 0;
                foreach (var o in obiektyZPliku) licznikLokalny++;

                wszystkieObiekty.AddRange((IEnumerable<object>)obiektyZPliku);
                File.Delete(tempFile);

                _logger.LogInformation("Rozpakowano plik EPP: {FileName} (Znaleziono {Count} obiektów)", plik.FileName, licznikLokalny);
            }

            if (wszystkieObiekty.Count == 0)
            {
                _logger.LogError("Przerwano zadanie: Wszystkie pliki EPP były puste.");
                throw new Exception("Wszystkie pliki EPP były puste!");
            }

            await raportujPostep(40, "Etap 1: Synchronizacja słowników Sfery...");
            var menedzerInstancji = _sfera.PodajObiektTypu<IInstancjeBazDanych>();
            var obecnaInstancja = menedzerInstancji.Dane.Wszystkie().FirstOrDefault();
            var menedzerOdbioru = _sfera.PodajObiektTypu<IOdbiorDanychKlientaBiuraRachunkowego>();

            var operacjaSlownikow = menedzerOdbioru.OdbierzOfflineEpp(wszystkieObiekty.ToArray(), obecnaInstancja, new ProstyInformatorOStatusieOdbioruDokumentuEPP());
            var operacjaDokumentow = operacjaSlownikow.Zapisz();

            await raportujPostep(60, "Etap 2: Wrzucanie faktur do Poczekalni...");
            operacjaDokumentow.Zapisz();
        }
    }
}