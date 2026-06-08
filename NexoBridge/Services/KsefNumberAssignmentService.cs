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
            "BFK",
            "DI",
            "OFF",
            "BRAK",
            "NONE",
            "NULL",
            "NIE DOTYCZY"
        };

        private readonly Uchwyt _sfera;
        private readonly ILogger<KsefNumberAssignmentService> _logger;

        public KsefNumberAssignmentService(Uchwyt sfera, ILogger<KsefNumberAssignmentService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task<int> PrzypiszPrzedDekretacjaAsync(ImportJob job, Func<int, string, Task> raportujPostep)
        {
            var metadaneZKsef = PobierzMetadaneZKsef(job).ToList();
            if (metadaneZKsef.Count == 0)
            {
                _logger.LogInformation("[KSEF] Brak numerów KSeF w metadanych zadania {JobId}. Pomijam etap weryfikacji Poczekalni.", job.JobId);
                return 0;
            }

            await raportujPostep(65, "Weryfikacja numerów KSeF w Poczekalni...");

            var menedzerDokumentow = _sfera.PodajObiektTypu<IDokumentyDoKsiegowania>();
            var oczekujace = menedzerDokumentow.Dane.Wszystkie()
                .Where(d => (int)d.StatusKsiegowy == 2)
                .ToList();

            if (oczekujace.Count == 0)
            {
                throw new Exception("Otrzymano numery KSeF, ale w Poczekalni nie ma dokumentów do księgowania.");
            }

            var plan = ZbudujPlanPrzypisania(oczekujace, metadaneZKsef);
            int potwierdzone = 0;

            foreach (var pozycja in plan)
            {
                string obecnyKsef = OczyscNumerKsef(pozycja.Dokument.NumerKSeF);
                if (PorownajKsef(obecnyKsef, pozycja.KsefNumber))
                {
                    potwierdzone++;
                    _logger.LogInformation("[KSEF OK] Dokument w Poczekalni {Numer} (NIP: {Nip}) ma numer KSeF {Ksef}.",
                        pozycja.Dokument.NumerDokumentu,
                        pozycja.Dokument.PodmiotHistoria?.NIP,
                        obecnyKsef);
                    continue;
                }

                throw new Exception($"Numer KSeF nie trafił do dokumentu w Poczekalni dla faktury {pozycja.InvoiceNumber} (NIP: {pozycja.VendorNip}). Oczekiwano: {pozycja.KsefNumber}, odczytano: {obecnyKsef ?? "brak"}.");
            }

            _logger.LogInformation("[KSEF PODSUMOWANIE] JobId={JobId}; otrzymane={Otrzymane}; potwierdzoneWPoczekalni={Potwierdzone}.",
                job.JobId,
                metadaneZKsef.Count,
                potwierdzone);

            return potwierdzone;
        }

        public async Task ZweryfikujPoDekretacjiAsync(
            ImportJob job,
            dynamic rezultat,
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> zatwierdzone,
            Func<int, string, Task> raportujPostep)
        {
            var metadaneZKsef = PobierzMetadaneZKsef(job).ToList();
            if (metadaneZKsef.Count == 0)
            {
                return;
            }

            if (rezultat == null || zatwierdzone == null || zatwierdzone.Count == 0)
            {
                throw new Exception("Otrzymano numery KSeF, ale dekretacja nie zwróciła dokumentów do weryfikacji.");
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

            var bledy = new List<string>();
            var sprawdzoneMetadane = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int sprawdzone = 0;

            for (int i = 0; i < zatwierdzone.Count; i++)
            {
                var dokumentZrodlowy = zatwierdzone[i].Item1;
                var meta = ZnajdzMetadaneDlaDokumentu(metadaneZKsef, dokumentZrodlowy);
                if (meta == null)
                {
                    continue;
                }

                string oczekiwanyKsef = OczyscNumerKsef(meta.KsefNumber);
                string kluczMetadanych = KluczMetadanych(meta);

                if (i >= listaWynikow.Count)
                {
                    bledy.Add($"{dokumentZrodlowy.NumerDokumentu}: brak wyniku dekretacji pod indeksem {i}");
                    continue;
                }

                var wynikowe = PobierzWynikoweZapisy(listaWynikow[i]);
                if (wynikowe.Count == 0)
                {
                    bledy.Add($"{dokumentZrodlowy.NumerDokumentu}: brak wynikowych zapisów księgowych");
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
                    bledy.Add($"{dokumentZrodlowy.NumerDokumentu}: oczekiwano {oczekiwanyKsef}, niezgodne wyniki: {string.Join("; ", niezgodneWyniki)}");
                }
                else
                {
                    sprawdzone++;
                    sprawdzoneMetadane.Add(kluczMetadanych);
                    _logger.LogInformation("[KSEF WERYFIKACJA OK] Dokument {Numer} zachował KSeF {Ksef}. Wyniki: {Wyniki}",
                        dokumentZrodlowy.NumerDokumentu,
                        oczekiwanyKsef,
                        string.Join("; ", opisWynikow));
                }
            }

            var brakujaceMetadane = metadaneZKsef
                .Where(m => !sprawdzoneMetadane.Contains(KluczMetadanych(m)))
                .Select(m => $"{m.InvoiceNumber} (NIP: {m.VendorNip}, KSeF: {OczyscNumerKsef(m.KsefNumber)})")
                .ToList();

            if (brakujaceMetadane.Count > 0)
            {
                bledy.Add("faktury z KSeF bez potwierdzonego zapisu po dekretacji: " + string.Join("; ", brakujaceMetadane));
            }

            if (bledy.Count > 0)
            {
                throw new Exception("Nie potwierdzono numerów KSeF po dekretacji: " + string.Join(" | ", bledy));
            }

            _logger.LogInformation("[KSEF WERYFIKACJA PODSUMOWANIE] JobId={JobId}; sprawdzone={Sprawdzone}.",
                job.JobId,
                sprawdzone);
        }

        private List<KsefAssignmentPlanItem> ZbudujPlanPrzypisania(List<DokumentDoKsiegowania> oczekujace, List<InvoiceMetadata> metadaneZKsef)
        {
            var plan = new List<KsefAssignmentPlanItem>();
            var dokumentyWPlanie = new Dictionary<int, KsefAssignmentPlanItem>();

            foreach (var meta in metadaneZKsef)
            {
                string ksefNumber = OczyscNumerKsef(meta.KsefNumber);
                if (string.IsNullOrWhiteSpace(meta.InvoiceNumber) || string.IsNullOrWhiteSpace(meta.VendorNip))
                {
                    throw new Exception($"Otrzymano numer KSeF {ksefNumber}, ale brakuje numeru faktury lub NIP kontrahenta w metadanych.");
                }

                var wszystkieTrafienia = oczekujace
                    .Where(d => PasujeDokument(d, meta, wymagajDokladnegoNumeru: false))
                    .ToList();

                var dokladneTrafienia = wszystkieTrafienia
                    .Where(d => PasujeDokument(d, meta, wymagajDokladnegoNumeru: true))
                    .ToList();

                var kandydaci = dokladneTrafienia.Count > 0 ? dokladneTrafienia : wszystkieTrafienia;

                if (kandydaci.Count == 0)
                {
                    throw new Exception($"Nie znaleziono dokumentu w Poczekalni dla faktury {meta.InvoiceNumber} (NIP: {meta.VendorNip}) z numerem KSeF {ksefNumber}.");
                }

                if (kandydaci.Count > 1)
                {
                    throw new Exception($"Nie mogę bezpiecznie przypisać KSeF {ksefNumber} do faktury {meta.InvoiceNumber} (NIP: {meta.VendorNip}) - znaleziono {kandydaci.Count} pasujących dokumentów: {OpiszDokumenty(kandydaci)}.");
                }

                var dokument = kandydaci[0];
                string obecnyKsef = OczyscNumerKsef(dokument.NumerKSeF);
                if (!string.IsNullOrWhiteSpace(obecnyKsef) && !PorownajKsef(obecnyKsef, ksefNumber))
                {
                    throw new Exception($"Dokument {dokument.NumerDokumentu} (NIP: {dokument.PodmiotHistoria?.NIP}) ma już inny numer KSeF: {obecnyKsef}, oczekiwano: {ksefNumber}.");
                }

                var pozycja = new KsefAssignmentPlanItem(dokument, meta.InvoiceNumber, meta.VendorNip, ksefNumber);
                if (dokumentyWPlanie.TryGetValue(dokument.Nr, out var istniejaca))
                {
                    throw new Exception($"Dwa wpisy metadanych wskazują na ten sam dokument w Poczekalni: {dokument.NumerDokumentu}. KSeF: {istniejaca.KsefNumber} oraz {ksefNumber}.");
                }

                dokumentyWPlanie[dokument.Nr] = pozycja;
                plan.Add(pozycja);
            }

            _logger.LogInformation("[KSEF PLAN] Przygotowano plan przypisania dla {Count} dokumentów: {Plan}",
                plan.Count,
                string.Join(" || ", plan.Select(p => $"{p.InvoiceNumber}/{p.VendorNip}->{p.KsefNumber}")));

            return plan;
        }

        private IEnumerable<InvoiceMetadata> PobierzMetadaneZKsef(ImportJob job)
        {
            return (job.InvoicesMetadata ?? new List<InvoiceMetadata>())
                .Where(m => !string.IsNullOrWhiteSpace(OczyscNumerKsef(m.KsefNumber)));
        }

        private InvoiceMetadata ZnajdzMetadaneDlaDokumentu(IEnumerable<InvoiceMetadata> metadane, DokumentDoKsiegowania dokument)
        {
            return metadane.FirstOrDefault(meta => PasujeDokument(dokument, meta, wymagajDokladnegoNumeru: false));
        }

        private bool PasujeDokument(DokumentDoKsiegowania dokument, InvoiceMetadata meta, bool wymagajDokladnegoNumeru)
        {
            string nrSystemowy = Normalizuj(dokument.NumerDokumentu);
            string nipSystemowy = Normalizuj(dokument.PodmiotHistoria?.NIP).Replace("pl", "");
            string nrFront = Normalizuj(meta.InvoiceNumber);
            string nipFront = Normalizuj(meta.VendorNip).Replace("pl", "");

            if (string.IsNullOrEmpty(nrSystemowy) || string.IsNullOrEmpty(nipSystemowy) ||
                string.IsNullOrEmpty(nrFront) || string.IsNullOrEmpty(nipFront))
            {
                return false;
            }

            bool numerPasuje = wymagajDokladnegoNumeru
                ? nrSystemowy == nrFront
                : nrSystemowy.EndsWith(nrFront);

            return numerPasuje && nipSystemowy.EndsWith(nipFront);
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

        private string KluczMetadanych(InvoiceMetadata meta)
        {
            return $"{Normalizuj(meta.InvoiceNumber)}|{Normalizuj(meta.VendorNip).Replace("pl", "")}|{OczyscNumerKsef(meta.KsefNumber)}";
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

        private string Normalizuj(string input)
        {
            return input == null ? "" : new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private string OpiszDokumenty(IEnumerable<DokumentDoKsiegowania> dokumenty)
        {
            return string.Join(" || ", dokumenty.Take(20).Select(d => $"Nr={d.Nr}, numer={d.NumerDokumentu}, nip={d.PodmiotHistoria?.NIP}, ksef={d.NumerKSeF ?? "brak"}"));
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

                if (bledy.Any()) return string.Join(" | ", bledy);
            }
            catch { }

            return "brak szczegółów błędów Sfery";
        }

        private class KsefAssignmentPlanItem
        {
            public KsefAssignmentPlanItem(DokumentDoKsiegowania dokument, string invoiceNumber, string vendorNip, string ksefNumber)
            {
                Dokument = dokument;
                InvoiceNumber = invoiceNumber;
                VendorNip = vendorNip;
                KsefNumber = ksefNumber;
            }

            public DokumentDoKsiegowania Dokument { get; }
            public string InvoiceNumber { get; }
            public string VendorNip { get; }
            public string KsefNumber { get; }
        }
    }
}
