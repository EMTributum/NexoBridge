using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.EwidencjaVAT;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class KsefNumberAssignmentService
    {
        private static readonly HashSet<string> PusteLubTechniczneKodyKsef = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BFK", "DI", "OFF", "BRAK", "NONE", "NULL", "NIE DOTYCZY"
        };

        private readonly Uchwyt _sfera;
        private readonly ILogger<KsefNumberAssignmentService> _logger;

        public KsefNumberAssignmentService(Uchwyt sfera, ILogger<KsefNumberAssignmentService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task<int> PrzypiszPrzedDekretacjaAsync(ImportJob job, List<DocumentProcessingReport> manifest, Func<int, string, Task> raportujPostep)
        {
            var metadaneZKsef = PobierzMetadaneZKsef(job).ToList();
            if (metadaneZKsef.Count == 0)
            {
                _logger.LogInformation("[KSEF] Brak numerów KSeF w metadanych zadania {JobId}. Pomijam etap audytu Poczekalni.", job.JobId);
                return 0;
            }

            await raportujPostep(65, "Audyt numerów KSeF w Poczekalni...");

            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            var oczekujace = ((IEnumerable)menedzerDokumentow.Dane.Wszystkie())
                .Cast<DokumentDoKsiegowania>()
                .Where(d => (int)d.StatusKsiegowy == 2)
                .ToList();

            int potwierdzone = 0;
            var problemy = new List<string>();

            foreach (var meta in metadaneZKsef)
            {
                string ksefNumber = OczyscNumerKsef(meta.KsefNumber);
                var raport = ZnajdzRaport(manifest, meta);
                var match = InvoiceDocumentMatcher.Match(oczekujace, meta);
                if (match.Document == null)
                {
                    var fallbackPoKsef = InvoiceDocumentMatcher.MatchByExactKsefAndNip(oczekujace, meta);
                    if (fallbackPoKsef.Document != null || fallbackPoKsef.Status == "ambiguous")
                    {
                        if (fallbackPoKsef.Document != null)
                        {
                            _logger.LogInformation("[KSEF MATCH FALLBACK] Faktura={Numer}; NIP={Nip}; KSeF={Ksef}; dokument={Dokument}; reason={Reason}",
                                meta.InvoiceNumber,
                                meta.VendorNip,
                                ksefNumber,
                                fallbackPoKsef.Document.NumerDokumentu,
                                match.Reason);
                        }

                        match = fallbackPoKsef;
                    }
                }

                if (match.Document == null)
                {
                    string status = match.Status == "ambiguous" ? "ambiguousWaitingRoomMatch" : "notFoundInWaitingRoom";
                    UstawStatusKsef(raport, status, $"Nie potwierdzono KSeF {ksefNumber} w Poczekalni dla faktury {meta.InvoiceNumber} (NIP: {meta.VendorNip}). {match.Reason} Kandydaci: {ListaDoLogu(match.Candidates)}");
                    problemy.Add($"{meta.InvoiceNumber}/{meta.VendorNip}: {status}");
                    _logger.LogWarning("[KSEF POCZEKALNIA PROBLEM] Faktura={Numer}; NIP={Nip}; KSeF={Ksef}; status={Status}; reason={Reason}; kandydaci={Kandydaci}",
                        meta.InvoiceNumber,
                        meta.VendorNip,
                        ksefNumber,
                        status,
                        match.Reason,
                        ListaDoLogu(match.Candidates));
                    continue;
                }

                if (raport != null)
                {
                    ImportManifestService.WypelnijDanePoczekalni(raport, match.Document);
                    raport.WaitingRoomStatus = "found";
                    raport.MatchStatus = match.Status;
                }

                string obecnyKsef = OczyscNumerKsef(match.Document.NumerKSeF);
                if (PorownajKsef(obecnyKsef, ksefNumber))
                {
                    potwierdzone++;
                    UstawStatusKsef(raport, "confirmedInWaitingRoom", null);
                    _logger.LogInformation("[KSEF OK] Dokument w Poczekalni {Numer} (NIP: {Nip}) ma numer KSeF {Ksef}.",
                        match.Document.NumerDokumentu,
                        match.Document.PodmiotHistoria?.NIP,
                        obecnyKsef);
                    continue;
                }

                string warning = string.IsNullOrWhiteSpace(obecnyKsef)
                    ? $"Numer KSeF nie został potwierdzony w Poczekalni. Oczekiwano: {ksefNumber}, odczytano: brak."
                    : $"Dokument w Poczekalni ma inny numer KSeF. Oczekiwano: {ksefNumber}, odczytano: {obecnyKsef}.";

                string problemStatus = string.IsNullOrWhiteSpace(obecnyKsef) ? "notConfirmedInWaitingRoom" : "differentInWaitingRoom";
                UstawStatusKsef(raport, problemStatus, warning);
                problemy.Add($"{meta.InvoiceNumber}/{meta.VendorNip}: {problemStatus}");
                _logger.LogWarning("[KSEF POCZEKALNIA NIEPOTWIERDZONY] Dokument={Numer}; NIP={Nip}; oczekiwano={Expected}; odczytano={Actual}; status={Status}",
                    match.Document.NumerDokumentu,
                    match.Document.PodmiotHistoria?.NIP,
                    ksefNumber,
                    obecnyKsef ?? "brak",
                    problemStatus);
            }

            _logger.LogInformation("[KSEF PODSUMOWANIE] JobId={JobId}; otrzymane={Otrzymane}; potwierdzoneWPoczekalni={Potwierdzone}; problemy={Problemy}",
                job.JobId,
                metadaneZKsef.Count,
                potwierdzone,
                ListaDoLogu(problemy));

            return potwierdzone;
        }

        public async Task ZweryfikujPoDekretacjiAsync(
            ImportJob job,
            object rezultat,
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> zatwierdzone,
            List<DocumentProcessingReport> manifest,
            Func<int, string, Task> raportujPostep)
        {
            var metadaneZKsef = PobierzMetadaneZKsef(job).ToList();
            if (metadaneZKsef.Count == 0)
            {
                return;
            }

            if (rezultat == null || zatwierdzone == null || zatwierdzone.Count == 0)
            {
                foreach (var meta in metadaneZKsef)
                {
                    UstawStatusKsef(ZnajdzRaport(manifest, meta), "notVerifiedAfterDecree", "Dekretacja nie zwróciła dokumentów do weryfikacji KSeF.");
                }
                _logger.LogWarning("[KSEF WERYFIKACJA POMINIĘTA] Brak wyników dekretacji dla zadania {JobId}.", job.JobId);
                return;
            }

            await raportujPostep(92, "Weryfikacja numerów KSeF po dekretacji...");

            var listaWynikow = PobierzWynikoweOperacje(rezultat);
            var menedzerowie = new Dictionary<string, dynamic>
            {
                { "KPiR", PobierzMenedzera("IZapisyWKPiR") },
                { "Vat", PobierzMenedzera("IZapisyWEwidencjiVAT") },
                { "Dekret", PobierzMenedzera("IDekrety") },
                { "EP", PobierzMenedzera("IZapisyWEP") }
            };

            int sprawdzone = 0;
            var problemy = new List<string>();

            for (int i = 0; i < zatwierdzone.Count; i++)
            {
                var dokumentZrodlowy = zatwierdzone[i].Item1;
                var raport = ImportManifestService.ZnajdzRaportDlaDokumentu(manifest, dokumentZrodlowy);
                var meta = raport != null
                    ? new InvoiceMetadata { InvoiceNumber = raport.InvoiceNumber, VendorNip = raport.VendorNip, KsefNumber = raport.KsefNumber, KsefCode = raport.KsefCode, PdfFileName = raport.PdfFileName }
                    : ZnajdzMetadaneDlaDokumentu(metadaneZKsef, dokumentZrodlowy);

                if (meta == null || string.IsNullOrWhiteSpace(OczyscNumerKsef(meta.KsefNumber)))
                {
                    continue;
                }

                string oczekiwanyKsef = OczyscNumerKsef(meta.KsefNumber);
                if (i >= listaWynikow.Count)
                {
                    string warning = $"Brak wyniku dekretacji pod indeksem {i}; nie potwierdzono KSeF {oczekiwanyKsef}.";
                    UstawStatusKsef(raport, "notVerifiedAfterDecree", warning);
                    problemy.Add($"{dokumentZrodlowy.NumerDokumentu}: brak wyniku dekretacji");
                    continue;
                }

                var wynikowe = PobierzWynikoweZapisy((object)listaWynikow[i]);
                if (wynikowe.Count == 0)
                {
                    string warning = $"Brak wynikowych zapisów księgowych; nie potwierdzono KSeF {oczekiwanyKsef}.";
                    UstawStatusKsef(raport, "notVerifiedAfterDecree", warning);
                    problemy.Add($"{dokumentZrodlowy.NumerDokumentu}: brak wynikowych zapisów");
                    continue;
                }

                var opisWynikow = new List<string>();
                var niezgodneWyniki = new List<string>();

                foreach (var wynik in wynikowe)
                {
                    object encja = ZnajdzEncje(menedzerowie, wynik);
                    string numerKsef = PobierzNumerKsef(encja) ?? PobierzNumerKsef((object)wynik);
                    string opis = $"{wynik?.GetType().Name}: dokumentId={PobierzDokumentId(wynik)}, encja={(encja == null ? "brak" : encja.GetType().Name)}, ksef={numerKsef ?? "brak"}";
                    opisWynikow.Add(opis);

                    if (!PorownajKsef(numerKsef, oczekiwanyKsef))
                    {
                        niezgodneWyniki.Add(opis);
                    }
                }

                if (niezgodneWyniki.Count > 0)
                {
                    string warning = $"Nie potwierdzono KSeF po dekretacji. Oczekiwano {oczekiwanyKsef}, niezgodne wyniki: {string.Join("; ", niezgodneWyniki)}";
                    UstawStatusKsef(raport, "notConfirmedAfterDecree", warning);
                    problemy.Add($"{dokumentZrodlowy.NumerDokumentu}: KSeF niepotwierdzony po dekretacji");
                    _logger.LogWarning("[KSEF WERYFIKACJA PROBLEM] Dokument {Numer}; {Warning}. Wyniki: {Wyniki}",
                        dokumentZrodlowy.NumerDokumentu,
                        warning,
                        string.Join("; ", opisWynikow));
                }
                else
                {
                    sprawdzone++;
                    UstawStatusKsef(raport, "confirmedAfterDecree", null);
                    _logger.LogInformation("[KSEF WERYFIKACJA OK] Dokument {Numer} zachował KSeF {Ksef}. Wyniki: {Wyniki}",
                        dokumentZrodlowy.NumerDokumentu,
                        oczekiwanyKsef,
                        string.Join("; ", opisWynikow));
                }
            }

            _logger.LogInformation("[KSEF WERYFIKACJA PODSUMOWANIE] JobId={JobId}; sprawdzone={Sprawdzone}; problemy={Problemy}.",
                job.JobId,
                sprawdzone,
                ListaDoLogu(problemy));
        }

        public async Task PrzypiszKodyPoDekretacjiAsync(
            ImportJob job,
            object rezultat,
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> zatwierdzone,
            List<DocumentProcessingReport> manifest,
            Func<int, string, Task> raportujPostep)
        {
            var metadaneZKodami = PobierzMetadaneZKodem(job).ToList();
            if (metadaneZKodami.Count == 0)
            {
                return;
            }

            if (rezultat == null || zatwierdzone == null || zatwierdzone.Count == 0)
            {
                foreach (var meta in metadaneZKodami)
                {
                    var raport = ZnajdzRaport(manifest, meta);
                    UstawStatusKoduKsef(raport, "notAssignedAfterDecree", "Nie przeniesiono technicznego kodu KSeF, bo dekretacja nie zwróciła wyników.");
                    _logger.LogDebug("[KSEF CODE PLAN BRAK DEKRETACJI] meta={Meta}; raport={Raport}",
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport));
                }

                _logger.LogWarning("[KSEF CODE POMINIĘTO] Brak wyników dekretacji dla zadania {JobId}. Kody={Codes}",
                    job.JobId,
                    ListaDoLogu(metadaneZKodami.Select(m => $"{m.InvoiceNumber}/{m.VendorNip}->{OczyscKodKsef(m.KsefCode)}")));
                return;
            }

            await raportujPostep(94, "Uzupełnianie kodów KSeF na zapisach VAT...");

            object mgrVat = PobierzMenedzera("IZapisyWEwidencjiVAT");
            if (mgrVat == null)
            {
                foreach (var meta in metadaneZKodami)
                {
                    var raport = ZnajdzRaport(manifest, meta);
                    UstawStatusKoduKsef(raport, "notAssignedAfterDecree", "Nie przeniesiono technicznego kodu KSeF, bo nie udało się pobrać menedżera zapisów VAT.");
                    _logger.LogDebug("[KSEF CODE PLAN BRAK MENEDŻERA VAT] meta={Meta}; raport={Raport}",
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport));
                }

                _logger.LogWarning("[KSEF CODE BŁĄD] Nie udało się pobrać IZapisyWEwidencjiVAT dla zadania {JobId}.", job.JobId);
                return;
            }

            var listaWynikow = PobierzWynikoweOperacje((object)rezultat);
            var wszystkieWynikoweZapisy = new List<dynamic>();
            foreach (var wynikOperacji in listaWynikow)
            {
                wszystkieWynikoweZapisy.AddRange(PobierzWynikoweZapisy((object)wynikOperacji));
            }
            int zapisane = 0;
            var problemy = new List<string>();
            var zatwierdzoneDokumenty = zatwierdzone
                .Where(z => z?.Item1 != null)
                .Select(z => z.Item1)
                .ToList();

            _logger.LogDebug("[KSEF CODE START] JobId={JobId}; metadataZKodami={MetadataCount}; zatwierdzone={AcceptedCount}; operacjeWynikowe={ResultOperations}; wynikoweZapisy={ResultEntries}; manifest={ManifestCount}; kody={Codes}",
                job.JobId,
                metadaneZKodami.Count,
                zatwierdzoneDokumenty.Count,
                listaWynikow.Count,
                wszystkieWynikoweZapisy.Count,
                manifest?.Count ?? 0,
                ListaDoLogu(metadaneZKodami.Select(OpiszMetadaneKodu)));

            foreach (var meta in metadaneZKodami)
            {
                string kod = OczyscKodKsef(meta?.KsefCode);
                if (string.IsNullOrWhiteSpace(kod))
                {
                    continue;
                }

                var raport = ZnajdzRaport(manifest, meta);
                _logger.LogDebug("[KSEF CODE PLAN START] meta={Meta}; raport={Raport}",
                    OpiszMetadaneKodu(meta),
                    OpiszRaportKodu(raport));

                if (!string.IsNullOrWhiteSpace(OczyscNumerKsef(meta?.KsefNumber)))
                {
                    UstawStatusKoduKsef(raport, "skippedRealKsefNumber", null);
                    _logger.LogInformation("[KSEF CODE POMINIĘTO] Dokument {Numer} ma realny numer KSeF, więc kod techniczny {Kod} nie jest przenoszony.",
                        (object)(meta.InvoiceNumber ?? "brak"),
                        (object)kod);
                    _logger.LogDebug("[KSEF CODE PLAN FINAL] status=skippedRealKsefNumber; meta={Meta}; raport={Raport}",
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport));
                    continue;
                }

                if (!TryMapujKodKsef(kod, out byte wartoscKodu))
                {
                    string warning = $"Nieobsługiwany techniczny kod KSeF: {kod}. Obsługiwane wartości: BFK, OFF, DI.";
                    UstawStatusKoduKsef(raport, "unsupportedCode", warning);
                    problemy.Add($"{meta.InvoiceNumber}/{meta.VendorNip}: {warning}");
                    _logger.LogWarning("[KSEF CODE NIEOBSŁUGIWANY] Faktura={Numer}; NIP={Nip}; kod={Kod}",
                        meta.InvoiceNumber,
                        meta.VendorNip,
                        kod);
                    _logger.LogDebug("[KSEF CODE PLAN FINAL] status=unsupportedCode; meta={Meta}; raport={Raport}",
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport));
                    continue;
                }

                var dokumentMatch = ZnajdzZatwierdzonyDokumentDlaKodu(zatwierdzoneDokumenty, raport, meta);
                _logger.LogDebug("[KSEF CODE PLAN MATCH] meta={Meta}; raport={Raport}; match={Match}; dokument={Dokument}",
                    OpiszMetadaneKodu(meta),
                    OpiszRaportKodu(raport),
                    OpiszMatch(dokumentMatch),
                    OpiszDokumentKodu(dokumentMatch.Document));

                if (dokumentMatch.Document == null)
                {
                    string status = dokumentMatch.Status == "ambiguous" ? "skippedAmbiguousMatch" : "notFound";
                    string warning = status == "skippedAmbiguousMatch"
                        ? $"Nie przeniesiono technicznego kodu KSeF {kod}, bo dopasowanie do zadekretowanego dokumentu jest niejednoznaczne. {dokumentMatch.Reason} Kandydaci: {ListaDoLogu(dokumentMatch.Candidates)}"
                        : $"Nie przeniesiono technicznego kodu KSeF {kod}, bo nie znaleziono zadekretowanego dokumentu dla metadanych. {dokumentMatch.Reason}";
                    UstawStatusKoduKsef(raport, status, warning);
                    problemy.Add($"{meta.InvoiceNumber}/{meta.VendorNip}: {status}");
                    _logger.LogWarning("[KSEF CODE BRAK DOKUMENTU] Faktura={Numer}; NIP={Nip}; kod={Kod}; status={Status}; reason={Reason}; kandydaci={Kandydaci}",
                        meta.InvoiceNumber,
                        meta.VendorNip,
                        kod,
                        status,
                        dokumentMatch.Reason,
                        ListaDoLogu(dokumentMatch.Candidates));
                    _logger.LogDebug("[KSEF CODE PLAN FINAL] status={Status}; meta={Meta}; raport={Raport}; match={Match}",
                        status,
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport),
                        OpiszMatch(dokumentMatch));
                    continue;
                }

                var dokumentZrodlowy = dokumentMatch.Document;
                if (raport != null)
                {
                    ImportManifestService.WypelnijDanePoczekalni(raport, dokumentZrodlowy);
                    raport.WaitingRoomStatus = "found";
                    raport.MatchStatus = dokumentMatch.Status;
                }

                int indeksZatwierdzonego = ZnajdzIndeksZatwierdzonego(zatwierdzone, dokumentZrodlowy);
                List<dynamic> wynikoweZIndeksu = indeksZatwierdzonego >= 0 && indeksZatwierdzonego < listaWynikow.Count
                    ? PobierzWynikoweZapisy((object)listaWynikow[indeksZatwierdzonego])
                    : new List<dynamic>();
                _logger.LogDebug("[KSEF CODE PLAN WYNIKI] meta={Meta}; dokument={Dokument}; indeksZatwierdzonego={Index}; wynikoweZIndeksu={IndexedResults}; wszystkieWynikoweVat={AllVatResults}",
                    OpiszMetadaneKodu(meta),
                    OpiszDokumentKodu(dokumentZrodlowy),
                    indeksZatwierdzonego,
                    OpiszWyniki(wynikoweZIndeksu),
                    OpiszWyniki(wszystkieWynikoweZapisy.Where(w => ZawieraTyp((w as object)?.GetType().Name, "VAT")).ToList()));

                IEnumerable<object> znalezioneZapisyVat = ZnajdzZapisyVat(
                    mgrVat,
                    wynikoweZIndeksu,
                    wszystkieWynikoweZapisy,
                    dokumentZrodlowy,
                    meta,
                    out string zrodlaZapisuVat);
                var zapisyVat = znalezioneZapisyVat.ToList();
                _logger.LogDebug("[KSEF CODE PLAN VAT] meta={Meta}; dokument={Dokument}; zrodla={Zrodla}; zapisyVat={ZapisyVat}",
                    OpiszMetadaneKodu(meta),
                    OpiszDokumentKodu(dokumentZrodlowy),
                    zrodlaZapisuVat,
                    OpiszZapisyVat(zapisyVat));

                if (zapisyVat.Count == 0)
                {
                    string warning = $"Nie znaleziono wynikowego zapisu VAT; nie przeniesiono technicznego kodu KSeF {kod}.";
                    UstawStatusKoduKsef(raport, "noVatResult", warning);
                    problemy.Add($"{dokumentZrodlowy?.NumerDokumentu}: brak zapisu VAT");
                    string opisWynikowZIndeksu = OpiszWyniki(wynikoweZIndeksu);
                    string opisWszystkichVat = OpiszWyniki(wszystkieWynikoweZapisy
                        .Where(w => ZawieraTyp((w as object)?.GetType().Name, "VAT"))
                        .ToList());
                    _logger.LogWarning("[KSEF CODE BRAK VAT] Dokument={Numer}; NIP={Nip}; kod={Kod}; wynikiZIndeksu={WynikiZIndeksu}; wszystkieWynikiVAT={WszystkieWynikiVat}",
                        (object)(dokumentZrodlowy?.NumerDokumentu ?? "brak"),
                        (object)(dokumentZrodlowy?.PodmiotHistoria?.NIP ?? "brak"),
                        (object)kod,
                        (object)opisWynikowZIndeksu,
                        (object)opisWszystkichVat);
                    _logger.LogDebug("[KSEF CODE PLAN FINAL] status=noVatResult; meta={Meta}; raport={Raport}; dokument={Dokument}; zrodla={Zrodla}",
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport),
                        OpiszDokumentKodu(dokumentZrodlowy),
                        zrodlaZapisuVat);
                    continue;
                }

                int zapisaneDlaDokumentu = 0;
                var bledyDlaDokumentu = new List<string>();
                foreach (var zapisVat in zapisyVat)
                {
                    string opisPrzed = OpiszZapisVat(zapisVat);
                    if (UstawKodNaZapisieVat(mgrVat, zapisVat, wartoscKodu, kod, out string szczegoly))
                    {
                        zapisaneDlaDokumentu++;
                        _logger.LogDebug("[KSEF CODE VAT ZAPIS OK] meta={Meta}; dokument={Dokument}; kod={Kod}; wartosc={Wartosc}; zapisPrzed={ZapisPrzed}; szczegoly={Szczegoly}",
                            OpiszMetadaneKodu(meta),
                            OpiszDokumentKodu(dokumentZrodlowy),
                            kod,
                            wartoscKodu,
                            opisPrzed,
                            szczegoly);
                    }
                    else
                    {
                        bledyDlaDokumentu.Add(szczegoly);
                        _logger.LogDebug("[KSEF CODE VAT ZAPIS BŁĄD] meta={Meta}; dokument={Dokument}; kod={Kod}; wartosc={Wartosc}; zapisPrzed={ZapisPrzed}; szczegoly={Szczegoly}",
                            OpiszMetadaneKodu(meta),
                            OpiszDokumentKodu(dokumentZrodlowy),
                            kod,
                            wartoscKodu,
                            opisPrzed,
                            szczegoly);
                    }
                }

                if (zapisaneDlaDokumentu == zapisyVat.Count)
                {
                    zapisane++;
                    UstawStatusKoduKsef(raport, "assignedAfterDecree", null);
                    _logger.LogInformation("[KSEF CODE OK] Dokument {Numer}; NIP={Nip}; kod={Kod}; zapisyVAT={Count}.",
                        (object)(dokumentZrodlowy?.NumerDokumentu ?? "brak"),
                        (object)(dokumentZrodlowy?.PodmiotHistoria?.NIP ?? "brak"),
                        (object)kod,
                        (object)zapisyVat.Count);
                    _logger.LogDebug("[KSEF CODE DIAG] Dokument={Numer}; NIP={Nip}; kod={Kod}; zrodlaZapisuVAT={Zrodla}; wynikiZIndeksu={WynikiZIndeksu}",
                        (object)(dokumentZrodlowy?.NumerDokumentu ?? "brak"),
                        (object)(dokumentZrodlowy?.PodmiotHistoria?.NIP ?? "brak"),
                        (object)kod,
                        (object)zrodlaZapisuVat,
                        (object)OpiszWyniki(wynikoweZIndeksu));
                    _logger.LogDebug("[KSEF CODE PLAN FINAL] status=assignedAfterDecree; meta={Meta}; raport={Raport}; dokument={Dokument}; zapisyVat={ZapisyVat}",
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport),
                        OpiszDokumentKodu(dokumentZrodlowy),
                        OpiszZapisyVat(zapisyVat));
                }
                else if (zapisaneDlaDokumentu > 0)
                {
                    string warning = $"Techniczny kod KSeF {kod} przeniesiono tylko częściowo: {zapisaneDlaDokumentu}/{zapisyVat.Count}. Błędy: {ListaDoLogu(bledyDlaDokumentu)}";
                    UstawStatusKoduKsef(raport, "assignedPartiallyAfterDecree", warning);
                    problemy.Add($"{dokumentZrodlowy?.NumerDokumentu}: kod częściowo");
                    _logger.LogWarning("[KSEF CODE CZĘŚCIOWO] Dokument={Numer}; NIP={Nip}; kod={Kod}; zapisane={Saved}/{Total}; błędy={Errors}",
                        (object)(dokumentZrodlowy?.NumerDokumentu ?? "brak"),
                        (object)(dokumentZrodlowy?.PodmiotHistoria?.NIP ?? "brak"),
                        (object)kod,
                        (object)zapisaneDlaDokumentu,
                        (object)zapisyVat.Count,
                        (object)ListaDoLogu(bledyDlaDokumentu));
                    _logger.LogDebug("[KSEF CODE PLAN FINAL] status=assignedPartiallyAfterDecree; meta={Meta}; raport={Raport}; dokument={Dokument}; zapisyVat={ZapisyVat}; bledy={Errors}",
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport),
                        OpiszDokumentKodu(dokumentZrodlowy),
                        OpiszZapisyVat(zapisyVat),
                        ListaDoLogu(bledyDlaDokumentu));
                }
                else
                {
                    string warning = $"Nie przeniesiono technicznego kodu KSeF {kod}. Błędy: {ListaDoLogu(bledyDlaDokumentu)}";
                    UstawStatusKoduKsef(raport, "notAssignedAfterDecree", warning);
                    problemy.Add($"{dokumentZrodlowy?.NumerDokumentu}: kod nieprzeniesiony");
                    _logger.LogWarning("[KSEF CODE BŁĄD] Dokument={Numer}; NIP={Nip}; kod={Kod}; błędy={Errors}",
                        dokumentZrodlowy?.NumerDokumentu,
                        dokumentZrodlowy?.PodmiotHistoria?.NIP,
                        kod,
                        ListaDoLogu(bledyDlaDokumentu));
                    _logger.LogDebug("[KSEF CODE PLAN FINAL] status=notAssignedAfterDecree; meta={Meta}; raport={Raport}; dokument={Dokument}; zapisyVat={ZapisyVat}; bledy={Errors}",
                        OpiszMetadaneKodu(meta),
                        OpiszRaportKodu(raport),
                        OpiszDokumentKodu(dokumentZrodlowy),
                        OpiszZapisyVat(zapisyVat),
                        ListaDoLogu(bledyDlaDokumentu));
                }
            }

            _logger.LogInformation("[KSEF CODE PODSUMOWANIE] JobId={JobId}; otrzymane={Received}; przeniesione={Assigned}; problemy={Problems}",
                job.JobId,
                metadaneZKodami.Count,
                zapisane,
                ListaDoLogu(problemy));
            _logger.LogDebug("[KSEF CODE PODSUMOWANIE DIAG] JobId={JobId}; statusyManifestu={Statuses}",
                job.JobId,
                OpiszStatusyKodowManifestu(manifest, metadaneZKodami));
        }

        private IEnumerable<InvoiceMetadata> PobierzMetadaneZKsef(ImportJob job)
        {
            return (job.InvoicesMetadata ?? new List<InvoiceMetadata>())
                .Where(m => !string.IsNullOrWhiteSpace(OczyscNumerKsef(m.KsefNumber)));
        }

        private IEnumerable<InvoiceMetadata> PobierzMetadaneZKodem(ImportJob job)
        {
            return (job.InvoicesMetadata ?? new List<InvoiceMetadata>())
                .Where(m => !string.IsNullOrWhiteSpace(OczyscKodKsef(m.KsefCode)));
        }

        private InvoiceMatchResult ZnajdzZatwierdzonyDokumentDlaKodu(
            List<DokumentDoKsiegowania> zatwierdzoneDokumenty,
            DocumentProcessingReport raport,
            InvoiceMetadata meta)
        {
            if (raport?.WaitingRoomNr.HasValue == true)
            {
                var byNr = zatwierdzoneDokumenty
                    .Where(d => d.Nr == raport.WaitingRoomNr.Value)
                    .ToList();

                if (byNr.Count == 1)
                {
                    return InvoiceMatchResult.Matched("matchedByWaitingRoomNr", byNr[0], raport.WaitingRoomNr.Value.ToString());
                }

                if (byNr.Count > 1)
                {
                    return InvoiceMatchResult.Ambiguous(
                        $"Wiele zatwierdzonych dokumentow ma ten sam Nr Poczekalni {raport.WaitingRoomNr}.",
                        byNr,
                        raport.WaitingRoomNr.Value.ToString());
                }

                return InvoiceMatchResult.NotFound(
                    $"Raport manifestu wskazuje dokument Poczekalni Nr={raport.WaitingRoomNr}, ale dokument nie znalazl sie wsrod zadekretowanych.",
                    zatwierdzoneDokumenty);
            }

            var match = InvoiceDocumentMatcher.Match(zatwierdzoneDokumenty, meta);
            if (match.Document != null || match.Status == "ambiguous")
            {
                return match;
            }

            var ksefMatch = InvoiceDocumentMatcher.MatchByExactKsefAndNip(zatwierdzoneDokumenty, meta);
            if (ksefMatch.Document != null || ksefMatch.Status == "ambiguous")
            {
                return ksefMatch;
            }

            return match;
        }

        private int ZnajdzIndeksZatwierdzonego(List<Tuple<DokumentDoKsiegowania, SchematImportu>> zatwierdzone, DokumentDoKsiegowania dokument)
        {
            if (zatwierdzone == null || dokument == null) return -1;

            for (int i = 0; i < zatwierdzone.Count; i++)
            {
                if (zatwierdzone[i]?.Item1?.Nr == dokument.Nr)
                {
                    return i;
                }
            }

            return -1;
        }

        private InvoiceMetadata ZnajdzMetadaneDlaDokumentu(IEnumerable<InvoiceMetadata> metadane, DokumentDoKsiegowania dokument)
        {
            var match = InvoiceDocumentMatcher.MatchMetadataForDocument(metadane, dokument);
            return match.Metadata;
        }

        private List<dynamic> PobierzWynikoweOperacje(object rezultat)
        {
            if (rezultat == null) return new List<dynamic>();
            try { return ((System.Collections.IEnumerable)rezultat).Cast<dynamic>().ToList(); }
            catch { return new List<dynamic>(); }
        }

        private IEnumerable<object> ZnajdzZapisyVat(
            object mgrVat,
            IEnumerable<dynamic> wynikoweZIndeksu,
            IEnumerable<dynamic> wszystkieWynikoweZapisy,
            DokumentDoKsiegowania dokumentZrodlowy,
            InvoiceMetadata meta,
            out string zrodla)
        {
            var zapisy = new List<object>();
            var opisZrodel = new List<string>();

            DodajZapisyVatZWynikow(
                zapisy,
                opisZrodel,
                mgrVat,
                wynikoweZIndeksu,
                dokumentZrodlowy,
                meta,
                "wynikIndeksowy",
                zaufanyWynikDokumentu: true);

            try
            {
                DodajUnikalny(zapisy, dokumentZrodlowy?.WynikowyZapisWEwidencjiVAT, opisZrodel, "relacjaDDK.WynikowyZapisWEwidencjiVAT");
            }
            catch { }

            try
            {
                DodajUnikalny(zapisy, dokumentZrodlowy?.ZrodlowyZapisWEwidencjiVAT, opisZrodel, "relacjaDDK.ZrodlowyZapisWEwidencjiVAT");
            }
            catch { }

            DodajZapisyVatZWynikow(
                zapisy,
                opisZrodel,
                mgrVat,
                wszystkieWynikoweZapisy,
                dokumentZrodlowy,
                meta,
                "dowolnyWynikDekretacji");

            DodajZapisyVatPoPowiazaniuLubMetadanych(
                zapisy,
                opisZrodel,
                mgrVat,
                dokumentZrodlowy,
                meta);

            zrodla = ListaDoLogu(opisZrodel);
            return zapisy;
        }

        private void DodajZapisyVatZWynikow(
            List<object> zapisy,
            List<string> opisZrodel,
            object mgrVat,
            IEnumerable<dynamic> wynikowe,
            DokumentDoKsiegowania dokumentZrodlowy,
            InvoiceMetadata meta,
            string zrodlo,
            bool zaufanyWynikDokumentu = false)
        {
            foreach (var wynik in wynikowe ?? Enumerable.Empty<dynamic>())
            {
                string typ = wynik?.GetType().Name ?? "";
                if (!ZawieraTyp(typ, "VAT"))
                {
                    continue;
                }

                object encja = ZnajdzFizycznaEncje(mgrVat, PobierzDokumentId(wynik));
                if (CzyVatPasujeDoDokumentuLubMetadanych(encja, dokumentZrodlowy, meta) ||
                    (zaufanyWynikDokumentu && CzyBezpieczniePrzyjacWynikIndeksowyVat(encja, dokumentZrodlowy, meta)))
                {
                    DodajUnikalny(zapisy, encja, opisZrodel, $"{zrodlo}:{typ}/{PobierzDokumentId(wynik)}");
                }
            }
        }

        private bool CzyBezpieczniePrzyjacWynikIndeksowyVat(object zapisVat, DokumentDoKsiegowania dokumentZrodlowy, InvoiceMetadata meta)
        {
            if (zapisVat == null)
            {
                return false;
            }

            string oczekiwanyNumer = InvoiceDocumentMatcher.Normalize(meta?.InvoiceNumber ?? dokumentZrodlowy?.NumerDokumentu);
            if (string.IsNullOrWhiteSpace(oczekiwanyNumer))
            {
                return false;
            }

            string oczekiwanyNip = InvoiceDocumentMatcher.NormalizeNip(meta?.VendorNip ?? dokumentZrodlowy?.PodmiotHistoria?.NIP);
            string nipVat = InvoiceDocumentMatcher.NormalizeNip(
                PobierzWartoscSciezki(zapisVat, "Podmiot.NIP")
                ?? PobierzWartoscSciezki(zapisVat, "PreviewNIP")
                ?? PobierzWartoscSciezki(zapisVat, "PodmiotHistoria.NIP"));

            if (!string.IsNullOrWhiteSpace(oczekiwanyNip) &&
                (string.IsNullOrWhiteSpace(nipVat) || !nipVat.EndsWith(oczekiwanyNip, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            foreach (string numerVat in PobierzNumeryDokumentuVat(zapisVat))
            {
                string normalizedVat = InvoiceDocumentMatcher.Normalize(numerVat);
                if (normalizedVat == oczekiwanyNumer || InvoiceDocumentMatcher.IsSafeNumberMatch(oczekiwanyNumer, normalizedVat))
                {
                    return true;
                }
            }

            return false;
        }

        private void DodajZapisyVatPoPowiazaniuLubMetadanych(
            List<object> zapisy,
            List<string> opisZrodel,
            object mgrVat,
            DokumentDoKsiegowania dokumentZrodlowy,
            InvoiceMetadata meta)
        {
            if (mgrVat == null || (dokumentZrodlowy == null && meta == null))
            {
                return;
            }

            var kandydaci = new List<object>();
            try
            {
                foreach (var encja in ((System.Collections.IEnumerable)((dynamic)mgrVat).Dane.Wszystkie()).Cast<dynamic>())
                {
                    object encjaObj = (object)encja;
                    if (CzyVatPasujeDoDokumentuLubMetadanych(encjaObj, dokumentZrodlowy, meta))
                    {
                        DodajUnikalny(kandydaci, encjaObj);
                    }
                }
            }
            catch { }

            if (kandydaci.Count == 1)
            {
                DodajUnikalny(zapisy, kandydaci[0], opisZrodel, "fallbackVatPoPowiazaniuLubMetadanych");
            }
            else if (kandydaci.Count > 1)
            {
                opisZrodel.Add($"fallbackVatPominietyWieleKandydatow={kandydaci.Count}");
            }
        }

        private bool CzyVatPasujeDoDokumentuLubMetadanych(object zapisVat, DokumentDoKsiegowania dokumentZrodlowy, InvoiceMetadata meta)
        {
            if (zapisVat == null)
            {
                return false;
            }

            if (dokumentZrodlowy != null && CzyPowiazanyZDdk(zapisVat, dokumentZrodlowy.Id))
            {
                return true;
            }

            string oczekiwanyNip = InvoiceDocumentMatcher.NormalizeNip(meta?.VendorNip ?? dokumentZrodlowy?.PodmiotHistoria?.NIP);
            string oczekiwanyNumer = InvoiceDocumentMatcher.Normalize(meta?.InvoiceNumber ?? dokumentZrodlowy?.NumerDokumentu);
            if (string.IsNullOrWhiteSpace(oczekiwanyNip) || string.IsNullOrWhiteSpace(oczekiwanyNumer))
            {
                return false;
            }

            string nipVat = InvoiceDocumentMatcher.NormalizeNip(
                PobierzWartoscSciezki(zapisVat, "Podmiot.NIP")
                ?? PobierzWartoscSciezki(zapisVat, "PreviewNIP")
                ?? PobierzWartoscSciezki(zapisVat, "PodmiotHistoria.NIP"));
            if (string.IsNullOrWhiteSpace(nipVat) || !nipVat.EndsWith(oczekiwanyNip, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (string numerVat in PobierzNumeryDokumentuVat(zapisVat))
            {
                string normalizedVat = InvoiceDocumentMatcher.Normalize(numerVat);
                if (normalizedVat == oczekiwanyNumer || InvoiceDocumentMatcher.IsSafeNumberMatch(oczekiwanyNumer, normalizedVat))
                {
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<string> PobierzNumeryDokumentuVat(object zapisVat)
        {
            yield return PobierzWartoscSciezki(zapisVat, "NumerDokumentu");
            yield return PobierzWartoscSciezki(zapisVat, "DokumentKsiegowy.NumerDokumentu");
            yield return PobierzWartoscSciezki(zapisVat, "DokumentKsiegowy.NumerPelny");
            yield return PobierzWartoscSciezki(zapisVat, "DokumentKsiegowy.NumerWewnetrzny");
            yield return PobierzWartoscSciezki(zapisVat, "ZapisKsiegowy.NumerDokumentu");
        }

        private bool CzyPowiazanyZDdk(object encja, Guid dokumentDoKsiegowaniaId)
        {
            try
            {
                object zrodlowy = PobierzWlasciwosc(encja, "ZrodlowyDokumentDoKsiegowania");
                object id = PobierzWlasciwosc(zrodlowy, "Id");
                if (id is Guid guid && guid == dokumentDoKsiegowaniaId)
                {
                    return true;
                }
            }
            catch { }

            try
            {
                object docelowy = PobierzWlasciwosc(encja, "DocelowyDokumentDoKsiegowania");
                object id = PobierzWlasciwosc(docelowy, "Id");
                if (id is Guid guid && guid == dokumentDoKsiegowaniaId)
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private void DodajUnikalny(List<object> lista, object encja)
        {
            DodajUnikalny(lista, encja, null, null);
        }

        private void DodajUnikalny(List<object> lista, object encja, List<string> opisZrodel, string zrodlo)
        {
            if (lista == null || encja == null) return;

            string id = PobierzWlasciwosc(encja, "Id")?.ToString();
            if (!string.IsNullOrWhiteSpace(id) && lista.Any(x => string.Equals(PobierzWlasciwosc(x, "Id")?.ToString(), id, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (lista.Any(x => ReferenceEquals(x, encja)))
            {
                return;
            }

            lista.Add(encja);
            if (!string.IsNullOrWhiteSpace(zrodlo))
            {
                opisZrodel?.Add($"{zrodlo}->VAT#{id ?? "bezId"}");
            }
        }

        private bool UstawKodNaZapisieVat(object mgrVat, object zapisVat, byte wartoscKodu, string kod, out string szczegoly)
        {
            szczegoly = "brak";
            dynamic zapisBO = null;

            try
            {
                dynamic menedzerVat = mgrVat;
                zapisBO = menedzerVat.Znajdz((dynamic)zapisVat);
                if (zapisBO == null)
                {
                    szczegoly = "Nie udało się otworzyć zapisu VAT przez IZapisyWEwidencjiVAT.Znajdz().";
                    return false;
                }

                object dane = zapisBO.Dane;
                string numerKsef = OczyscNumerKsef(PobierzWlasciwosc(dane, "NumerKSeF")?.ToString());
                if (!string.IsNullOrWhiteSpace(numerKsef))
                {
                    szczegoly = $"Zapis VAT ma już realny numer KSeF {numerKsef}; kod {kod} pozostawiono pusty.";
                    return true;
                }

                byte? obecnaWartosc = PobierzByteNullable(dane, "WystepowanieFakturKsef");
                if (obecnaWartosc.HasValue && obecnaWartosc.Value == wartoscKodu)
                {
                    szczegoly = $"Kod {kod} był już ustawiony na zapisie VAT.";
                    return true;
                }

                UstawWlasciwosc(dane, "WystepowanieFakturKsef", (byte?)wartoscKodu);
                bool zapisano = zapisBO.Zapisz();
                if (!zapisano)
                {
                    szczegoly = WyciagnijBledySfery(zapisBO);
                    return false;
                }

                byte? wartoscPoZapisie = PobierzByteNullable(dane, "WystepowanieFakturKsef");
                if (!wartoscPoZapisie.HasValue || wartoscPoZapisie.Value != wartoscKodu)
                {
                    szczegoly = $"Sfera zwróciła Zapisz=true, ale po zapisie odczytano WystepowanieFakturKsef={wartoscPoZapisie?.ToString() ?? "null"} zamiast {wartoscKodu}.";
                    return false;
                }

                szczegoly = $"Ustawiono kod {kod}.";
                return true;
            }
            catch (Exception ex)
            {
                szczegoly = ex.GetBaseException().Message;
                return false;
            }
        }

        private byte? PobierzByteNullable(object source, string propertyName)
        {
            object value = PobierzWlasciwosc(source, propertyName);
            if (value == null) return null;

            try
            {
                return Convert.ToByte(value);
            }
            catch
            {
                return null;
            }
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

        private void UstawWlasciwosc(object source, string propertyName, object value)
        {
            var prop = source?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop == null || !prop.CanWrite)
            {
                throw new InvalidOperationException($"Nie znaleziono zapisywalnej właściwości {propertyName} na {source?.GetType().FullName ?? "null"}.");
            }

            prop.SetValue(source, value);
        }

        private string WyciagnijBledySfery(dynamic obiektBO)
        {
            try
            {
                var bledy = ((IEnumerable<dynamic>)obiektBO.Bledy)
                    .Select(e =>
                    {
                        try { return (string)e.Komunikat ?? (string)e.Tresc ?? (string)e.Opis; }
                        catch { return e.ToString(); }
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (bledy.Count > 0) return string.Join(" | ", bledy);
            }
            catch { }

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

            return "brak szczegółów walidacji";
        }

        private DocumentProcessingReport ZnajdzRaport(List<DocumentProcessingReport> manifest, InvoiceMetadata meta)
        {
            if (manifest == null || meta == null) return null;
            string nr = InvoiceDocumentMatcher.Normalize(meta.InvoiceNumber);
            string nip = InvoiceDocumentMatcher.NormalizeNip(meta.VendorNip);
            string ksef = OczyscNumerKsef(meta.KsefNumber);

            return manifest.FirstOrDefault(d =>
                d.Source == "frontendPackage" &&
                InvoiceDocumentMatcher.Normalize(d.InvoiceNumber) == nr &&
                InvoiceDocumentMatcher.NormalizeNip(d.VendorNip) == nip &&
                (string.IsNullOrWhiteSpace(ksef) || string.Equals(OczyscNumerKsef(d.KsefNumber), ksef, StringComparison.OrdinalIgnoreCase)));
        }

        private void UstawStatusKsef(DocumentProcessingReport raport, string status, string warning)
        {
            if (raport == null) return;
            raport.KsefStatus = status;
            if (!string.IsNullOrWhiteSpace(warning))
            {
                ImportManifestService.DodajWarning(raport, warning);
            }
        }

        private void UstawStatusKoduKsef(DocumentProcessingReport raport, string status, string warning)
        {
            if (raport == null) return;
            raport.KsefCodeStatus = status;
            if (!string.IsNullOrWhiteSpace(warning))
            {
                ImportManifestService.DodajWarning(raport, warning);
            }
        }

        private List<dynamic> PobierzWynikoweZapisy(object wynikOperacji)
        {
            try
            {
                dynamic wynikOperacjiDyn = wynikOperacji;
                var wynikowe = (System.Collections.IEnumerable)wynikOperacjiDyn.WynikowePoprawneZapisy;
                return wynikowe?.Cast<dynamic>().ToList() ?? new List<dynamic>();
            }
            catch
            {
                return new List<dynamic>();
            }
        }

        private string OpiszWyniki(IEnumerable<dynamic> wyniki)
        {
            if (wyniki == null)
            {
                return "brak";
            }

            var opisy = new List<string>();
            foreach (var wynik in wyniki.Take(50))
            {
                object wynikObj = (object)wynik;
                opisy.Add($"{wynikObj?.GetType().Name ?? "brak"}: dokumentId={PobierzDokumentId(wynik)}");
            }

            return ListaDoLogu(opisy);
        }

        private object ZnajdzEncje(Dictionary<string, dynamic> menedzerowie, dynamic wynik)
        {
            string typ = wynik?.GetType().Name ?? "";
            dynamic mgr = null;

            if (ZawieraTyp(typ, "KPiR")) mgr = menedzerowie["KPiR"];
            else if (ZawieraTyp(typ, "VAT")) mgr = menedzerowie["Vat"];
            else if (ZawieraTyp(typ, "Dekret")) mgr = menedzerowie["Dekret"];
            else if (ZawieraTyp(typ, "EP")) mgr = menedzerowie["EP"];

            return ZnajdzFizycznaEncje(mgr, PobierzDokumentId(wynik));
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

        private dynamic PobierzMenedzera(string nazwaInterfejsu)
        {
            var typSzukany = ZnajdzTypInterfejsu(nazwaInterfejsu);
            if (typSzukany == null) return null;

            var metoda = _sfera.GetType().GetMethods().FirstOrDefault(m => m.Name == "PodajObiektTypu" && m.IsGenericMethod && m.GetParameters().Length == 0);
            return metoda?.MakeGenericMethod(typSzukany).Invoke(_sfera, null);
        }

        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                var typ = types?.FirstOrDefault(x => x != null && x.Name == nazwa && x.IsInterface);
                if (typ != null) return typ;
            }

            return null;
        }

        private object PobierzDokumentId(dynamic wynik)
        {
            try { return wynik?.DokumentId; } catch { return null; }
        }

        private string PobierzNumerKsef(object obiekt)
        {
            if (obiekt == null) return null;

            return PobierzWartoscSciezki(obiekt, "NumerKSeF")
                ?? PobierzWartoscSciezki(obiekt, "ZapisKsiegowy.NumerKSeF");
        }

        private string PobierzWartoscSciezki(object obiekt, string sciezka)
        {
            object aktualny = obiekt;
            foreach (string nazwaWlasciwosci in sciezka.Split('.'))
            {
                if (aktualny == null) return null;

                var prop = aktualny.GetType().GetProperty(nazwaWlasciwosci, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null) return null;

                try { aktualny = prop.GetValue(aktualny); }
                catch { return null; }
            }

            return aktualny?.ToString();
        }

        private string OczyscNumerKsef(string value)
        {
            string cleaned = value?.Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return null;
            return PusteLubTechniczneKodyKsef.Contains(cleaned) ? null : cleaned;
        }

        private string OczyscKodKsef(string value)
        {
            string cleaned = value?.Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return null;

            string normalized = cleaned.ToUpperInvariant();
            if (normalized == "BRAK" || normalized == "NONE" || normalized == "NULL" || normalized == "NIE DOTYCZY")
            {
                return null;
            }

            return normalized;
        }

        private bool TryMapujKodKsef(string kod, out byte wartosc)
        {
            switch (OczyscKodKsef(kod))
            {
                case "BFK":
                    wartosc = (byte)WystepowanieFakturKSeF.BFK;
                    return true;
                case "OFF":
                    wartosc = (byte)WystepowanieFakturKSeF.OFF;
                    return true;
                case "DI":
                    wartosc = (byte)WystepowanieFakturKSeF.DI;
                    return true;
                default:
                    wartosc = 0;
                    return false;
            }
        }

        private bool PorownajKsef(string left, string right)
        {
            return string.Equals(OczyscNumerKsef(left), OczyscNumerKsef(right), StringComparison.OrdinalIgnoreCase);
        }

        private string OpiszMetadaneKodu(InvoiceMetadata meta)
        {
            if (meta == null) return "brak";

            return $"invoice={meta.InvoiceNumber ?? "brak"}, nip={meta.VendorNip ?? "brak"}, normalizedInvoice={InvoiceDocumentMatcher.Normalize(meta.InvoiceNumber)}, normalizedNip={InvoiceDocumentMatcher.NormalizeNip(meta.VendorNip)}, ksef={OczyscNumerKsef(meta.KsefNumber) ?? "brak"}, ksefCode={OczyscKodKsef(meta.KsefCode) ?? "brak"}, pdf={meta.PdfFileName ?? "brak"}";
        }

        private string OpiszRaportKodu(DocumentProcessingReport raport)
        {
            if (raport == null) return "brak";

            return $"source={raport.Source ?? "brak"}, invoice={raport.InvoiceNumber ?? "brak"}, nip={raport.VendorNip ?? "brak"}, waitingNr={raport.WaitingRoomNr?.ToString() ?? "brak"}, waitingId={raport.WaitingRoomId ?? "brak"}, waitingNumber={raport.WaitingRoomNumber ?? "brak"}, waitingNip={raport.WaitingRoomNip ?? "brak"}, match={raport.MatchStatus ?? "brak"}, waitingStatus={raport.WaitingRoomStatus ?? "brak"}, decree={raport.DecreeStatus ?? "brak"}, ksefStatus={raport.KsefStatus ?? "brak"}, ksefCode={raport.KsefCode ?? "brak"}, ksefCodeStatus={raport.KsefCodeStatus ?? "brak"}, pdf={raport.PdfFileName ?? "brak"}, attachment={raport.AttachmentStatus ?? "brak"}, warnings={raport.Warnings?.Count ?? 0}, resultEntries={raport.ResultEntries?.Count ?? 0}";
        }

        private string OpiszDokumentKodu(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return "brak";

            string typ = "brak";
            try { typ = dokument.TypDokumentuDoKsiegowania?.Nazwa ?? dokument.TypDokumentuDoKsiegowania?.GetType().Name ?? "brak"; }
            catch { }

            return $"nr={dokument.Nr}, id={dokument.Id}, numer={dokument.NumerDokumentu ?? "brak"}, nip={dokument.PodmiotHistoria?.NIP ?? "brak"}, ksef={dokument.NumerKSeF ?? "brak"}, statusKsiegowy={(int)dokument.StatusKsiegowy}, typ={typ}";
        }

        private string OpiszMatch(InvoiceMatchResult match)
        {
            if (match == null) return "brak";

            return $"status={match.Status ?? "brak"}, variant={match.MatchedVariant ?? "brak"}, reason={match.Reason ?? "brak"}, document={OpiszDokumentKodu(match.Document)}, candidates={ListaDoLogu(match.Candidates)}";
        }

        private string OpiszZapisyVat(IEnumerable<object> zapisyVat)
        {
            if (zapisyVat == null) return "brak";

            var opisy = zapisyVat
                .Take(50)
                .Select(OpiszZapisVat)
                .ToList();

            return opisy.Count == 0 ? "brak" : string.Join(" || ", opisy);
        }

        private string OpiszZapisVat(object zapisVat)
        {
            if (zapisVat == null) return "brak";

            object id = PobierzWlasciwosc(zapisVat, "Id");
            string typ = zapisVat.GetType().Name;
            string numer = PobierzWartoscSciezki(zapisVat, "NumerDokumentu")
                ?? PobierzWartoscSciezki(zapisVat, "DokumentKsiegowy.NumerDokumentu")
                ?? PobierzWartoscSciezki(zapisVat, "DokumentKsiegowy.NumerPelny")
                ?? PobierzWartoscSciezki(zapisVat, "ZapisKsiegowy.NumerDokumentu");
            string nip = PobierzWartoscSciezki(zapisVat, "Podmiot.NIP")
                ?? PobierzWartoscSciezki(zapisVat, "PreviewNIP")
                ?? PobierzWartoscSciezki(zapisVat, "PodmiotHistoria.NIP");
            string ksef = PobierzNumerKsef(zapisVat);
            byte? kod = PobierzByteNullable(zapisVat, "WystepowanieFakturKsef");
            string ddkZrodlowy = PobierzWartoscSciezki(zapisVat, "ZrodlowyDokumentDoKsiegowania.Id");
            string ddkDocelowy = PobierzWartoscSciezki(zapisVat, "DocelowyDokumentDoKsiegowania.Id");

            return $"typ={typ}, id={id?.ToString() ?? "brak"}, numer={numer ?? "brak"}, nip={nip ?? "brak"}, ksef={OczyscNumerKsef(ksef) ?? "brak"}, kod={kod?.ToString() ?? "null"}, zrodlowyDDK={ddkZrodlowy ?? "brak"}, docelowyDDK={ddkDocelowy ?? "brak"}";
        }

        private string OpiszStatusyKodowManifestu(List<DocumentProcessingReport> manifest, IEnumerable<InvoiceMetadata> metadaneZKodami)
        {
            if (metadaneZKodami == null) return "brak";

            return ListaDoLogu(metadaneZKodami.Select(meta =>
            {
                var raport = ZnajdzRaport(manifest, meta);
                return $"{meta.InvoiceNumber}/{meta.VendorNip}->{OczyscKodKsef(meta.KsefCode) ?? "brak"}: {raport?.KsefCodeStatus ?? "brakRaportu"}; waiting={raport?.WaitingRoomNumber ?? "brak"}; decree={raport?.DecreeStatus ?? "brak"}";
            }));
        }

        private string ListaDoLogu(IEnumerable<string> items)
        {
            if (items == null) return "brak";
            var list = items.Where(x => !string.IsNullOrWhiteSpace(x)).Take(200).ToList();
            return list.Count == 0 ? "brak" : string.Join(" || ", list);
        }
    }
}
