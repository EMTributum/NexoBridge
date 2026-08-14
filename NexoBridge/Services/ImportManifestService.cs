using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ImportKsiegowy;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using InsERT.Moria;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class ImportManifestService
    {
        private readonly Uchwyt _sfera;
        private readonly PoczekalniaBaselineStore _baselineStore;
        private readonly ILogger<ImportManifestService> _logger;

        public ImportManifestService(Uchwyt sfera, PoczekalniaBaselineStore baselineStore, ILogger<ImportManifestService> logger)
        {
            _sfera = sfera;
            _baselineStore = baselineStore;
            _logger = logger;
        }

        public List<DocumentProcessingReport> ZbudujManifest(ImportJob job)
        {
            var documents = new List<DocumentProcessingReport>();
            foreach (var meta in job.InvoicesMetadata ?? new List<InvoiceMetadata>())
            {
                var item = new DocumentProcessingReport
                {
                    Source = "frontendPackage",
                    InvoiceNumber = meta.InvoiceNumber,
                    VendorNip = meta.VendorNip,
                    KsefNumber = Oczysc(meta.KsefNumber),
                    KsefCode = Oczysc(meta.KsefCode),
                    PdfFileName = meta.PdfFileName,
                    KsefStatus = string.IsNullOrWhiteSpace(Oczysc(meta.KsefNumber)) ? "notProvided" : "pending",
                    KsefCodeStatus = string.IsNullOrWhiteSpace(Oczysc(meta.KsefCode)) ? "notProvided" : "pending",
                    AttachmentStatus = string.IsNullOrWhiteSpace(meta.PdfFileName) ? "notProvided" : "pending",
                    DecreeStatus = "pending"
                };

                if (!string.IsNullOrWhiteSpace(item.KsefNumber) &&
                    string.IsNullOrWhiteSpace(InvoiceDocumentMatcher.NormalizeNip(item.VendorNip)))
                {
                    DodajWarning(item, "Metadane zawieraja numer KSeF, ale brakuje NIP dostawcy. Bez NIP nie uzyjemy bezpiecznego fallbacku po KSeF.");
                }

                documents.Add(item);
            }

            foreach (var attachment in job.Attachments ?? new List<AttachmentPayload>())
            {
                string attachmentNumber = InvoiceDocumentMatcher.Normalize(attachment.DocumentNumber);
                string attachmentNip = InvoiceDocumentMatcher.NormalizeNip(attachment.VendorNip);
                bool exists = documents.Any(d =>
                    string.Equals(d.PdfFileName, attachment.FileName, StringComparison.OrdinalIgnoreCase) ||
                    (InvoiceDocumentMatcher.Normalize(d.InvoiceNumber) == attachmentNumber &&
                     (string.IsNullOrWhiteSpace(attachmentNip) ||
                      string.IsNullOrWhiteSpace(InvoiceDocumentMatcher.NormalizeNip(d.VendorNip)) ||
                      InvoiceDocumentMatcher.NormalizeNip(d.VendorNip) == attachmentNip)));

                if (exists) continue;

                documents.Add(new DocumentProcessingReport
                {
                    Source = "frontendPackage",
                    InvoiceNumber = attachment.DocumentNumber,
                    VendorNip = attachment.VendorNip,
                    PdfFileName = attachment.FileName,
                    KsefStatus = "notProvided",
                    KsefCodeStatus = "notProvided",
                    AttachmentStatus = "pending",
                    DecreeStatus = "pending",
                    Warnings = new List<string> { "Wpis manifestu utworzony z zalacznika, bo nie znaleziono odpowiadajacych metadanych faktury." }
                });
            }

            _logger.LogInformation("[MANIFEST] JobId={JobId}; wpisyZPaczki={Count}; dokumenty={Documents}",
                job.JobId,
                documents.Count,
                ListaDoLogu(documents.Select(OpiszRaport)));

            return documents;
        }

        /// <summary>
        /// Numery dokumentów aktualnie w poczekalni - do wywołania PRZED importem EPP w danym zleceniu,
        /// żeby ewentualny bootstrap baseline'u mógł odróżnić stary zaległy backlog od dokumentu, który
        /// TO SAMO zlecenie właśnie zaimportowało (patrz PobierzWyborDokumentowWPoczekalni).
        /// </summary>
        public HashSet<int> PobierzNumeryOczekujaceTeraz()
        {
            return PobierzWszystkieOczekujace().Select(d => d.Nr).ToHashSet();
        }

        public async Task<WaitingRoomDocumentSelection> PobierzWyborDokumentowWPoczekalni(DateTime dataRozliczenia, ImportPackageContext packageContext, ImportJob job, HashSet<int> poolNumeryPrzedZleceniem)
        {
            var wszystkieOczekujace = PobierzWszystkieOczekujace();
            var znaneNumery = await _baselineStore.PobierzZnaneNumeryAsync(job?.DatabaseName);
            bool bootstrap = znaneNumery == null;

            // Podczas bootstrapu "znane" to backlog sprzed TEGO zlecenia, a nie cała aktualna pula -
            // inaczej dokument, który to samo zlecenie właśnie zaimportowało EPP-em, zostałby po cichu
            // wrzucony do "już znanych" i nigdy by się nie zadekretował.
            bool CzyZnany(DokumentDoKsiegowania doc) => bootstrap
                ? (poolNumeryPrzedZleceniem == null || poolNumeryPrzedZleceniem.Contains(doc.Nr))
                : znaneNumery.Contains(doc.Nr);

            var wybor = WaitingRoomDocumentFilter.SelectForBaseline(wszystkieOczekujace, CzyZnany);

            _logger.LogInformation("[MANIFEST POCZEKALNIA FILTR BASELINE] bootstrap={Bootstrap}; wszystkie={All}; doObslugi={Included}; nowe={IncludedNew}; wyjatkiKadrowe={IncludedPayrollException}; znaneZBaseline={SkippedNotNew}; amortyzacjeCzastkowe={PartialAmortization}; rachunkiPracowniczeZPodmiotem={EmployeeBillsWithSubject}; dokumentyWewnetrzne={InternalDocuments}",
                bootstrap,
                wybor.Total,
                wybor.Included.Count,
                wybor.IncludedNew.Count,
                wybor.IncludedPayrollException.Count,
                wybor.SkippedNotNew.Count,
                wybor.PartialAmortization.Count,
                wybor.EmployeeBillsWithSubject.Count,
                wybor.InternalDocuments.Count);

            return wybor;
        }

        private List<DokumentDoKsiegowania> PobierzWszystkieOczekujace()
        {
            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            return menedzerDokumentow.Dane.Wszystkie()
                .Where(d => (int)d.StatusKsiegowy == 2)
                .ToList();
        }

        public void AktualizujPoPoczekalni(List<DocumentProcessingReport> manifest, WaitingRoomDocumentSelection wybor)
        {
            if (wybor == null)
            {
                AktualizujPoPoczekalni(manifest, new List<DokumentDoKsiegowania>());
                return;
            }

            AktualizujPoPoczekalni(manifest, wybor.Included, wybor.IncludedPayrollException.Select(d => d.Nr).ToHashSet());
            AktualizujPominietePrzezFiltr(
                manifest,
                wybor.SkippedNotNew,
                "skippedNotNew",
                "Dokument byl juz znany z poprzedniego przebiegu (baseline). Nie przekazano go ponownie do dekretacji.");
            AktualizujPominietePrzezFiltr(
                manifest,
                wybor.EmployeeBillsWithSubject,
                "skippedEmployeeBillWithSubject",
                "Rachunek do umowy pracowniczej ma podmiot, wiec zostal pozostawiony w Poczekalni.");
            AktualizujPominietePrzezFiltr(
                manifest,
                wybor.PartialAmortization,
                "skippedPartialAmortization",
                "Pominieto amortyzacje czastkowa. Do dekretacji trafia tylko dokument zbiorczy.");
            AktualizujPominietePrzezFiltr(
                manifest,
                wybor.InternalDocuments,
                "skippedInternalDocument",
                "Dokument generowany wewnetrznie przez Rachmistrza (ZUS/bank/kasa/VAT/roznice kursowe itp.) - pozostawiony w Poczekalni do recznej dekretacji.");
        }

        public void AktualizujPoPoczekalni(List<DocumentProcessingReport> manifest, List<DokumentDoKsiegowania> oczekujace)
        {
            AktualizujPoPoczekalni(manifest, oczekujace, new HashSet<int>());
        }

        private void AktualizujPoPoczekalni(List<DocumentProcessingReport> manifest, List<DokumentDoKsiegowania> oczekujace, HashSet<int> payrollExceptionNumbers)
        {
            if (manifest == null) return;
            var matchedWaitingRoomNumbers = new HashSet<int>();
            payrollExceptionNumbers ??= new HashSet<int>();

            foreach (var item in manifest.Where(d => d.Source == "frontendPackage"))
            {
                var meta = new InvoiceMetadata
                {
                    InvoiceNumber = item.InvoiceNumber,
                    VendorNip = item.VendorNip,
                    KsefNumber = item.KsefNumber,
                    KsefCode = item.KsefCode,
                    PdfFileName = item.PdfFileName
                };

                var match = InvoiceDocumentMatcher.Match(oczekujace, meta);
                if (match.Document == null)
                {
                    var ksefMatch = InvoiceDocumentMatcher.MatchByExactKsefAndNip(oczekujace, meta);
                    if (ksefMatch.Document != null || ksefMatch.Status == "ambiguous")
                    {
                        match = ksefMatch;
                    }
                }

                item.MatchStatus = match.Status;

                if (match.Document != null)
                {
                    WypelnijDanePoczekalni(item, match.Document);
                    item.WaitingRoomStatus = "found";
                    matchedWaitingRoomNumbers.Add(match.Document.Nr);
                    if (match.Status != "matchedExact")
                    {
                        DodajWarning(item, $"Dopasowano dokument w Poczekalni wariantem numeru: {match.Status}, wariant={match.MatchedVariant}.");
                    }
                }
                else if (match.Status == "ambiguous")
                {
                    item.WaitingRoomStatus = "ambiguous";
                    item.DecreeStatus = "skippedAmbiguousMatch";
                    DodajWarning(item, $"Nie mozna jednoznacznie dopasowac dokumentu w Poczekalni. {match.Reason} Kandydaci: {ListaDoLogu(match.Candidates)}");
                }
                else
                {
                    item.WaitingRoomStatus = "notFound";
                    DodajWarning(item, $"Nie znaleziono dokumentu w Poczekalni. {match.Reason} Kandydaci z tym NIP: {ListaDoLogu(match.Candidates)}");
                }
            }

            foreach (var doc in oczekujace)
            {
                if (WaitingRoomDocumentFilter.CzyCzastkowaAmortyzacja(doc)) continue;
                if (matchedWaitingRoomNumbers.Contains(doc.Nr)) continue;
                if (ZnajdzRaportPoNrPoczekalni(manifest, doc.Nr) != null) continue;

                manifest.Add(new DocumentProcessingReport
                {
                    Source = "waitingRoomExtra",
                    InvoiceNumber = doc.NumerDokumentu,
                    VendorNip = doc.PodmiotHistoria?.NIP,
                    KsefNumber = Oczysc(doc.NumerKSeF),
                    KsefCode = null,
                    MatchStatus = payrollExceptionNumbers.Contains(doc.Nr)
                        ? "includedPayrollException"
                        : "includedNew",
                    WaitingRoomStatus = "found",
                    WaitingRoomId = doc.Id.ToString(),
                    WaitingRoomNr = doc.Nr,
                    WaitingRoomNumber = doc.NumerDokumentu,
                    WaitingRoomNip = doc.PodmiotHistoria?.NIP,
                    KsefStatus = string.IsNullOrWhiteSpace(Oczysc(doc.NumerKSeF)) ? "notProvided" : "presentInWaitingRoom",
                    KsefCodeStatus = "notProvided",
                    AttachmentStatus = "notProvided",
                    DecreeStatus = "pending"
                });
            }

            _logger.LogInformation("[MANIFEST POCZEKALNIA] wpisy={Count}; znalezione={Found}; nieznalezione={NotFound}; niejednoznaczne={Ambiguous}; pominieteBezN={SkippedNotNew}; pominieteRachunkiZPodmiotem={SkippedEmployeeBillWithSubject}; pominieteAmortyzacjeCzastkowe={SkippedPartialAmortization}; dodatkowe={Extra}",
                manifest.Count,
                manifest.Count(d => d.WaitingRoomStatus == "found" && d.Source == "frontendPackage"),
                manifest.Count(d => d.WaitingRoomStatus == "notFound"),
                manifest.Count(d => d.WaitingRoomStatus == "ambiguous"),
                manifest.Count(d => d.DecreeStatus == "skippedNotNew"),
                manifest.Count(d => d.DecreeStatus == "skippedEmployeeBillWithSubject"),
                manifest.Count(d => d.DecreeStatus == "skippedPartialAmortization"),
                manifest.Count(d => d.Source == "waitingRoomExtra"));
        }

        private void AktualizujPominietePrzezFiltr(List<DocumentProcessingReport> manifest, List<DokumentDoKsiegowania> documents, string decreeStatus, string warning)
        {
            if (manifest == null || documents == null || documents.Count == 0) return;

            foreach (var item in manifest.Where(d => d.Source == "frontendPackage" && !d.WaitingRoomNr.HasValue))
            {
                var meta = new InvoiceMetadata
                {
                    InvoiceNumber = item.InvoiceNumber,
                    VendorNip = item.VendorNip,
                    KsefNumber = item.KsefNumber,
                    KsefCode = item.KsefCode,
                    PdfFileName = item.PdfFileName
                };

                var match = InvoiceDocumentMatcher.Match(documents, meta);
                if (match.Document == null)
                {
                    var ksefMatch = InvoiceDocumentMatcher.MatchByExactKsefAndNip(documents, meta);
                    if (ksefMatch.Document != null || ksefMatch.Status == "ambiguous")
                    {
                        match = ksefMatch;
                    }
                }

                if (match.Document != null)
                {
                    WypelnijDanePoczekalni(item, match.Document);
                    item.WaitingRoomStatus = "found";
                    item.MatchStatus = match.Status;
                    item.DecreeStatus = decreeStatus;
                    DodajWarning(item, warning);
                }
                else if (match.Status == "ambiguous")
                {
                    item.WaitingRoomStatus = "ambiguous";
                    item.MatchStatus = match.Status;
                    item.DecreeStatus = "skippedAmbiguousMatch";
                    DodajWarning(item, $"{warning} Kandydaci: {ListaDoLogu(match.Candidates)}");
                }
            }
        }

        public static DocumentProcessingReport ZnajdzRaportDlaDokumentu(List<DocumentProcessingReport> manifest, DokumentDoKsiegowania dokument)
        {
            if (manifest == null || dokument == null) return null;

            var byNr = ZnajdzRaportPoNrPoczekalni(manifest, dokument.Nr);
            if (byNr != null) return byNr;

            var metadataMatches = manifest
                .Where(d => d.Source == "frontendPackage")
                .Select(d => new
                {
                    Report = d,
                    Match = InvoiceDocumentMatcher.Match(new[] { dokument }, new InvoiceMetadata
                    {
                        InvoiceNumber = d.InvoiceNumber,
                        VendorNip = d.VendorNip,
                        KsefNumber = d.KsefNumber,
                        KsefCode = d.KsefCode,
                        PdfFileName = d.PdfFileName
                    })
                })
                .Where(x => x.Match.Document != null)
                .ToList();

            if (metadataMatches.Count == 1)
            {
                WypelnijDanePoczekalni(metadataMatches[0].Report, dokument);
                metadataMatches[0].Report.WaitingRoomStatus = "found";
                metadataMatches[0].Report.MatchStatus = metadataMatches[0].Match.Status;
                return metadataMatches[0].Report;
            }

            var ksefMatches = manifest
                .Where(d => d.Source == "frontendPackage")
                .Select(d => new
                {
                    Report = d,
                    Match = InvoiceDocumentMatcher.MatchByExactKsefAndNip(new[] { dokument }, new InvoiceMetadata
                    {
                        InvoiceNumber = d.InvoiceNumber,
                        VendorNip = d.VendorNip,
                        KsefNumber = d.KsefNumber,
                        KsefCode = d.KsefCode,
                        PdfFileName = d.PdfFileName
                    })
                })
                .Where(x => x.Match.Document != null)
                .ToList();

            if (ksefMatches.Count == 1)
            {
                WypelnijDanePoczekalni(ksefMatches[0].Report, dokument);
                ksefMatches[0].Report.WaitingRoomStatus = "found";
                ksefMatches[0].Report.MatchStatus = ksefMatches[0].Match.Status;
                return ksefMatches[0].Report;
            }

            return null;
        }

        private static DocumentProcessingReport ZnajdzRaportPoNrPoczekalni(List<DocumentProcessingReport> manifest, int nr)
        {
            return manifest
                .Where(d => d.WaitingRoomNr == nr)
                .OrderBy(d => d.Source == "frontendPackage" ? 0 : 1)
                .ThenBy(d => string.IsNullOrWhiteSpace(d.PdfFileName) ? 1 : 0)
                .ThenBy(d => string.IsNullOrWhiteSpace(d.KsefCode) ? 1 : 0)
                .FirstOrDefault();
        }

        public static void WypelnijDanePoczekalni(DocumentProcessingReport item, DokumentDoKsiegowania dokument)
        {
            item.WaitingRoomId = dokument.Id.ToString();
            item.WaitingRoomNr = dokument.Nr;
            item.WaitingRoomNumber = dokument.NumerDokumentu;
            item.WaitingRoomNip = dokument.PodmiotHistoria?.NIP;
        }

        public static void DodajWarning(DocumentProcessingReport item, string message)
        {
            if (item == null || string.IsNullOrWhiteSpace(message)) return;
            if (item.Warnings == null) item.Warnings = new List<string>();
            if (!item.Warnings.Contains(message)) item.Warnings.Add(message);
        }

        private string Oczysc(string value)
        {
            string cleaned = value?.Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }

        private string OpiszRaport(DocumentProcessingReport report)
        {
            return $"source={report.Source}, invoice={report.InvoiceNumber}, nip={report.VendorNip}, ksef={(string.IsNullOrWhiteSpace(report.KsefNumber) ? "brak" : report.KsefNumber)}, ksefCode={(string.IsNullOrWhiteSpace(report.KsefCode) ? "brak" : report.KsefCode)}, pdf={(string.IsNullOrWhiteSpace(report.PdfFileName) ? "brak" : report.PdfFileName)}";
        }

        private string ListaDoLogu(IEnumerable<string> items)
        {
            if (items == null) return "brak";
            var list = items.Where(x => !string.IsNullOrWhiteSpace(x)).Take(200).ToList();
            return list.Count == 0 ? "brak" : string.Join(" || ", list);
        }
    }
}
