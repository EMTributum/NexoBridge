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
                    Warnings = new List<string> { "Wpis manifestu utworzony z zalacznika, bo nie znaleziono odpowiadajacych metadanych faktury." }
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
            return PobierzWyborDokumentowWPoczekalni(dataRozliczenia, null).Included;
        }

        public List<DokumentDoKsiegowania> PobierzDokumentyWPoczekalni(DateTime dataRozliczenia, ImportPackageContext packageContext)
        {
            return PobierzWyborDokumentowWPoczekalni(dataRozliczenia, packageContext).Included;
        }

        public WaitingRoomDocumentSelection PobierzWyborDokumentowWPoczekalni(DateTime dataRozliczenia, ImportPackageContext packageContext)
        {
            var wszystkieOczekujace = PobierzWszystkieOczekujace();
            var wybor = WaitingRoomDocumentFilter.SelectForNewMarker(wszystkieOczekujace, PobierzKontekstZnacznikaNowosci());

            _logger.LogInformation("[MANIFEST POCZEKALNIA FILTR N] wszystkie={All}; doObslugi={Included}; noweN={IncludedByNewMarker}; wyjatkiKadrowe={IncludedPayrollException}; bezN={SkippedNotNew}; amortyzacjeCzastkowe={PartialAmortization}; rachunkiPracowniczeZPodmiotem={EmployeeBillsWithSubject}",
                wybor.Total,
                wybor.Included.Count,
                wybor.IncludedByNewMarker.Count,
                wybor.IncludedPayrollException.Count,
                wybor.SkippedNotNew.Count,
                wybor.PartialAmortization.Count,
                wybor.EmployeeBillsWithSubject.Count);

            return wybor;
        }

        private List<DokumentDoKsiegowania> PobierzWszystkieOczekujace()
        {
            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            return menedzerDokumentow.Dane.Wszystkie()
                .Where(d => (int)d.StatusKsiegowy == 2)
                .ToList();
        }

        private NewDocumentMarkerContext PobierzKontekstZnacznikaNowosci()
        {
            var parametryImportu = _sfera.PodajObiektTypu<IParametryImportuKsiegowego>();
            var parametrImportu = parametryImportu?.DaneDomyslne?.Domyslny;
            var dataSystemowa = _sfera.PodajObiektTypu<IDataSystemowa>();

            return new NewDocumentMarkerContext(parametrImportu, dataSystemowa);
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
                "Dokument zostal znaleziony w Poczekalni, ale nie ma statusu N. Nie przekazano go do dekretacji.");
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
                if (manifest.Any(x => x.WaitingRoomNr == doc.Nr)) continue;

                manifest.Add(new DocumentProcessingReport
                {
                    Source = "waitingRoomExtra",
                    InvoiceNumber = doc.NumerDokumentu,
                    VendorNip = doc.PodmiotHistoria?.NIP,
                    KsefNumber = Oczysc(doc.NumerKSeF),
                    MatchStatus = payrollExceptionNumbers.Contains(doc.Nr)
                        ? "includedPayrollException"
                        : "includedByNewMarker",
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

            foreach (var item in manifest.Where(d => d.Source == "frontendPackage"))
            {
                var meta = new InvoiceMetadata
                {
                    InvoiceNumber = item.InvoiceNumber,
                    VendorNip = item.VendorNip,
                    KsefNumber = item.KsefNumber,
                    PdfFileName = item.PdfFileName
                };

                var match = InvoiceDocumentMatcher.Match(documents, meta);
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
            if (!item.Warnings.Contains(message)) item.Warnings.Add(message);
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
