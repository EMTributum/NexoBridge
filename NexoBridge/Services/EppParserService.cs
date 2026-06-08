using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ImportKsiegowy;
using InsERT.Moria.Narzedzia.EPP;
using InsERT.Moria.Narzedzia.EPP.Typy;
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
                var obiektyLista = ((IEnumerable<object>)obiektyZPliku).ToList();
                int licznikLokalny = obiektyLista.Count;
                int dopisaneKsef = UzupelnijNumeryKsefWObiektachEpp(job, obiektyLista);

                wszystkieObiekty.AddRange(obiektyLista);
                File.Delete(tempFile);

                _logger.LogInformation("Rozpakowano plik EPP: {FileName} (Znaleziono {Count} obiektów, dopisano KSeF: {KsefCount})", plik.FileName, licznikLokalny, dopisaneKsef);
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

        private int UzupelnijNumeryKsefWObiektachEpp(ImportJob job, List<object> obiekty)
        {
            var metadaneZKsef = (job.InvoicesMetadata ?? new List<InvoiceMetadata>())
                .Where(m => !string.IsNullOrWhiteSpace(OczyscNumerKsef(m.KsefNumber)))
                .ToList();

            if (metadaneZKsef.Count == 0)
            {
                return 0;
            }

            var naglowki = PobierzNaglowkiLogistyki(obiekty).ToList();
            int dopisane = 0;

            foreach (var meta in metadaneZKsef)
            {
                var kandydaci = naglowki
                    .Where(n => PasujeNaglowek(n, meta))
                    .ToList();

                if (kandydaci.Count == 0)
                {
                    throw new Exception($"Nie znaleziono nagłówka EPP dla faktury z KSeF: {meta.InvoiceNumber} (NIP: {meta.VendorNip}).");
                }

                if (kandydaci.Count > 1)
                {
                    throw new Exception($"Nie mogę bezpiecznie dopisać KSeF do EPP dla faktury {meta.InvoiceNumber} (NIP: {meta.VendorNip}) - znaleziono {kandydaci.Count} nagłówków.");
                }

                var naglowek = kandydaci[0];
                string ksefNumber = OczyscNumerKsef(meta.KsefNumber);
                var danePowiazania = new DanePowiazaniaZKSeF
                {
                    Symbol = !string.IsNullOrWhiteSpace(naglowek.PelnyNumer) ? naglowek.PelnyNumer : meta.InvoiceNumber,
                    NumerKSeF = ksefNumber,
                    Tryb = (byte)TrybDokumentuKSeFEpp.Normalna,
                    DataNadaniaNumeruKSeF = naglowek.DataWystawienia
                };

                naglowek.DanePowiazaniaZKSeF = danePowiazania;
                DodajDanePowiazaniaDoKontenera(obiekty, danePowiazania);
                dopisane++;

                _logger.LogInformation("[KSEF EPP] Dopisano KSeF {Ksef} do nagłówka EPP: NumerDostawcy={NumerDostawcy}; PelnyNumer={PelnyNumer}; NIP={Nip}.",
                    ksefNumber,
                    naglowek.NumerDokumentuDostawcy,
                    naglowek.PelnyNumer,
                    naglowek.NIP);
            }

            return dopisane;
        }

        private IEnumerable<LogistykaNaglowek> PobierzNaglowkiLogistyki(IEnumerable<object> obiekty)
        {
            foreach (var obiekt in obiekty)
            {
                if (obiekt is LogistykaNaglowek naglowek)
                {
                    yield return naglowek;
                    continue;
                }

                if (obiekt is DaneDoWyslaniaEPP dane && dane.DokumentyLogistyka != null)
                {
                    foreach (var n in dane.DokumentyLogistyka)
                    {
                        yield return n;
                    }
                }
            }
        }

        private void DodajDanePowiazaniaDoKontenera(IEnumerable<object> obiekty, DanePowiazaniaZKSeF danePowiazania)
        {
            foreach (var obiekt in obiekty)
            {
                if (obiekt is DaneDoWyslaniaEPP dane)
                {
                    if (dane.DanePowiazaniaZKSeF == null)
                    {
                        dane.DanePowiazaniaZKSeF = new List<DanePowiazaniaZKSeF>();
                    }

                    bool juzIstnieje = dane.DanePowiazaniaZKSeF.Any(x =>
                        string.Equals(x.Symbol, danePowiazania.Symbol, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.NumerKSeF, danePowiazania.NumerKSeF, StringComparison.OrdinalIgnoreCase));

                    if (!juzIstnieje)
                    {
                        dane.DanePowiazaniaZKSeF.Add(danePowiazania);
                    }
                }
            }
        }

        private bool PasujeNaglowek(LogistykaNaglowek naglowek, InvoiceMetadata meta)
        {
            string nrFront = Normalizuj(meta.InvoiceNumber);
            string nipFront = Normalizuj(meta.VendorNip).Replace("pl", "");
            string nipEpp = Normalizuj(naglowek.NIP).Replace("pl", "");

            if (string.IsNullOrEmpty(nrFront) || string.IsNullOrEmpty(nipFront) || string.IsNullOrEmpty(nipEpp))
            {
                return false;
            }

            var numeryEpp = new[]
            {
                naglowek.NumerDokumentuDostawcy,
                naglowek.PelnyNumer,
                naglowek.Numer.ToString()
            }
            .Select(Normalizuj)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

            return nipEpp.EndsWith(nipFront) &&
                   numeryEpp.Any(n => n == nrFront || n.EndsWith(nrFront));
        }

        private string OczyscNumerKsef(string value)
        {
            string cleaned = value?.Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return null;

            string normalized = cleaned.ToUpperInvariant();
            if (normalized == "BFK" || normalized == "DI" || normalized == "OFF" ||
                normalized == "BRAK" || normalized == "NONE" || normalized == "NULL" ||
                normalized == "NIE DOTYCZY")
            {
                return null;
            }

            return cleaned;
        }

        private string Normalizuj(string input)
        {
            return input == null ? "" : new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }
    }
}
