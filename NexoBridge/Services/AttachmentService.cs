using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class AttachmentService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<AttachmentService> _logger;
        private readonly Func<ImportJob, Action<int, string>, SferaEngine> _freshSferaFactory;

        public AttachmentService(
            Uchwyt sfera,
            ILogger<AttachmentService> logger,
            Func<ImportJob, Action<int, string>, SferaEngine> freshSferaFactory = null)
        {
            _sfera = sfera;
            _logger = logger;
            _freshSferaFactory = freshSferaFactory;
        }

        public async Task PodepnijZalacznikiAsync(
            ImportJob job,
            dynamic rezultat,
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> zatwierdzone,
            List<DocumentProcessingReport> manifest,
            Func<int, string, Task> raportujPostep)
        {
            _logger.LogDebug("[ZAŁĄCZNIKI SERVICE] Uruchomiono usługę załączników dla zadania: {JobId}", job.JobId);
            if (rezultat == null || zatwierdzone == null || zatwierdzone.Count == 0) return;

            await raportujPostep(10, "Podpinanie załączników (bezpieczne dopasowanie)...");
            var bibliotekaZalacznikow = _sfera.PodajObiektTypu<InsERT.Moria.BibliotekaZalacznikow.IBibliotekaZalacznikow>();

            var menedzerowie = new Dictionary<string, dynamic> {
                { "KPiR", PobierzMenedzera("IZapisyWKPiR") },
                { "Vat", PobierzMenedzera("IZapisyWEwidencjiVAT") },
                { "Dekret", PobierzMenedzera("IDekrety") },
                { "EP", PobierzMenedzera("IZapisyWEP") }
            };

            var listaWynikow = ((System.Collections.IEnumerable)rezultat).Cast<dynamic>().ToList();
            var podpieteZalaczniki = new List<string>();
            var niepodpieteZalaczniki = new List<string>();
            var kandydaciAudytu = new List<AttachmentAuditCandidate>();
            var operacje = new List<AttachmentOperationRecord>();

            _logger.LogInformation("[ZAŁĄCZNIKI START] JobId={JobId}; baza={Database}; okres={Month:D2}/{Year}; pdf={PdfCount}; metadane={MetadataCount}; dokumenty={Zatwierdzone}; wynikowe={Wynikowe}",
                job.JobId,
                job.DatabaseName,
                job.BillingMonth,
                job.BillingYear,
                job.Attachments?.Count ?? 0,
                job.InvoicesMetadata?.Count ?? 0,
                zatwierdzone.Count,
                listaWynikow.Count);
            _logger.LogDebug("[ZAŁĄCZNIKI MENEDŻEROWIE] JobId={JobId}; menedzerowie={Menedzerowie}",
                job.JobId,
                OpiszMenedzerow(menedzerowie));
            _logger.LogDebug("[ZAŁĄCZNIKI ODEBRANE SZCZEGÓŁY] JobId={JobId}; pliki={Pliki}",
                job.JobId,
                OpiszZalaczniki(job.Attachments));
            _logger.LogDebug("[ZAŁĄCZNIKI META SZCZEGÓŁY] JobId={JobId}; metadane={Metadane}",
                job.JobId,
                OpiszMetadane(job.InvoicesMetadata));
            _logger.LogDebug("[ZAŁĄCZNIKI PAYLOAD DIAG] JobId={JobId}; pdfStat={PdfStat}; metadataStat={MetadataStat}",
                job.JobId,
                OpiszStatystykeZalacznikow(job.Attachments),
                OpiszStatystykeMetadanych(job.InvoicesMetadata));

            for (int i = 0; i < zatwierdzone.Count; i++)
            {
                var dok = zatwierdzone[i].Item1;
                string nrSystemowy = dok.NumerDokumentu ?? "";
                string nipSystemowy = dok.PodmiotHistoria?.NIP ?? "";
                var raport = ImportManifestService.ZnajdzRaportDlaDokumentu(manifest, dok);
                var operacja = UtworzOperacje(job, dok, raport, i);
                operacje.Add(operacja);

                var zalacznik = ZnajdzZalacznik(job, dok, raport, out string attachmentMatchStatus);
                if (zalacznik == null)
                {
                    bool oczekiwanoPdf = CzyOczekiwanoPdf(raport);
                    operacja.FinalStatus = oczekiwanoPdf ? "notFound" : "notProvided";
                    operacja.FailureReason = oczekiwanoPdf
                        ? "Nie znaleziono pasującego PDF w paczce."
                        : "Dla dokumentu nie przekazano PDF w metadanych.";
                    operacja.MatchStatus = attachmentMatchStatus;
                    if (oczekiwanoPdf)
                    {
                        string wpis = $"BRAK DOPASOWANIA -> {nrSystemowy} ({nipSystemowy})";
                        niepodpieteZalaczniki.Add(wpis);
                        if (raport != null)
                        {
                            raport.AttachmentStatus = "notFound";
                            ImportManifestService.DodajWarning(raport, $"Nie znaleziono załącznika PDF dla dokumentu {nrSystemowy}. Dostępne pliki: {OpiszZalaczniki(job.Attachments)}");
                        }
                    }
                    _logger.LogDebug("[ZAŁĄCZNIK BRAK] Dokument={Numer}; NIP={Nip}; status={Status}; oczekiwanoPdf={OczekiwanoPdf}",
                        nrSystemowy,
                        nipSystemowy,
                        attachmentMatchStatus,
                        oczekiwanoPdf);
                    _logger.LogDebug("[ZAŁĄCZNIK BRAK SZCZEGÓŁY] Dokument={Numer}; NIP={Nip}; dostępnePliki={Pliki}",
                        nrSystemowy,
                        nipSystemowy,
                        OpiszZalaczniki(job.Attachments));
                    continue;
                }

                if (raport != null)
                {
                    raport.AttachmentStatus = "matched";
                }
                operacja.FinalStatus = "matched";
                operacja.MatchStatus = attachmentMatchStatus;
                operacja.PdfFileName = zalacznik.FileName;
                operacja.AttachmentDocumentNumber = zalacznik.DocumentNumber;
                operacja.AttachmentVendorNip = zalacznik.VendorNip;
                operacja.AttachmentBytes = zalacznik.Content?.Length ?? 0;

                _logger.LogDebug("[ZAŁĄCZNIK DOPASOWANY] Dokument={Numer}; NIP={Nip}; plik={Plik}; match={Match}",
                    nrSystemowy,
                    nipSystemowy,
                    zalacznik.FileName,
                    attachmentMatchStatus);
                _logger.LogDebug("[ZAŁĄCZNIK DOPASOWANY SZCZEGÓŁY] Dokument={Numer}; NIP={Nip}; plik={Plik}; documentNumber={DocumentNumber}; vendorNip={VendorNip}; bytes={Bytes}; match={Match}",
                    nrSystemowy,
                    nipSystemowy,
                    zalacznik.FileName,
                    zalacznik.DocumentNumber,
                    zalacznik.VendorNip,
                    zalacznik.Content?.Length ?? 0,
                    attachmentMatchStatus);

                string bezpiecznaNazwa = nrSystemowy.Replace("/", "_").Replace("\\", "_").Replace(":", "_").Replace(" ", "_");
                bezpiecznaNazwa = string.Join("_", bezpiecznaNazwa.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(bezpiecznaNazwa)) bezpiecznaNazwa = $"Skan_{Guid.NewGuid():N}";

                string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                string rozszerzenie = Path.GetExtension(zalacznik.FileName);
                if (string.IsNullOrWhiteSpace(rozszerzenie)) rozszerzenie = ".pdf";
                string tempPath = Path.Combine(tempDir, $"{bezpiecznaNazwa}{rozszerzenie}");

                File.WriteAllBytes(tempPath, zalacznik.Content);
                string sha256 = ObliczSha256(zalacznik.Content);
                operacja.SafeAttachmentName = bezpiecznaNazwa;
                operacja.AttachmentExtension = rozszerzenie;
                operacja.TempPath = tempPath;
                operacja.Sha256 = sha256;
                _logger.LogDebug("[ZAŁĄCZNIK TEMP] Plik={Plik}; tempPath={TempPath}; bytes={Bytes}; sha256={Sha256}",
                    zalacznik.FileName,
                    tempPath,
                    zalacznik.Content?.Length ?? 0,
                    sha256);

                try
                {
                    if (i >= listaWynikow.Count)
                    {
                        string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy}) | brak wyniku dekretacji pod indeksem {i}";
                        niepodpieteZalaczniki.Add(wpis);
                        operacja.FinalStatus = "notAttached";
                        operacja.FailureReason = $"Dekretacja nie zwróciła wyniku operacji pod indeksem {i}.";
                        if (raport != null)
                        {
                            raport.AttachmentStatus = "notAttached";
                            ImportManifestService.DodajWarning(raport, "Nie podpięto załącznika, bo dekretacja nie zwróciła odpowiadającego wyniku operacji.");
                        }
                        _logger.LogDebug("[ZAŁĄCZNIK BRAK WYNIKU] Plik={Plik}; Dokument={Numer}; NIP={Nip}; listaWynikowIndex={Index}",
                            zalacznik.FileName,
                            nrSystemowy,
                            nipSystemowy,
                            i);
                        continue;
                    }

                    dynamic dokumentyWynikowe = listaWynikow[i].WynikowePoprawneZapisy;
                    if (dokumentyWynikowe == null)
                    {
                        string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                        niepodpieteZalaczniki.Add(wpis + " | brak WynikowePoprawneZapisy");
                        operacja.FinalStatus = "notAttached";
                        operacja.FailureReason = "Wynik dekretacji nie zawierał WynikowePoprawneZapisy.";
                        if (raport != null)
                        {
                            raport.AttachmentStatus = "notAttached";
                            ImportManifestService.DodajWarning(raport, "Nie podpięto załącznika, bo wynik dekretacji nie zawierał WynikowePoprawneZapisy.");
                        }
                        _logger.LogDebug("[ZAŁĄCZNIK BRAK WYNIKÓW] Plik={Plik}; Dokument={Numer}; NIP={Nip}; listaWynikowIndex={Index}",
                            zalacznik.FileName,
                            nrSystemowy,
                            nipSystemowy,
                            i);
                        continue;
                    }

                    var wynikowe = ((System.Collections.IEnumerable)dokumentyWynikowe).Cast<dynamic>().ToList();
                    operacja.ResultEntriesCount = wynikowe.Count;
                    operacja.ResultEntriesDescription = OpiszWyniki(wynikowe);
                    _logger.LogDebug("[ZAŁĄCZNIK WYNIKOWE] Plik={Plik}; Dokument={Numer}; liczba={Count}; wyniki={Wyniki}",
                        zalacznik.FileName,
                        nrSystemowy,
                        wynikowe.Count,
                        OpiszWyniki(wynikowe));

                    using (var zalacznikBO = bibliotekaZalacznikow.Utworz())
                    {
                        zalacznikBO.Wczytaj(tempPath);
                        zalacznikBO.Dane.Opis = "Oryginał ze Scanye";

                        bool podpieto = false;
                        var celeDoPowiazania = new List<AttachmentTargetRef>();
                        foreach (var wynik in wynikowe)
                        {
                            object wynikObj = (object)wynik;
                            string typWyniku = wynikObj?.GetType().Name;
                            object dokumentId = PobierzDokumentId(wynik);
                            AttachmentTargetRef cel = ZnajdzCelPowiazania(menedzerowie, (object)wynik, dok);
                            if (cel?.Entity != null)
                            {
                                cel.CanHaveLibrary = CzyMaBiblioteke(bibliotekaZalacznikow, cel.Entity, out string bibliotekaError);
                                cel.LibraryCheckError = bibliotekaError;
                                zalacznikBO.DodajPowiazanie((dynamic)cel.Entity);
                                celeDoPowiazania.Add(cel);
                                podpieto = true;
                                string wynikTypLog = typWyniku ?? "brak";
                                string encjaTypLog = cel.Entity?.GetType().FullName ?? "brak";
                                string dokumentIdLog = dokumentId?.ToString() ?? "brak";
                                _logger.LogDebug("[ZAŁĄCZNIK POWIĄZANIE] Plik={Plik}; Dokument={Numer}; wynikTyp={WynikTyp}; dokumentId={DokumentId}; encjaId={EncjaId}; encjaTyp={EncjaTyp}; menedzer={Manager}; czyMaBiblioteke={CzyMaBiblioteke}; bibliotekaBlad={BibliotekaBlad}",
                                    zalacznik.FileName,
                                    nrSystemowy,
                                    wynikTypLog,
                                    dokumentIdLog,
                                    cel.EntityId,
                                    encjaTypLog,
                                    cel.ManagerKey,
                                    FormatNullableBool(cel.CanHaveLibrary),
                                    string.IsNullOrWhiteSpace(bibliotekaError) ? "brak" : bibliotekaError);
                            }
                            else
                            {
                                operacja.MissingEntityCount++;
                                _logger.LogDebug("[ZAŁĄCZNIK BRAK ENCJI] Plik={Plik}; Dokument={Numer}; wynikTyp={WynikTyp}; dokumentId={DokumentId}",
                                    zalacznik.FileName,
                                    nrSystemowy,
                                    typWyniku,
                                    dokumentId);
                            }
                        }

                        if (!podpieto)
                        {
                            string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                            niepodpieteZalaczniki.Add(wpis + " | brak powiązań z encjami wynikowymi");
                            operacja.FinalStatus = "notAttached";
                            operacja.FailureReason = "Nie znaleziono encji wynikowych do powiązania.";
                            if (raport != null)
                            {
                                raport.AttachmentStatus = "notAttached";
                                ImportManifestService.DodajWarning(raport, "Nie podpięto załącznika, bo nie znaleziono encji wynikowych do powiązania.");
                            }
                            _logger.LogDebug("[ZAŁĄCZNIK BEZ POWIĄZAŃ] Plik={Plik}; Dokument={Numer}; NIP={Nip}", zalacznik.FileName, nrSystemowy, nipSystemowy);
                        }
                        else
                        {
                            _logger.LogDebug("[ZAŁĄCZNIK ZAPIS START] Plik={Plik}; Dokument={Numer}; NIP={Nip}; nazwaSfery={Skan}; bytes={Bytes}; sha256={Sha256}; powiazania={Powiazania}",
                                zalacznik.FileName,
                                nrSystemowy,
                                nipSystemowy,
                                bezpiecznaNazwa,
                                zalacznik.Content?.Length ?? 0,
                                sha256,
                                OpiszCelePowiazania(celeDoPowiazania));

                            bool zapisano = zalacznikBO.Zapisz();
                            int? zapisanyZalacznikId = PobierzInt(zalacznikBO.Dane, "Id");
                            string zapisanaNazwa = PobierzString(zalacznikBO.Dane, "Nazwa");
                            string zapisanyTyp = PobierzString(zalacznikBO.Dane, "Typ");
                            string invalidData = WyciagnijBledySfery(zalacznikBO);
                            operacja.TargetsCount = celeDoPowiazania.Count;
                            operacja.TargetsDescription = OpiszCelePowiazania(celeDoPowiazania);
                            operacja.SaveResult = zapisano ? "primaryTrue" : "primaryFalse";
                            operacja.SavedAttachmentId = zapisanyZalacznikId;
                            operacja.SavedAttachmentName = zapisanaNazwa;
                            operacja.SavedAttachmentType = zapisanyTyp;
                            operacja.InvalidData = invalidData;

                            _logger.LogDebug("[ZAŁĄCZNIK ZAPIS] Plik={Plik}; Dokument={Numer}; NIP={Nip}; Zapisz={Zapisz}; zalacznikId={ZalacznikId}; powiazania={Powiazania}",
                                zalacznik.FileName,
                                nrSystemowy,
                                nipSystemowy,
                                zapisano,
                                FormatNullableInt(zapisanyZalacznikId),
                                celeDoPowiazania.Count);
                            _logger.LogDebug("[ZAŁĄCZNIK ZAPIS SZCZEGÓŁY] Plik={Plik}; Dokument={Numer}; NIP={Nip}; Zapisz={Zapisz}; zalacznikId={ZalacznikId}; nazwa={Nazwa}; typ={Typ}; invalidData={InvalidData}",
                                zalacznik.FileName,
                                nrSystemowy,
                                nipSystemowy,
                                zapisano,
                                FormatNullableInt(zapisanyZalacznikId),
                                string.IsNullOrWhiteSpace(zapisanaNazwa) ? "brak" : zapisanaNazwa,
                                string.IsNullOrWhiteSpace(zapisanyTyp) ? "brak" : zapisanyTyp,
                                invalidData);

                            if (zapisano)
                            {
                                string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                                podpieteZalaczniki.Add(wpis);
                                if (raport != null)
                                {
                                    raport.AttachmentStatus = "attachedPendingVerification";
                                }
                                operacja.FinalStatus = "attachedPendingVerification";
                                kandydaciAudytu.AddRange(celeDoPowiazania.Select(cel => UtworzKandydataAudytu(
                                    job,
                                    zalacznik,
                                    raport,
                                    operacja,
                                    cel,
                                    bezpiecznaNazwa,
                                    rozszerzenie,
                                    zapisanyZalacznikId,
                                    zapisanaNazwa,
                                    zapisanyTyp,
                                    sha256,
                                    false)));

                                _logger.LogDebug("[ZAŁĄCZNIK SUKCES SZCZEGÓŁY] Sfera przyjęła zapis pliku={Plik} pod nazwą '{Skan}' dla dokumentu={Numer}; NIP={Nip}; zalacznikId={ZalacznikId}; oczekujeNaAudyt=true",
                                    zalacznik.FileName,
                                    bezpiecznaNazwa,
                                    nrSystemowy,
                                    nipSystemowy,
                                    FormatNullableInt(zapisanyZalacznikId));
                            }
                            else
                            {
                                string bledy = invalidData;
                                int zapisaneFallback = ZapiszZalacznikiOsobno(bibliotekaZalacznikow, tempPath, celeDoPowiazania, out string fallbackBledy, out List<AttachmentSaveResult> fallbackZapisane);
                                string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                                operacja.FallbackSavedCount = zapisaneFallback;
                                operacja.FallbackTotalCount = celeDoPowiazania.Count;
                                operacja.FallbackErrors = fallbackBledy;

                                if (zapisaneFallback > 0)
                                {
                                    podpieteZalaczniki.Add(wpis + $" | fallback {zapisaneFallback}/{celeDoPowiazania.Count}");
                                    operacja.SaveResult = zapisaneFallback == celeDoPowiazania.Count ? "fallbackFull" : "fallbackPartial";
                                    operacja.FinalStatus = zapisaneFallback == celeDoPowiazania.Count ? "attachedPendingVerification" : "attachedPartial";
                                    if (raport != null)
                                    {
                                        raport.AttachmentStatus = zapisaneFallback == celeDoPowiazania.Count ? "attachedPendingVerification" : "attachedPartial";
                                        if (zapisaneFallback != celeDoPowiazania.Count)
                                        {
                                            ImportManifestService.DodajWarning(raport, $"Załącznik zapisano tylko częściowo fallbackiem: {zapisaneFallback}/{celeDoPowiazania.Count}. Błędy: {fallbackBledy}");
                                        }
                                    }

                                    kandydaciAudytu.AddRange(fallbackZapisane.Select(zapis => UtworzKandydataAudytu(
                                        job,
                                        zalacznik,
                                        raport,
                                        operacja,
                                        zapis.Target,
                                        bezpiecznaNazwa,
                                        rozszerzenie,
                                        zapis.AttachmentId,
                                        zapis.AttachmentName,
                                        zapis.AttachmentType,
                                        sha256,
                                        true)));

                                    _logger.LogDebug("[ZAŁĄCZNIK SUKCES FALLBACK] Plik={Plik}; Dokument={Numer}; NIP={Nip}; zapisane={Saved}/{Total}; pierwotnyBlad={Bledy}; fallbackBledy={FallbackBledy}",
                                        zalacznik.FileName,
                                        nrSystemowy,
                                        nipSystemowy,
                                        zapisaneFallback,
                                        celeDoPowiazania.Count,
                                        bledy,
                                        fallbackBledy);
                                }
                                else
                                {
                                    niepodpieteZalaczniki.Add(wpis + $" | Zapisz=false | {bledy} | fallback=false | {fallbackBledy}");
                                    operacja.FinalStatus = "notAttached";
                                    operacja.FailureReason = $"Zapisz=false. Fallback nie zapisał żadnego powiązania. Błędy={bledy}; fallback={fallbackBledy}";
                                    if (raport != null)
                                    {
                                        raport.AttachmentStatus = "notAttached";
                                        ImportManifestService.DodajWarning(raport, $"Nie udało się zapisać załącznika w Sferze: {bledy}. Fallback: {fallbackBledy}");
                                    }
                                    _logger.LogDebug("[ZAŁĄCZNIK ZAPIS NIEUDANY] Plik={Plik}; Dokument={Numer}; NIP={Nip}; Błędy={Bledy}; fallbackBledy={FallbackBledy}",
                                        zalacznik.FileName,
                                        nrSystemowy,
                                        nipSystemowy,
                                        bledy,
                                        fallbackBledy);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                    niepodpieteZalaczniki.Add(wpis + $" | wyjątek: {ex.GetBaseException().Message}");
                    operacja.FinalStatus = "notAttached";
                    operacja.FailureReason = $"Wyjątek: {ex.GetBaseException().Message}";
                    if (raport != null)
                    {
                        raport.AttachmentStatus = "notAttached";
                        ImportManifestService.DodajWarning(raport, $"Wyjątek podczas podpinania załącznika: {ex.GetBaseException().Message}");
                    }
                    _logger.LogError(ex, "[ZAŁĄCZNIK BŁĄD] Wystąpił wyjątek podczas podpinania pliku '{Skan}' do dokumentu: {Numer}; plik={Plik}; NIP={Nip}",
                        bezpiecznaNazwa,
                        nrSystemowy,
                        zalacznik.FileName,
                        nipSystemowy);
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir);
                }
            }

            await ZweryfikujZalacznikiWSwiezejSesjiAsync(job, kandydaciAudytu, raportujPostep);
            AktualizujOperacjePoAudycie(kandydaciAudytu);
            ZalogujWynikiDokumentow(job, operacje);

            _logger.LogInformation("[ZAŁĄCZNIKI PODSUMOWANIE] JobId={JobId}; podpięte={PodpieteCount}; niepodpięte={NiepodpieteCount}; audyt={AudytCount}; audytOk={AudytOk}; audytFail={AudytFail}",
                job.JobId,
                podpieteZalaczniki.Count,
                niepodpieteZalaczniki.Count,
                kandydaciAudytu.Count,
                kandydaciAudytu.Count(k => k.Verified),
                kandydaciAudytu.Count(k => !k.Verified));
            _logger.LogDebug("[ZAŁĄCZNIKI PODSUMOWANIE SZCZEGÓŁY] JobId={JobId}; podpięte={Podpiete}; niepodpięte={Niepodpiete}",
                job.JobId,
                ListaDoLogu(podpieteZalaczniki),
                ListaDoLogu(niepodpieteZalaczniki));
        }

        private void AktualizujOperacjePoAudycie(List<AttachmentAuditCandidate> kandydaci)
        {
            if (kandydaci == null) return;

            foreach (var grupa in kandydaci.Where(k => k.OperationRecord != null).GroupBy(k => k.OperationRecord))
            {
                var operacja = grupa.Key;
                operacja.AuditTargetsCount = grupa.Count();
                operacja.AuditVerifiedCount = grupa.Count(k => k.Verified);
                operacja.AuditFailedCount = grupa.Count(k => !k.Verified);
                operacja.AuditDetails = ListaDoLogu(grupa.Select(k => $"{k.ManagerKey}/{FormatNullableInt(k.EntityId ?? k.DocumentId)}={k.VerificationStatus}; {k.VerificationDetails}"));
                operacja.FinalStatus = operacja.Report?.AttachmentStatus ?? operacja.FinalStatus;
            }
        }

        private void ZalogujWynikiDokumentow(ImportJob job, List<AttachmentOperationRecord> operacje)
        {
            if (operacje == null || operacje.Count == 0) return;

            foreach (var operacja in operacje)
            {
                string status = operacja.Report?.AttachmentStatus ?? operacja.FinalStatus ?? "unknown";
                string reason = string.IsNullOrWhiteSpace(operacja.FailureReason) ? "brak" : operacja.FailureReason;
                int targety = operacja.TargetsCount;
                int audytOk = operacja.AuditVerifiedCount;
                int audytTotal = operacja.AuditTargetsCount;
                string pdf = string.IsNullOrWhiteSpace(operacja.PdfFileName) ? "brak" : operacja.PdfFileName;

                if (CzyStatusZalacznikaWymagaUwagi(status))
                {
                    _logger.LogWarning("[ZAŁĄCZNIK DOKUMENT] JobId={JobId}; baza={Database}; dokument={Dokument}; NIP={Nip}; PDF={Pdf}; status={Status}; targety={Targety}; audyt={AuditOk}/{AuditTotal}; powod={Reason}",
                        operacja.JobId,
                        operacja.DatabaseName,
                        operacja.DocumentNumber,
                        operacja.SystemNip,
                        pdf,
                        status,
                        targety,
                        audytOk,
                        audytTotal,
                        reason);
                }
                else
                {
                    _logger.LogInformation("[ZAŁĄCZNIK DOKUMENT] JobId={JobId}; baza={Database}; dokument={Dokument}; NIP={Nip}; PDF={Pdf}; status={Status}; targety={Targety}; audyt={AuditOk}/{AuditTotal}",
                        operacja.JobId,
                        operacja.DatabaseName,
                        operacja.DocumentNumber,
                        operacja.SystemNip,
                        pdf,
                        status,
                        targety,
                        audytOk,
                        audytTotal);
                }

                _logger.LogDebug("[ZAŁĄCZNIK DOKUMENT DIAG] {Diag}", OpiszOperacjeDiagnostycznie(operacja));
            }
        }

        private int ZapiszZalacznikiOsobno(
            dynamic bibliotekaZalacznikow,
            string tempPath,
            IEnumerable<AttachmentTargetRef> cele,
            out string bledy,
            out List<AttachmentSaveResult> zapisaneCele)
        {
            var errors = new List<string>();
            zapisaneCele = new List<AttachmentSaveResult>();
            int saved = 0;

            foreach (var cel in cele ?? Enumerable.Empty<AttachmentTargetRef>())
            {
                try
                {
                    using (var pojedynczyBO = bibliotekaZalacznikow.Utworz())
                    {
                        pojedynczyBO.Wczytaj(tempPath);
                        pojedynczyBO.Dane.Opis = "Oryginał ze Scanye";
                        pojedynczyBO.DodajPowiazanie((dynamic)cel.Entity);

                        if (pojedynczyBO.Zapisz())
                        {
                            int? fallbackAttachmentId = PobierzInt(pojedynczyBO.Dane, "Id");
                            string fallbackAttachmentName = PobierzString(pojedynczyBO.Dane, "Nazwa");
                            string fallbackAttachmentType = PobierzString(pojedynczyBO.Dane, "Typ");
                            saved++;
                            zapisaneCele.Add(new AttachmentSaveResult
                            {
                                Target = cel,
                                AttachmentId = fallbackAttachmentId,
                                AttachmentName = fallbackAttachmentName,
                                AttachmentType = fallbackAttachmentType
                            });
                            _logger.LogDebug("[ZAŁĄCZNIK FALLBACK WYNIK] target={Target}; Zapisz=true; zalacznikId={ZalacznikId}; nazwa={Nazwa}; typ={Typ}",
                                OpiszCelPowiazania(cel),
                                FormatNullableInt(fallbackAttachmentId),
                                fallbackAttachmentName ?? "brak",
                                fallbackAttachmentType ?? "brak");
                        }
                        else
                        {
                            errors.Add($"{OpiszCelPowiazania(cel)}: {WyciagnijBledySfery(pojedynczyBO)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{OpiszCelPowiazania(cel)}: {ex.GetBaseException().Message}");
                }
            }

            bledy = errors.Count == 0 ? "brak" : string.Join(" | ", errors);
            return saved;
        }

        private async Task ZweryfikujZalacznikiWSwiezejSesjiAsync(
            ImportJob job,
            List<AttachmentAuditCandidate> kandydaci,
            Func<int, string, Task> raportujPostep)
        {
            if (kandydaci == null || kandydaci.Count == 0)
            {
                return;
            }

            await raportujPostep(70, "Weryfikacja zapisanych załączników...");

            if (_freshSferaFactory == null)
            {
                string reason = "Brak fabryki świeżej sesji Sfery dla audytu załączników.";
                OznaczAudytNieudany(kandydaci, "attachedUnverified", reason);
                _logger.LogDebug("[ZAŁĄCZNIK AUDYT POMINIĘTY] JobId={JobId}; powód={Reason}; kandydaci={Count}",
                    job.JobId,
                    reason,
                    kandydaci.Count);
                AktualizujRaportyPoAudycie(kandydaci);
                return;
            }

            SferaEngine auditEngine = null;
            try
            {
                auditEngine = _freshSferaFactory(job, (procent, opis) =>
                {
                    int mappedPercent = 70 + (int)Math.Round(Math.Max(0, Math.Min(100, procent)) * 0.15m, MidpointRounding.AwayFromZero);
                    raportujPostep(mappedPercent, "Weryfikacja załączników: " + opis).GetAwaiter().GetResult();
                });

                if (auditEngine?.Sfera == null)
                {
                    throw new InvalidOperationException("Fabryka świeżej sesji Sfery nie zwróciła uchwytu.");
                }

                var auditSfera = auditEngine.Sfera;
                var bibliotekaZalacznikow = auditSfera.PodajObiektTypu<InsERT.Moria.BibliotekaZalacznikow.IBibliotekaZalacznikow>();
                var menedzerowie = new Dictionary<string, dynamic> {
                    { "KPiR", PobierzMenedzera("IZapisyWKPiR", auditSfera) },
                    { "Vat", PobierzMenedzera("IZapisyWEwidencjiVAT", auditSfera) },
                    { "Dekret", PobierzMenedzera("IDekrety", auditSfera) },
                    { "EP", PobierzMenedzera("IZapisyWEP", auditSfera) }
                };

                _logger.LogInformation("[ZAŁĄCZNIK AUDYT START] JobId={JobId}; kandydaci={Count}",
                    job.JobId,
                    kandydaci.Count);
                _logger.LogDebug("[ZAŁĄCZNIK AUDYT START SZCZEGÓŁY] JobId={JobId}; cele={Cele}",
                    job.JobId,
                    ListaDoLogu(kandydaci.Select(OpiszKandydataAudytu)));

                for (int i = 0; i < kandydaci.Count; i++)
                {
                    var kandydat = kandydaci[i];
                    if (!menedzerowie.TryGetValue(kandydat.ManagerKey ?? "", out dynamic manager) || manager == null)
                    {
                        kandydat.VerificationStatus = "managerNotAvailable";
                        kandydat.VerificationDetails = $"Nie udało się pobrać menedżera {kandydat.ManagerKey}.";
                        _logger.LogDebug("[ZAŁĄCZNIK AUDYT BRAK MENEDŻERA] {Kandydat}; szczegoly={Szczegoly}",
                            OpiszKandydataAudytu(kandydat),
                            kandydat.VerificationDetails);
                        continue;
                    }

                    object encja = ZnajdzFizycznaEncje(manager, kandydat.EntityId ?? kandydat.DocumentId);
                    if (encja == null)
                    {
                        kandydat.VerificationStatus = "entityNotFound";
                        kandydat.VerificationDetails = $"Nie znaleziono encji {kandydat.ManagerKey} po Id={FormatNullableInt(kandydat.EntityId ?? kandydat.DocumentId)}.";
                        _logger.LogDebug("[ZAŁĄCZNIK AUDYT BRAK ENCJI] {Kandydat}; szczegoly={Szczegoly}",
                            OpiszKandydataAudytu(kandydat),
                            kandydat.VerificationDetails);
                        continue;
                    }

                    bool? czyMaBiblioteke = CzyMaBiblioteke(bibliotekaZalacznikow, encja, out string bibliotekaBlad);
                    var widoczneZalaczniki = PobierzZalaczniki(bibliotekaZalacznikow, encja, out string odczytBlad);
                    var dopasowany = widoczneZalaczniki.FirstOrDefault(z => CzyZalacznikPasujeDoKandydata(z, kandydat));

                    if (dopasowany != null)
                    {
                        kandydat.Verified = true;
                        kandydat.VerificationStatus = "verified";
                        kandydat.VerificationDetails = $"Potwierdzono załącznik {dopasowany.DisplayName}.";
                        _logger.LogDebug("[ZAŁĄCZNIK AUDYT OK] {Kandydat}; czyMaBiblioteke={CzyMaBiblioteke}; znaleziony={Znaleziony}; wszystkie={Wszystkie}",
                            OpiszKandydataAudytu(kandydat),
                            FormatNullableBool(czyMaBiblioteke),
                            dopasowany.DisplayName,
                            OpiszDeskryptoryZalacznikow(widoczneZalaczniki));
                    }
                    else
                    {
                        kandydat.VerificationStatus = "notVisibleAfterSave";
                        kandydat.VerificationDetails = $"Nie widać oczekiwanego załącznika po świeżym odczycie. czyMaBiblioteke={FormatNullableBool(czyMaBiblioteke)}, bibliotekaBlad={bibliotekaBlad ?? "brak"}, odczytBlad={odczytBlad ?? "brak"}, widoczne={OpiszDeskryptoryZalacznikow(widoczneZalaczniki)}.";
                        _logger.LogDebug("[ZAŁĄCZNIK AUDYT BRAK WIDOCZNOŚCI] {Kandydat}; {Szczegoly}",
                            OpiszKandydataAudytu(kandydat),
                            kandydat.VerificationDetails);
                    }

                    if ((i + 1) % 10 == 0 || i == kandydaci.Count - 1)
                    {
                        int local = 85 + (int)Math.Round((i + 1) * 10m / kandydaci.Count, MidpointRounding.AwayFromZero);
                        await raportujPostep(Math.Min(95, local), $"Weryfikacja załączników: {i + 1}/{kandydaci.Count}");
                    }
                }
            }
            catch (Exception ex)
            {
                string reason = ex.GetBaseException().Message;
                OznaczAudytNieudany(kandydaci, "verificationFailed", reason);
                _logger.LogError(ex, "[ZAŁĄCZNIK AUDYT BŁĄD] JobId={JobId}; Nie udało się zweryfikować załączników po świeżej sesji Sfery.", job.JobId);
            }
            finally
            {
                auditEngine?.Dispose();
            }

            AktualizujRaportyPoAudycie(kandydaci);
            _logger.LogInformation("[ZAŁĄCZNIK AUDYT PODSUMOWANIE] JobId={JobId}; kandydaci={Count}; ok={Ok}; fail={Fail}",
                job.JobId,
                kandydaci.Count,
                kandydaci.Count(k => k.Verified),
                kandydaci.Count(k => !k.Verified));
            _logger.LogDebug("[ZAŁĄCZNIK AUDYT PODSUMOWANIE SZCZEGÓŁY] JobId={JobId}; wyniki={Wyniki}",
                job.JobId,
                ListaDoLogu(kandydaci.Select(k => $"{OpiszKandydataAudytu(k)} => {k.VerificationStatus}: {k.VerificationDetails}")));

            await raportujPostep(95, "Weryfikacja załączników zakończona.");
        }

        private void OznaczAudytNieudany(List<AttachmentAuditCandidate> kandydaci, string status, string reason)
        {
            foreach (var kandydat in kandydaci)
            {
                if (kandydat.Verified)
                {
                    continue;
                }

                kandydat.VerificationStatus = status;
                kandydat.VerificationDetails = reason;
            }
        }

        private void AktualizujRaportyPoAudycie(List<AttachmentAuditCandidate> kandydaci)
        {
            foreach (var grupa in kandydaci.Where(k => k.Report != null).GroupBy(k => k.Report))
            {
                var raport = grupa.Key;
                bool bylCzesciowyFallback = raport.AttachmentStatus == "attachedPartial";

                if (grupa.All(k => k.Verified))
                {
                    if (bylCzesciowyFallback)
                    {
                        ImportManifestService.DodajWarning(raport, "Zweryfikowano zapisane powiązania załącznika, ale pierwotny zapis był tylko częściowy.");
                    }
                    else
                    {
                        raport.AttachmentStatus = "verified";
                    }
                    continue;
                }

                string problemy = ListaDoLogu(grupa.Where(k => !k.Verified).Select(k => $"{k.ManagerKey}/{FormatNullableInt(k.EntityId ?? k.DocumentId)}: {k.VerificationStatus} - {k.VerificationDetails}"));
                if (grupa.Any(k => k.Verified))
                {
                    raport.AttachmentStatus = "attachedPartial";
                    ImportManifestService.DodajWarning(raport, $"Załącznik jest widoczny tylko na części zapisów wynikowych. Problemy: {problemy}");
                    continue;
                }

                raport.AttachmentStatus = grupa.All(k => k.VerificationStatus == "verificationFailed" || k.VerificationStatus == "attachedUnverified")
                    ? grupa.First().VerificationStatus
                    : "notVisibleAfterSave";
                ImportManifestService.DodajWarning(raport, $"Sfera przyjęła zapis załącznika, ale audyt nie potwierdził jego widoczności. Problemy: {problemy}");
            }
        }

        private AttachmentAuditCandidate UtworzKandydataAudytu(
            ImportJob job,
            AttachmentPayload zalacznik,
            DocumentProcessingReport raport,
            AttachmentOperationRecord operacja,
            AttachmentTargetRef cel,
            string bezpiecznaNazwa,
            string rozszerzenie,
            int? zapisanyZalacznikId,
            string zapisanaNazwa,
            string zapisanyTyp,
            string sha256,
            bool savedByFallback)
        {
            return new AttachmentAuditCandidate
            {
                JobId = job.JobId,
                FileName = zalacznik.FileName,
                SafeName = bezpiecznaNazwa,
                Extension = rozszerzenie,
                ContentLength = zalacznik.Content?.Length ?? 0,
                Sha256 = sha256,
                Report = raport,
                OperationRecord = operacja,
                InvoiceNumber = raport?.InvoiceNumber,
                VendorNip = raport?.VendorNip,
                WaitingRoomNumber = raport?.WaitingRoomNumber,
                ManagerKey = cel.ManagerKey,
                ResultType = cel.ResultType,
                DocumentId = cel.DocumentId,
                EntityId = cel.EntityId,
                EntityType = cel.EntityType,
                SavedAttachmentId = zapisanyZalacznikId,
                SavedAttachmentName = zapisanaNazwa,
                SavedAttachmentType = zapisanyTyp,
                SavedByFallback = savedByFallback
            };
        }

        private AttachmentPayload ZnajdzZalacznik(ImportJob job, DokumentDoKsiegowania dok, DocumentProcessingReport raport, out string matchStatus)
        {
            matchStatus = "none";
            if (job.Attachments == null || job.Attachments.Count == 0) return null;

            if (raport != null && !string.IsNullOrWhiteSpace(raport.PdfFileName))
            {
                var byName = job.Attachments.FirstOrDefault(z => string.Equals(z.FileName, raport.PdfFileName, StringComparison.OrdinalIgnoreCase));
                if (byName != null)
                {
                    matchStatus = "pdfFileName";
                    return byName;
                }
            }

            var matches = job.Attachments
                .Select(z => new { Attachment = z, Match = InvoiceDocumentMatcher.Match(new[] { dok }, new InvoiceMetadata { InvoiceNumber = z.DocumentNumber, VendorNip = z.VendorNip }) })
                .Where(x => x.Match.Document != null)
                .ToList();

            if (matches.Count == 1)
            {
                matchStatus = matches[0].Match.Status;
                return matches[0].Attachment;
            }

            if (matches.Count > 1 && raport != null)
            {
                raport.AttachmentStatus = "ambiguous";
                ImportManifestService.DodajWarning(raport, "Nie podpięto załącznika, bo wiele plików pasuje do tego samego dokumentu.");
            }

            return null;
        }

        private string OpiszZalaczniki(IEnumerable<AttachmentPayload> zalaczniki)
        {
            if (zalaczniki == null) return "brak";

            var opisy = zalaczniki
                .Take(200)
                .Select(z => $"fileName={z.FileName}, documentNumber={z.DocumentNumber}, vendorNip={z.VendorNip}, bytes={z.Content?.Length ?? 0}")
                .ToList();

            return ListaDoLogu(opisy);
        }

        private string OpiszMetadane(IEnumerable<InvoiceMetadata> metadane)
        {
            if (metadane == null) return "brak";

            var opisy = metadane
                .Take(200)
                .Select(m => $"invoiceNumber={m.InvoiceNumber}, vendorNip={m.VendorNip}, ksefNumber={m.KsefNumber}, ksefCode={m.KsefCode}, pdfFileName={m.PdfFileName}")
                .ToList();

            return ListaDoLogu(opisy);
        }

        private string OpiszStatystykeZalacznikow(IEnumerable<AttachmentPayload> zalaczniki)
        {
            var list = (zalaczniki ?? Enumerable.Empty<AttachmentPayload>()).ToList();
            if (list.Count == 0) return "count=0";

            var duplicateFileNames = list
                .Where(a => !string.IsNullOrWhiteSpace(a.FileName))
                .GroupBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}x{g.Count()}");

            var duplicateInvoiceKeys = list
                .GroupBy(a => $"{InvoiceDocumentMatcher.NormalizeNip(a.VendorNip)}|{InvoiceDocumentMatcher.Normalize(a.DocumentNumber)}")
                .Where(g => !string.IsNullOrWhiteSpace(g.Key.Trim('|')) && g.Count() > 1)
                .Select(g => $"{g.Key}x{g.Count()}");

            return $"count={list.Count}; bytes={list.Sum(a => (long)(a.Content?.Length ?? 0))}; emptyContent={list.Count(a => a.Content == null || a.Content.Length == 0)}; missingFileName={list.Count(a => string.IsNullOrWhiteSpace(a.FileName))}; missingDocumentNumber={list.Count(a => string.IsNullOrWhiteSpace(a.DocumentNumber))}; missingVendorNip={list.Count(a => string.IsNullOrWhiteSpace(a.VendorNip))}; duplicateFileNames={ListaDoLogu(duplicateFileNames)}; duplicateInvoiceKeys={ListaDoLogu(duplicateInvoiceKeys)}";
        }

        private string OpiszStatystykeMetadanych(IEnumerable<InvoiceMetadata> metadane)
        {
            var list = (metadane ?? Enumerable.Empty<InvoiceMetadata>()).ToList();
            if (list.Count == 0) return "count=0";

            var duplicateInvoiceKeys = list
                .GroupBy(m => $"{InvoiceDocumentMatcher.NormalizeNip(m.VendorNip)}|{InvoiceDocumentMatcher.Normalize(m.InvoiceNumber)}")
                .Where(g => !string.IsNullOrWhiteSpace(g.Key.Trim('|')) && g.Count() > 1)
                .Select(g => $"{g.Key}x{g.Count()}");

            return $"count={list.Count}; withPdf={list.Count(m => !string.IsNullOrWhiteSpace(m.PdfFileName))}; withKsef={list.Count(m => !string.IsNullOrWhiteSpace(m.KsefNumber))}; withKsefCode={list.Count(m => !string.IsNullOrWhiteSpace(m.KsefCode))}; missingInvoiceNumber={list.Count(m => string.IsNullOrWhiteSpace(m.InvoiceNumber))}; missingVendorNip={list.Count(m => string.IsNullOrWhiteSpace(m.VendorNip))}; duplicateInvoiceKeys={ListaDoLogu(duplicateInvoiceKeys)}";
        }

        private string OpiszWyniki(IEnumerable<dynamic> wyniki)
        {
            if (wyniki == null) return "brak";

            var opisy = wyniki
                .Take(50)
                .Select(w => $"typ={w?.GetType().Name}, dokumentId={PobierzDokumentId(w)}")
                .ToList();

            return ListaDoLogu(opisy);
        }

        private object PobierzDokumentId(dynamic wynik)
        {
            try { return wynik?.DokumentId; } catch { return null; }
        }

        private string WyciagnijBledySfery(dynamic obiektBO)
        {
            try
            {
                var invalidData = (System.Collections.IEnumerable)obiektBO.InvalidData;
                if (invalidData != null)
                {
                    var bledy = invalidData.Cast<dynamic>().Select(e =>
                    {
                        try { return (string)e.Komunikat ?? (string)e.Tresc ?? (string)e.Opis; }
                        catch { return e.ToString(); }
                    }).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

                    if (bledy.Any()) return string.Join(" | ", bledy);
                }
            }
            catch { }

            return "brak szczegółów InvalidData";
        }

        private string ListaDoLogu(IEnumerable<string> items)
        {
            if (items == null) return "brak";

            var lista = items.Where(x => !string.IsNullOrWhiteSpace(x)).Take(200).ToList();
            return lista.Count == 0 ? "brak" : string.Join(" || ", lista);
        }

        private AttachmentOperationRecord UtworzOperacje(ImportJob job, DokumentDoKsiegowania dok, DocumentProcessingReport raport, int index)
        {
            return new AttachmentOperationRecord
            {
                JobId = job.JobId,
                DatabaseName = job.DatabaseName,
                BillingPeriod = $"{job.BillingYear}-{job.BillingMonth:D2}",
                Index = index,
                DocumentNumber = dok?.NumerDokumentu ?? "",
                SystemNip = dok?.PodmiotHistoria?.NIP ?? "",
                WaitingRoomNr = raport?.WaitingRoomNr,
                WaitingRoomId = raport?.WaitingRoomId,
                WaitingRoomNumber = raport?.WaitingRoomNumber,
                WaitingRoomNip = raport?.WaitingRoomNip,
                ManifestInvoiceNumber = raport?.InvoiceNumber,
                ManifestVendorNip = raport?.VendorNip,
                ManifestPdfFileName = raport?.PdfFileName,
                KsefNumber = raport?.KsefNumber,
                KsefCode = raport?.KsefCode,
                WaitingRoomStatus = raport?.WaitingRoomStatus,
                MatchStatus = raport?.MatchStatus,
                DecreeStatus = raport?.DecreeStatus,
                AttachmentStatusBefore = raport?.AttachmentStatus,
                FinalStatus = raport?.AttachmentStatus ?? "notChecked",
                Report = raport
            };
        }

        private bool CzyOczekiwanoPdf(DocumentProcessingReport raport)
        {
            return raport != null &&
                (raport.AttachmentStatus == "pending" || !string.IsNullOrWhiteSpace(raport.PdfFileName));
        }

        private bool CzyStatusZalacznikaWymagaUwagi(string status)
        {
            return status == "notFound" ||
                status == "ambiguous" ||
                status == "notAttached" ||
                status == "attachedPartial" ||
                status == "attachedUnverified" ||
                status == "notVisibleAfterSave" ||
                status == "verificationFailed";
        }

        private string OpiszOperacjeDiagnostycznie(AttachmentOperationRecord op)
        {
            if (op == null) return "brak";

            return $"jobId={op.JobId}; baza={op.DatabaseName}; okres={op.BillingPeriod}; index={op.Index}; " +
                $"system[dokument={op.DocumentNumber}, nip={op.SystemNip}]; " +
                $"manifest[invoice={op.ManifestInvoiceNumber}, nip={op.ManifestVendorNip}, pdf={op.ManifestPdfFileName}, ksef={op.KsefNumber}, ksefCode={op.KsefCode}]; " +
                $"poczekalnia[nr={FormatNullableInt(op.WaitingRoomNr)}, id={op.WaitingRoomId}, numer={op.WaitingRoomNumber}, nip={op.WaitingRoomNip}, status={op.WaitingRoomStatus}]; " +
                $"dopasowanie[match={op.MatchStatus}, attachmentBefore={op.AttachmentStatusBefore}, decree={op.DecreeStatus}]; " +
                $"pdf[file={op.PdfFileName}, documentNumber={op.AttachmentDocumentNumber}, vendorNip={op.AttachmentVendorNip}, bytes={op.AttachmentBytes}, sha256={op.Sha256}, safeName={op.SafeAttachmentName}, ext={op.AttachmentExtension}, tempPath={op.TempPath}]; " +
                $"wyniki[resultEntries={op.ResultEntriesCount}, entries={op.ResultEntriesDescription}, missingEntities={op.MissingEntityCount}]; " +
                $"targety[count={op.TargetsCount}, list={op.TargetsDescription}]; " +
                $"zapis[result={op.SaveResult}, attachmentId={FormatNullableInt(op.SavedAttachmentId)}, name={op.SavedAttachmentName}, type={op.SavedAttachmentType}, invalidData={op.InvalidData}, fallback={op.FallbackSavedCount}/{op.FallbackTotalCount}, fallbackErrors={op.FallbackErrors}]; " +
                $"audyt[ok={op.AuditVerifiedCount}/{op.AuditTargetsCount}, failed={op.AuditFailedCount}, details={op.AuditDetails}]; " +
                $"final[status={op.Report?.AttachmentStatus ?? op.FinalStatus}, reason={op.FailureReason}]";
        }

        private string OpiszMenedzerow(Dictionary<string, dynamic> menedzerowie)
        {
            if (menedzerowie == null) return "brak";
            return ListaDoLogu(menedzerowie.Select(kv => $"{kv.Key}={(kv.Value == null ? "brak" : kv.Value.GetType().FullName)}"));
        }

        private AttachmentTargetRef ZnajdzCelPowiazania(Dictionary<string, dynamic> menedzerowie, dynamic wynik, DokumentDoKsiegowania dokumentZrodlowy)
        {
            string typWyniku = wynik?.GetType().Name;
            object dokumentIdObj = PobierzDokumentId(wynik);
            int? dokumentId = KonwertujNaInt(dokumentIdObj);
            object encja = ZnajdzEncje(menedzerowie, wynik, dokumentZrodlowy);
            if (encja == null)
            {
                return null;
            }

            return new AttachmentTargetRef
            {
                Entity = encja,
                ManagerKey = PobierzKluczMenedzeraDlaWyniku(typWyniku),
                ResultType = typWyniku,
                DocumentId = dokumentId,
                EntityId = PobierzInt(encja, "Id") ?? dokumentId,
                EntityType = encja.GetType().FullName
            };
        }

        private string PobierzKluczMenedzeraDlaWyniku(string typWyniku)
        {
            if (ZawieraTyp(typWyniku, "VAT")) return "Vat";
            if (ZawieraTyp(typWyniku, "KPiR")) return "KPiR";
            if (ZawieraTyp(typWyniku, "Dekret")) return "Dekret";
            if (ZawieraTyp(typWyniku, "EP")) return "EP";
            return "Nieznany";
        }

        private string OpiszCelePowiazania(IEnumerable<AttachmentTargetRef> cele)
        {
            return ListaDoLogu((cele ?? Enumerable.Empty<AttachmentTargetRef>()).Select(OpiszCelPowiazania));
        }

        private string OpiszCelPowiazania(AttachmentTargetRef cel)
        {
            if (cel == null) return "brak";
            return $"manager={cel.ManagerKey}, resultType={cel.ResultType}, documentId={FormatNullableInt(cel.DocumentId)}, entityId={FormatNullableInt(cel.EntityId)}, entityType={cel.EntityType}, czyMaBiblioteke={FormatNullableBool(cel.CanHaveLibrary)}";
        }

        private string OpiszKandydataAudytu(AttachmentAuditCandidate kandydat)
        {
            if (kandydat == null) return "brak";
            return $"plik={kandydat.FileName}, safeName={kandydat.SafeName}, zalacznikId={FormatNullableInt(kandydat.SavedAttachmentId)}, manager={kandydat.ManagerKey}, resultType={kandydat.ResultType}, documentId={FormatNullableInt(kandydat.DocumentId)}, entityId={FormatNullableInt(kandydat.EntityId)}, invoice={kandydat.InvoiceNumber}, nip={kandydat.VendorNip}, bytes={kandydat.ContentLength}, sha256={kandydat.Sha256}, fallback={kandydat.SavedByFallback}";
        }

        private bool? CzyMaBiblioteke(object bibliotekaZalacznikow, object encja, out string error)
        {
            error = null;
            try
            {
                object result = InvokeBestMethod(bibliotekaZalacznikow, "CzyMaBiblioteke", encja);
                return result == null ? null : Convert.ToBoolean(result);
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return null;
            }
        }

        private List<AttachmentDescriptor> PobierzZalaczniki(object bibliotekaZalacznikow, object encja, out string error)
        {
            error = null;
            var wynik = new List<AttachmentDescriptor>();
            try
            {
                object result = InvokeBestMethod(bibliotekaZalacznikow, "PodajZalaczniki", encja);
                if (result is not IEnumerable enumerable)
                {
                    return wynik;
                }

                foreach (object item in enumerable)
                {
                    object dane = PobierzWlasciwosc(item, "Dane") ?? item;
                    string nazwa = PobierzString(dane, "Nazwa");
                    string typ = PobierzString(dane, "Typ");
                    int? id = PobierzInt(dane, "Id") ?? PobierzInt(item, "Id");
                    string opis = PobierzString(dane, "Opis");

                    wynik.Add(new AttachmentDescriptor
                    {
                        Id = id,
                        Name = nazwa,
                        Type = typ,
                        Description = opis,
                        DisplayName = $"{(string.IsNullOrWhiteSpace(nazwa) ? "brak" : nazwa)}.{(string.IsNullOrWhiteSpace(typ) ? "brak" : typ)}#{FormatNullableInt(id)}"
                    });
                }
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
            }

            return wynik;
        }

        private bool CzyZalacznikPasujeDoKandydata(AttachmentDescriptor zalacznik, AttachmentAuditCandidate kandydat)
        {
            if (zalacznik == null || kandydat == null)
            {
                return false;
            }

            if (kandydat.SavedAttachmentId.HasValue && zalacznik.Id == kandydat.SavedAttachmentId)
            {
                return true;
            }

            var oczekiwaneNazwy = new HashSet<string>(
                new[]
                {
                    NormalizujNazweZalacznika(kandydat.SafeName),
                    NormalizujNazweZalacznika(kandydat.SafeName + kandydat.Extension),
                    NormalizujNazweZalacznika(kandydat.SavedAttachmentName),
                    NormalizujNazweZalacznika(kandydat.FileName),
                    NormalizujNazweZalacznika(Path.GetFileNameWithoutExtension(kandydat.FileName))
                }.Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            string nazwaWidoczna = NormalizujNazweZalacznika(zalacznik.Name);
            if (!oczekiwaneNazwy.Contains(nazwaWidoczna))
            {
                return false;
            }

            string oczekiwanyTyp = NormalizujTypZalacznika(kandydat.SavedAttachmentType)
                ?? NormalizujTypZalacznika(kandydat.Extension)
                ?? NormalizujTypZalacznika(Path.GetExtension(kandydat.FileName));
            string widocznyTyp = NormalizujTypZalacznika(zalacznik.Type);

            return string.IsNullOrWhiteSpace(widocznyTyp)
                || string.IsNullOrWhiteSpace(oczekiwanyTyp)
                || string.Equals(widocznyTyp, oczekiwanyTyp, StringComparison.OrdinalIgnoreCase);
        }

        private string OpiszDeskryptoryZalacznikow(IEnumerable<AttachmentDescriptor> zalaczniki)
        {
            return ListaDoLogu((zalaczniki ?? Enumerable.Empty<AttachmentDescriptor>()).Select(z => z.DisplayName));
        }

        private string ObliczSha256(byte[] content)
        {
            if (content == null || content.Length == 0)
            {
                return "brak";
            }

            return Convert.ToHexString(SHA256.HashData(content));
        }

        private string FormatNullableBool(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "brak";
        }

        private string FormatNullableInt(int? value)
        {
            return value.HasValue ? value.Value.ToString() : "brak";
        }

        private object PobierzWlasciwosc(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            try
            {
                return source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private string PobierzString(object source, string propertyName)
        {
            object value = PobierzWlasciwosc(source, propertyName);
            return value?.ToString();
        }

        private int? PobierzInt(object source, string propertyName)
        {
            return KonwertujNaInt(PobierzWlasciwosc(source, propertyName));
        }

        private int? KonwertujNaInt(object value)
        {
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return null;
            }
        }

        private object InvokeBestMethod(object target, string methodName, params object[] args)
        {
            if (target == null)
            {
                return null;
            }

            foreach (MethodInfo method in target.GetType().GetMethods().Where(m => m.Name == methodName && m.GetParameters().Length == args.Length))
            {
                ParameterInfo[] parameters = method.GetParameters();
                bool accepted = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (!CanAccept(parameters[i].ParameterType, args[i]))
                    {
                        accepted = false;
                        break;
                    }
                }

                if (accepted)
                {
                    return method.Invoke(target, args);
                }
            }

            throw new MissingMethodException(target.GetType().FullName, $"{methodName}({args.Length})");
        }

        private bool CanAccept(Type parameterType, object value)
        {
            if (value == null)
            {
                return !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;
            }

            return parameterType.IsInstanceOfType(value);
        }

        private string NormalizujNazweZalacznika(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            string fileName = Path.GetFileNameWithoutExtension(value.Trim());
            return string.IsNullOrWhiteSpace(fileName) ? value.Trim() : fileName.Trim();
        }

        private string NormalizujTypZalacznika(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().TrimStart('.').ToLowerInvariant();
        }

        private object ZnajdzEncje(Dictionary<string, dynamic> menedzerowie, dynamic wynik, DokumentDoKsiegowania dokumentZrodlowy)
        {
            string typ = wynik.GetType().Name;
            dynamic mgr = null;

            if (ZawieraTyp(typ, "VAT"))
            {
                return ZnajdzEncjeVat(menedzerowie["Vat"], wynik, dokumentZrodlowy);
            }

            if (ZawieraTyp(typ, "KPiR")) mgr = menedzerowie["KPiR"];
            else if (ZawieraTyp(typ, "Dekret")) mgr = menedzerowie["Dekret"];
            else if (ZawieraTyp(typ, "EP")) mgr = menedzerowie["EP"];

            return ZnajdzFizycznaEncje(mgr, wynik.DokumentId);
        }

        private object ZnajdzEncjeVat(dynamic mgrVat, dynamic wynik, DokumentDoKsiegowania dokumentZrodlowy)
        {
            object dokumentId = PobierzDokumentId(wynik);
            object encja = ZnajdzFizycznaEncje(mgrVat, dokumentId);
            if (encja != null)
            {
                return encja;
            }

            encja = PobierzVatZDokumentuZrodlowego(dokumentZrodlowy, dokumentId);
            if (encja != null)
            {
                _logger.LogDebug("[ZAŁĄCZNIK VAT FALLBACK] Znaleziono zapis VAT przez relację DokumentDoKsiegowania. Dokument={Numer}; wynikDokumentId={DokumentId}; encjaTyp={EncjaTyp}",
                    dokumentZrodlowy?.NumerDokumentu,
                    dokumentId,
                    encja.GetType().FullName);
                return encja;
            }

            encja = ZnajdzVatPoPowiazaniuZDdk(mgrVat, dokumentZrodlowy, dokumentId);
            if (encja != null)
            {
                _logger.LogDebug("[ZAŁĄCZNIK VAT FALLBACK] Znaleziono zapis VAT przez powiązanie Zrodlowy/DocelowyDokumentDoKsiegowania. Dokument={Numer}; wynikDokumentId={DokumentId}; encjaTyp={EncjaTyp}",
                    dokumentZrodlowy?.NumerDokumentu,
                    dokumentId,
                    encja.GetType().FullName);
            }

            return encja;
        }

        private object PobierzVatZDokumentuZrodlowego(DokumentDoKsiegowania dokumentZrodlowy, object wynikDokumentId)
        {
            if (dokumentZrodlowy == null)
            {
                return null;
            }

            try
            {
                object wynikowyVat = dokumentZrodlowy.WynikowyZapisWEwidencjiVAT;
                if (wynikowyVat != null && CzyIdPasuje(wynikowyVat, wynikDokumentId))
                {
                    return wynikowyVat;
                }
            }
            catch { }

            try
            {
                object zrodlowyVat = dokumentZrodlowy.ZrodlowyZapisWEwidencjiVAT;
                if (zrodlowyVat != null && CzyIdPasuje(zrodlowyVat, wynikDokumentId))
                {
                    return zrodlowyVat;
                }
            }
            catch { }

            return null;
        }

        private object ZnajdzVatPoPowiazaniuZDdk(dynamic mgrVat, DokumentDoKsiegowania dokumentZrodlowy, object wynikDokumentId)
        {
            if (mgrVat == null || dokumentZrodlowy == null)
            {
                return null;
            }

            Guid dokumentZrodlowyId = dokumentZrodlowy.Id;
            try
            {
                foreach (var encja in ((System.Collections.IEnumerable)mgrVat.Dane.Wszystkie()).Cast<dynamic>())
                {
                    object encjaObj = (object)encja;
                    if (CzyIdPasuje(encjaObj, wynikDokumentId))
                    {
                        return encjaObj;
                    }

                    if (CzyPowiazanyZDdk(encja, dokumentZrodlowyId))
                    {
                        return encjaObj;
                    }
                }
            }
            catch { }

            return null;
        }

        private bool CzyPowiazanyZDdk(dynamic encja, Guid dokumentDoKsiegowaniaId)
        {
            try
            {
                if (encja.ZrodlowyDokumentDoKsiegowania != null && encja.ZrodlowyDokumentDoKsiegowania.Id == dokumentDoKsiegowaniaId)
                {
                    return true;
                }
            }
            catch { }

            try
            {
                if (encja.DocelowyDokumentDoKsiegowania != null && encja.DocelowyDokumentDoKsiegowania.Id == dokumentDoKsiegowaniaId)
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private bool CzyIdPasuje(object encja, object id)
        {
            if (encja == null)
            {
                return false;
            }

            if (id == null)
            {
                return true;
            }

            try
            {
                object encjaId = encja.GetType().GetProperty("Id")?.GetValue(encja);
                return encjaId != null && Convert.ToInt32(encjaId) == Convert.ToInt32(id);
            }
            catch
            {
                return false;
            }
        }

        private bool ZawieraTyp(string typ, string fragment)
        {
            return typ?.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private object ZnajdzFizycznaEncje(dynamic mgr, object id)
        {
            if (mgr == null || id == null) return null;
            int targetId = Convert.ToInt32(id);
            try { return mgr.Dane.Znajdz(targetId); } catch { }
            try { return ((IEnumerable<dynamic>)mgr.Dane.Wszystkie()).FirstOrDefault(e => e.Id == targetId); } catch { }
            return null;
        }

        private dynamic PobierzMenedzera(string nazwaInterfejsu, Uchwyt sfera = null)
        {
            var uchwyt = sfera ?? _sfera;
            var typSzukany = ZnajdzTypInterfejsu(nazwaInterfejsu);
            if (typSzukany != null)
            {
                var metoda = uchwyt.GetType().GetMethods().FirstOrDefault(m => m.Name == "PodajObiektTypu" && m.IsGenericMethod && m.GetParameters().Length == 0);
                if (metoda != null) return metoda.MakeGenericMethod(typSzukany).Invoke(uchwyt, null);
            }
            return null;
        }

        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types != null)
                {
                    var t = types.FirstOrDefault(x => x != null && x.Name == nazwa && x.IsInterface);
                    if (t != null) return t;
                }
            }
            return null;
        }

        private sealed class AttachmentTargetRef
        {
            public object Entity { get; set; }
            public string ManagerKey { get; set; }
            public string ResultType { get; set; }
            public int? DocumentId { get; set; }
            public int? EntityId { get; set; }
            public string EntityType { get; set; }
            public bool? CanHaveLibrary { get; set; }
            public string LibraryCheckError { get; set; }
        }

        private sealed class AttachmentSaveResult
        {
            public AttachmentTargetRef Target { get; set; }
            public int? AttachmentId { get; set; }
            public string AttachmentName { get; set; }
            public string AttachmentType { get; set; }
        }

        private sealed class AttachmentAuditCandidate
        {
            public string JobId { get; set; }
            public string FileName { get; set; }
            public string SafeName { get; set; }
            public string Extension { get; set; }
            public int ContentLength { get; set; }
            public string Sha256 { get; set; }
            public DocumentProcessingReport Report { get; set; }
            public AttachmentOperationRecord OperationRecord { get; set; }
            public string InvoiceNumber { get; set; }
            public string VendorNip { get; set; }
            public string WaitingRoomNumber { get; set; }
            public string ManagerKey { get; set; }
            public string ResultType { get; set; }
            public int? DocumentId { get; set; }
            public int? EntityId { get; set; }
            public string EntityType { get; set; }
            public int? SavedAttachmentId { get; set; }
            public string SavedAttachmentName { get; set; }
            public string SavedAttachmentType { get; set; }
            public bool SavedByFallback { get; set; }
            public bool Verified { get; set; }
            public string VerificationStatus { get; set; } = "pending";
            public string VerificationDetails { get; set; }
        }

        private sealed class AttachmentOperationRecord
        {
            public string JobId { get; set; }
            public string DatabaseName { get; set; }
            public string BillingPeriod { get; set; }
            public int Index { get; set; }
            public string DocumentNumber { get; set; }
            public string SystemNip { get; set; }
            public int? WaitingRoomNr { get; set; }
            public string WaitingRoomId { get; set; }
            public string WaitingRoomNumber { get; set; }
            public string WaitingRoomNip { get; set; }
            public string ManifestInvoiceNumber { get; set; }
            public string ManifestVendorNip { get; set; }
            public string ManifestPdfFileName { get; set; }
            public string KsefNumber { get; set; }
            public string KsefCode { get; set; }
            public string WaitingRoomStatus { get; set; }
            public string MatchStatus { get; set; }
            public string DecreeStatus { get; set; }
            public string AttachmentStatusBefore { get; set; }
            public string PdfFileName { get; set; }
            public string AttachmentDocumentNumber { get; set; }
            public string AttachmentVendorNip { get; set; }
            public int AttachmentBytes { get; set; }
            public string Sha256 { get; set; }
            public string SafeAttachmentName { get; set; }
            public string AttachmentExtension { get; set; }
            public string TempPath { get; set; }
            public int ResultEntriesCount { get; set; }
            public string ResultEntriesDescription { get; set; }
            public int MissingEntityCount { get; set; }
            public int TargetsCount { get; set; }
            public string TargetsDescription { get; set; }
            public string SaveResult { get; set; }
            public int? SavedAttachmentId { get; set; }
            public string SavedAttachmentName { get; set; }
            public string SavedAttachmentType { get; set; }
            public string InvalidData { get; set; }
            public int FallbackSavedCount { get; set; }
            public int FallbackTotalCount { get; set; }
            public string FallbackErrors { get; set; }
            public int AuditTargetsCount { get; set; }
            public int AuditVerifiedCount { get; set; }
            public int AuditFailedCount { get; set; }
            public string AuditDetails { get; set; }
            public string FinalStatus { get; set; }
            public string FailureReason { get; set; }
            public DocumentProcessingReport Report { get; set; }
        }

        private sealed class AttachmentDescriptor
        {
            public int? Id { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public string Description { get; set; }
            public string DisplayName { get; set; }
        }
    }
}


