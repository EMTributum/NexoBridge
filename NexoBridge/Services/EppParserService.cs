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
using System.Reflection;
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

        public async Task<EppImportResult> ParseAndSyncAsync(ImportJob job, Func<int, string, Task> raportujPostep)
        {
            await raportujPostep(20, "Deserializacja plikow EPP...");
            var serializator = _sfera.PodajObiektTypu<ISerializatorEPP>();
            var wszystkieObiekty = new List<object>();
            var result = new EppImportResult();

            foreach (var plik in job.Files)
            {
                string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".epp");
                try
                {
                    File.WriteAllBytes(tempFile, plik.Content);
                    var obiektyZPliku = serializator.DeserializujObiektyZPliku(tempFile);
                    var obiektyLista = ((IEnumerable<object>)obiektyZPliku).ToList();
                    int licznikLokalny = obiektyLista.Count;
                    int dopisaneKsef = UzupelnijNumeryKsefWObiektachEpp(job, obiektyLista);
                    var naglowki = PobierzNaglowkiLogistyki(obiektyLista).ToList();

                    wszystkieObiekty.AddRange(obiektyLista);
                    result.Headers.AddRange(naglowki.Select(MapujNaglowek));
                    result.KsefAssignedCount += dopisaneKsef;

                    _logger.LogInformation("Rozpakowano plik EPP: {FileName} (Znaleziono {Count} obiektow, naglowki={Headers}, dopisano KSeF: {KsefCount})",
                        plik.FileName,
                        licznikLokalny,
                        naglowki.Count,
                        dopisaneKsef);
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            }

            result.ObjectsCount = wszystkieObiekty.Count;
            if (wszystkieObiekty.Count == 0)
            {
                _logger.LogError("Przerwano zadanie: Wszystkie pliki EPP byly puste.");
                throw new Exception("Wszystkie pliki EPP byly puste!");
            }

            await raportujPostep(40, "Etap 1: Synchronizacja slownikow Sfery...");
            var menedzerInstancji = _sfera.PodajObiektTypu<IInstancjeBazDanych>();
            var obecnaInstancja = menedzerInstancji.Dane.Wszystkie().FirstOrDefault();
            var menedzerOdbioru = _sfera.PodajObiektTypu<IOdbiorDanychKlientaBiuraRachunkowego>();

            var operacjaSlownikow = menedzerOdbioru.OdbierzOfflineEpp(wszystkieObiekty.ToArray(), obecnaInstancja, new ProstyInformatorOStatusieOdbioruDokumentuEPP());
            var operacjaDokumentow = operacjaSlownikow.Zapisz();

            await raportujPostep(60, "Etap 2: Wrzucanie faktur do Poczekalni...");
            operacjaDokumentow.Zapisz();

            return result;
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
                var dopasowania = naglowki
                    .Select(n => new { Naglowek = n, Score = ObliczDopasowanieNaglowka(n, meta) })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ToList();

                if (dopasowania.Count == 0)
                {
                    _logger.LogWarning("[KSEF EPP POMINIETY] Nie znaleziono naglowka EPP dla faktury z KSeF: {Numer} (NIP: {Nip}). KSeF zostanie zaraportowany jako niepotwierdzony, ale import bedzie kontynuowany.", meta.InvoiceNumber, meta.VendorNip);
                    continue;
                }

                int bestScore = dopasowania[0].Score;
                var najlepsi = dopasowania.Where(x => x.Score == bestScore).ToList();
                if (najlepsi.Count > 1)
                {
                    _logger.LogWarning("[KSEF EPP NIEJEDNOZNACZNY] Nie moge bezpiecznie dopisac KSeF do EPP dla faktury {Numer} (NIP: {Nip}) - znaleziono {Count} najlepszych naglowkow dla score={Score}. Kandydaci={Kandydaci}. Import bedzie kontynuowany bez tego dopisania.",
                        meta.InvoiceNumber,
                        meta.VendorNip,
                        najlepsi.Count,
                        bestScore,
                        ListaDoLogu(najlepsi.Select(x => OpiszNaglowek(x.Naglowek))));
                    continue;
                }

                var naglowek = najlepsi[0].Naglowek;
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

                _logger.LogInformation("[KSEF EPP] Dopisano KSeF {Ksef} do naglowka EPP: NumerDostawcy={NumerDostawcy}; PelnyNumer={PelnyNumer}; NIP={Nip}; score={Score}.",
                    ksefNumber,
                    naglowek.NumerDokumentuDostawcy,
                    naglowek.PelnyNumer,
                    naglowek.NIP,
                    bestScore);
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

        private int ObliczDopasowanieNaglowka(LogistykaNaglowek naglowek, InvoiceMetadata meta)
        {
            string nrFront = InvoiceDocumentMatcher.Normalize(meta.InvoiceNumber);
            string nipFront = InvoiceDocumentMatcher.NormalizeNip(meta.VendorNip);
            string nipEpp = InvoiceDocumentMatcher.NormalizeNip(naglowek.NIP);

            if (string.IsNullOrEmpty(nrFront) || string.IsNullOrEmpty(nipFront) || string.IsNullOrEmpty(nipEpp) ||
                !nipEpp.EndsWith(nipFront, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var numeryEpp = new[]
            {
                naglowek.NumerDokumentuDostawcy,
                naglowek.PelnyNumer,
                naglowek.Numer.ToString()
            }
            .Select(InvoiceDocumentMatcher.Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            if (numeryEpp.Any(n => n == nrFront)) return 100;
            if (numeryEpp.Any(n => n.EndsWith(nrFront, StringComparison.OrdinalIgnoreCase))) return 90;
            if (numeryEpp.Any(n => InvoiceDocumentMatcher.IsSafeNumberMatch(nrFront, n))) return 80;

            foreach (string wariant in InvoiceDocumentMatcher.GenerateNumberVariants(meta.InvoiceNumber))
            {
                if (numeryEpp.Any(n => n == wariant)) return 70;
                if (numeryEpp.Any(n => n.EndsWith(wariant, StringComparison.OrdinalIgnoreCase))) return 60;
                if (numeryEpp.Any(n => InvoiceDocumentMatcher.IsSafeNumberMatch(wariant, n))) return 50;
            }

            return 0;
        }

        private EppImportedHeader MapujNaglowek(LogistykaNaglowek naglowek)
        {
            object danePowiazania = PobierzWlasciwosc(naglowek, "DanePowiazaniaZKSeF");

            return new EppImportedHeader
            {
                InvoiceNumber = naglowek.NumerDokumentuDostawcy,
                FullNumber = naglowek.PelnyNumer,
                VendorNip = naglowek.NIP,
                TechnicalNumber = PobierzWlasciwosc(naglowek, "Numer")?.ToString(),
                ReceivedDate = PobierzDate(naglowek, "DataOtrzymania")
                    ?? PobierzDate(naglowek, "DataPrzyjecia")
                    ?? PobierzDate(naglowek, "DataWplywu"),
                IssueDate = PobierzDate(naglowek, "DataWystawienia"),
                KsefAssigned = danePowiazania != null,
                KsefNumber = PobierzWlasciwosc(danePowiazania, "NumerKSeF")?.ToString()
            };
        }

        private DateTime? PobierzDate(object source, string propertyName)
        {
            object value = PobierzWlasciwosc(source, propertyName);
            if (value == null) return null;
            if (value is DateTime dateTime) return dateTime;
            if (value is DateTimeOffset dateTimeOffset) return dateTimeOffset.DateTime;
            if (DateTime.TryParse(value.ToString(), out DateTime parsed)) return parsed;
            return null;
        }

        private object PobierzWlasciwosc(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName)) return null;
            try
            {
                var prop = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (prop != null) return prop.GetValue(source);

                var field = source.GetType().GetField(propertyName, BindingFlags.Instance | BindingFlags.Public);
                return field?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private string OpiszNaglowek(LogistykaNaglowek naglowek)
        {
            if (naglowek == null) return "brak";
            return $"NumerDostawcy={naglowek.NumerDokumentuDostawcy}, PelnyNumer={naglowek.PelnyNumer}, NIP={naglowek.NIP}, DataOtrzymania={PobierzDate(naglowek, "DataOtrzymania")?.ToString("yyyy-MM-dd") ?? "brak"}";
        }

        private string ListaDoLogu(IEnumerable<string> items)
        {
            if (items == null) return "brak";
            var list = items.Where(x => !string.IsNullOrWhiteSpace(x)).Take(50).ToList();
            return list.Count == 0 ? "brak" : string.Join(" || ", list);
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
    }
}

