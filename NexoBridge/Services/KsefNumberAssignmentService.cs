using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
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
            var oczekujace = menedzerDokumentow.Dane.Wszystkie()
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
            dynamic rezultat,
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

            var listaWynikow = ((System.Collections.IEnumerable)rezultat).Cast<dynamic>().ToList();
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
                    ? new InvoiceMetadata { InvoiceNumber = raport.InvoiceNumber, VendorNip = raport.VendorNip, KsefNumber = raport.KsefNumber, PdfFileName = raport.PdfFileName }
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

                var wynikowe = PobierzWynikoweZapisy(listaWynikow[i]);
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

        private IEnumerable<InvoiceMetadata> PobierzMetadaneZKsef(ImportJob job)
        {
            return (job.InvoicesMetadata ?? new List<InvoiceMetadata>())
                .Where(m => !string.IsNullOrWhiteSpace(OczyscNumerKsef(m.KsefNumber)));
        }

        private InvoiceMetadata ZnajdzMetadaneDlaDokumentu(IEnumerable<InvoiceMetadata> metadane, DokumentDoKsiegowania dokument)
        {
            var match = InvoiceDocumentMatcher.MatchMetadataForDocument(metadane, dokument);
            return match.Metadata;
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

        private List<dynamic> PobierzWynikoweZapisy(dynamic wynikOperacji)
        {
            try
            {
                var wynikowe = (System.Collections.IEnumerable)wynikOperacji.WynikowePoprawneZapisy;
                return wynikowe?.Cast<dynamic>().ToList() ?? new List<dynamic>();
            }
            catch
            {
                return new List<dynamic>();
            }
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

        private bool PorownajKsef(string left, string right)
        {
            return string.Equals(OczyscNumerKsef(left), OczyscNumerKsef(right), StringComparison.OrdinalIgnoreCase);
        }

        private string ListaDoLogu(IEnumerable<string> items)
        {
            if (items == null) return "brak";
            var list = items.Where(x => !string.IsNullOrWhiteSpace(x)).Take(200).ToList();
            return list.Count == 0 ? "brak" : string.Join(" || ", list);
        }
    }
}
