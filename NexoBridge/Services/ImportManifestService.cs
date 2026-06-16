using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NexoBridge.Services
{
    public class ImportManifestService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<ImportManifestService> _logger;

        public ImportManifestService(Uchwyt sfera, ILogger<ImportManifestService> logger)
        {
            _sfera = sfera;
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
                    PdfFileName = meta.PdfFileName,
                    KsefStatus = string.IsNullOrWhiteSpace(Oczysc(meta.KsefNumber)) ? "notProvided" : "pending",
                    AttachmentStatus = string.IsNullOrWhiteSpace(meta.PdfFileName) ? "notProvided" : "pending",
                    DecreeStatus = "pending"
                };

                documents.Add(item);
            }

            foreach (var attachment in job.Attachments ?? new List<AttachmentPayload>())
            {
                bool exists = documents.Any(d =>
                    string.Equals(d.PdfFileName, attachment.FileName, StringComparison.OrdinalIgnoreCase) ||
                    (InvoiceDocumentMatcher.Normalize(d.InvoiceNumber) == InvoiceDocumentMatcher.Normalize(attachment.DocumentNumber) &&
                     InvoiceDocumentMatcher.NormalizeNip(d.VendorNip) == InvoiceDocumentMatcher.NormalizeNip(attachment.VendorNip)));

                if (exists) continue;

                documents.Add(new DocumentProcessingReport
                {
                    Source = "frontendPackage",
                    InvoiceNumber = attachment.DocumentNumber,
                    VendorNip = attachment.VendorNip,
                    PdfFileName = attachment.FileName,
                    KsefStatus = "notProvided",
                    AttachmentStatus = "pending",
                    DecreeStatus = "pending",
                    Warnings = new List<string> { "Wpis manifestu utworzony z załącznika, bo nie znaleziono odpowiadających metadanych faktury." }
                });
            }

            _logger.LogInformation("[MANIFEST] JobId={JobId}; wpisyZPaczki={Count}; dokumenty={Documents}",
                job.JobId,
                documents.Count,
                ListaDoLogu(documents.Select(OpiszRaport)));

            return documents;
        }

        public List<DokumentDoKsiegowania> PobierzDokumentyWPoczekalni()
        {
            return PobierzWszystkieOczekujace();
        }

        public List<DokumentDoKsiegowania> PobierzDokumentyWPoczekalni(DateTime dataRozliczenia)
        {
            var wszystkieOczekujace = PobierzWszystkieOczekujace();
            var wybor = WaitingRoomDocumentFilter.SelectForPeriod(wszystkieOczekujace, dataRozliczenia);

            _logger.LogInformation("[MANIFEST POCZEKALNIA FILTR] Okres={Okres}; wszystkie={All}; wOkresieDoObslugi={Included}; pozaOkresem={OutsidePeriod}; bezDaty={MissingDate}; amortyzacjeCzastkowe={PartialAmortization}; rachunkiPracowniczeZPodmiotem={EmployeeBillsWithSubject}",
                dataRozliczenia.ToString("yyyy-MM"),
                wybor.Total,
                wybor.Included.Count,
                wybor.OutsidePeriod.Count,
                wybor.MissingDate.Count,
                wybor.PartialAmortization.Count,
                wybor.EmployeeBillsWithSubject.Count);

            return wybor.Included;
        }

        private List<DokumentDoKsiegowania> PobierzWszystkieOczekujace()
        {
            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            return menedzerDokumentow.Dane.Wszystkie()
                .Where(d => (int)d.StatusKsiegowy == 2)
                .ToList();
        }

        public void AktualizujPoPoczekalni(List<DocumentProcessingReport> manifest, List<DokumentDoKsiegowania> oczekujace)
        {
            if (manifest == null) return;
            var matchedWaitingRoomNumbers = new HashSet<int>();

            foreach (var item in manifest.Where(d => d.Source == "frontendPackage"))
            {
                var meta = new InvoiceMetadata
                {
                    InvoiceNumber = item.InvoiceNumber,
                    VendorNip = item.VendorNip,
                    KsefNumber = item.KsefNumber,
                    PdfFileName = item.PdfFileName
                };

                var match = InvoiceDocumentMatcher.Match(oczekujace, meta);
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
                    DodajWarning(item, $"Nie można jednoznacznie dopasować dokumentu w Poczekalni. {match.Reason} Kandydaci: {ListaDoLogu(match.Candidates)}");
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
                if (manifest.Any(x => x.WaitingRoomNr == doc.Nr)) continue;

                manifest.Add(new DocumentProcessingReport
                {
                    Source = "waitingRoomExtra",
                    InvoiceNumber = doc.NumerDokumentu,
                    VendorNip = doc.PodmiotHistoria?.NIP,
                    KsefNumber = Oczysc(doc.NumerKSeF),
                    MatchStatus = "extraWaitingRoomDocument",
                    WaitingRoomStatus = "found",
                    WaitingRoomId = doc.Id.ToString(),
                    WaitingRoomNr = doc.Nr,
                    WaitingRoomNumber = doc.NumerDokumentu,
                    WaitingRoomNip = doc.PodmiotHistoria?.NIP,
                    KsefStatus = string.IsNullOrWhiteSpace(Oczysc(doc.NumerKSeF)) ? "notProvided" : "presentInWaitingRoom",
                    AttachmentStatus = "notProvided",
                    DecreeStatus = "pending"
                });
            }

            _logger.LogInformation("[MANIFEST POCZEKALNIA] wpisy={Count}; znalezione={Found}; nieznalezione={NotFound}; niejednoznaczne={Ambiguous}; dodatkowe={Extra}",
                manifest.Count,
                manifest.Count(d => d.WaitingRoomStatus == "found" && d.Source == "frontendPackage"),
                manifest.Count(d => d.WaitingRoomStatus == "notFound"),
                manifest.Count(d => d.WaitingRoomStatus == "ambiguous"),
                manifest.Count(d => d.Source == "waitingRoomExtra"));
        }

        public static DocumentProcessingReport ZnajdzRaportDlaDokumentu(List<DocumentProcessingReport> manifest, DokumentDoKsiegowania dokument)
        {
            if (manifest == null || dokument == null) return null;

            var byNr = manifest.FirstOrDefault(d => d.WaitingRoomNr == dokument.Nr);
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

            return null;
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
            item.Warnings.Add(message);
        }

        private string Oczysc(string value)
        {
            string cleaned = value?.Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }

        private string OpiszRaport(DocumentProcessingReport report)
        {
            return $"source={report.Source}, invoice={report.InvoiceNumber}, nip={report.VendorNip}, ksef={(string.IsNullOrWhiteSpace(report.KsefNumber) ? "brak" : report.KsefNumber)}, pdf={(string.IsNullOrWhiteSpace(report.PdfFileName) ? "brak" : report.PdfFileName)}";
        }

        private string ListaDoLogu(IEnumerable<string> items)
        {
            if (items == null) return "brak";
            var list = items.Where(x => !string.IsNullOrWhiteSpace(x)).Take(200).ToList();
            return list.Count == 0 ? "brak" : string.Join(" || ", list);
        }
    }
}

