using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ModelDanych;
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
    public class AttachmentService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<AttachmentService> _logger;

        public AttachmentService(Uchwyt sfera, ILogger<AttachmentService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task PodepnijZalacznikiAsync(
            ImportJob job,
            dynamic rezultat,
            List<Tuple<DokumentDoKsiegowania, SchematImportu>> zatwierdzone,
            List<DocumentProcessingReport> manifest,
            Func<int, string, Task> raportujPostep)
        {
            _logger.LogInformation("[DEBUG] Uruchomiono usługę załączników dla zadania: {JobId}", job.JobId);
            if (rezultat == null || zatwierdzone == null || zatwierdzone.Count == 0) return;

            await raportujPostep(95, "Podpinanie załączników (bezpieczne dopasowanie)...");
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

            _logger.LogInformation("[ZAŁĄCZNIKI ODEBRANE] JobId={JobId}; liczba={Count}; pliki={Pliki}",
                job.JobId,
                job.Attachments?.Count ?? 0,
                OpiszZalaczniki(job.Attachments));
            _logger.LogInformation("[ZAŁĄCZNIKI META] JobId={JobId}; liczba={Count}; metadane={Metadane}",
                job.JobId,
                job.InvoicesMetadata?.Count ?? 0,
                OpiszMetadane(job.InvoicesMetadata));
            _logger.LogInformation("[ZAŁĄCZNIKI KONTEKST] JobId={JobId}; zatwierdzone={Zatwierdzone}; wynikowe={Wynikowe}",
                job.JobId,
                zatwierdzone.Count,
                listaWynikow.Count);

            for (int i = 0; i < zatwierdzone.Count; i++)
            {
                var dok = zatwierdzone[i].Item1;
                string nrSystemowy = dok.NumerDokumentu ?? "";
                string nipSystemowy = dok.PodmiotHistoria?.NIP ?? "";
                var raport = ImportManifestService.ZnajdzRaportDlaDokumentu(manifest, dok);

                var zalacznik = ZnajdzZalacznik(job, dok, raport, out string attachmentMatchStatus);
                if (zalacznik == null)
                {
                    string wpis = $"BRAK DOPASOWANIA -> {nrSystemowy} ({nipSystemowy})";
                    niepodpieteZalaczniki.Add(wpis);
                    if (raport != null && raport.AttachmentStatus == "pending")
                    {
                        raport.AttachmentStatus = "notFound";
                        ImportManifestService.DodajWarning(raport, $"Nie znaleziono załącznika PDF dla dokumentu {nrSystemowy}. Dostępne pliki: {OpiszZalaczniki(job.Attachments)}");
                    }
                    _logger.LogWarning("[ZAŁĄCZNIK BRAK] Nie znaleziono dopasowania w metadanych ani attachments dla dokumentu: {Numer} (NIP: {Nip}). Dostępne pliki: {Pliki}",
                        nrSystemowy,
                        nipSystemowy,
                        OpiszZalaczniki(job.Attachments));
                    continue;
                }

                if (raport != null)
                {
                    raport.AttachmentStatus = "matched";
                }

                _logger.LogInformation("[ZAŁĄCZNIK DOPASOWANY] Dokument={Numer}; NIP={Nip}; plik={Plik}; documentNumber={DocumentNumber}; vendorNip={VendorNip}; bytes={Bytes}; match={Match}",
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
                _logger.LogInformation("[ZAŁĄCZNIK TEMP] Plik={Plik}; tempPath={TempPath}; bytes={Bytes}",
                    zalacznik.FileName,
                    tempPath,
                    zalacznik.Content?.Length ?? 0);

                try
                {
                    if (i >= listaWynikow.Count)
                    {
                        string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy}) | brak wyniku dekretacji pod indeksem {i}";
                        niepodpieteZalaczniki.Add(wpis);
                        if (raport != null)
                        {
                            raport.AttachmentStatus = "notAttached";
                            ImportManifestService.DodajWarning(raport, "Nie podpięto załącznika, bo dekretacja nie zwróciła odpowiadającego wyniku operacji.");
                        }
                        _logger.LogWarning("[ZAŁĄCZNIK BRAK WYNIKU] Plik={Plik}; Dokument={Numer}; NIP={Nip}; listaWynikowIndex={Index}",
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
                        if (raport != null)
                        {
                            raport.AttachmentStatus = "notAttached";
                            ImportManifestService.DodajWarning(raport, "Nie podpięto załącznika, bo wynik dekretacji nie zawierał WynikowePoprawneZapisy.");
                        }
                        _logger.LogWarning("[ZAŁĄCZNIK BRAK WYNIKÓW] Plik={Plik}; Dokument={Numer}; NIP={Nip}; listaWynikowIndex={Index}",
                            zalacznik.FileName,
                            nrSystemowy,
                            nipSystemowy,
                            i);
                        continue;
                    }

                    var wynikowe = ((System.Collections.IEnumerable)dokumentyWynikowe).Cast<dynamic>().ToList();
                    _logger.LogInformation("[ZAŁĄCZNIK WYNIKOWE] Plik={Plik}; Dokument={Numer}; liczba={Count}; wyniki={Wyniki}",
                        zalacznik.FileName,
                        nrSystemowy,
                        wynikowe.Count,
                        OpiszWyniki(wynikowe));

                    using (var zalacznikBO = bibliotekaZalacznikow.Utworz())
                    {
                        zalacznikBO.Wczytaj(tempPath);
                        zalacznikBO.Dane.Opis = "Oryginał ze Scanye";

                        bool podpieto = false;
                        var encjeDoPowiazania = new List<object>();
                        foreach (var wynik in wynikowe)
                        {
                            object wynikObj = (object)wynik;
                            string typWyniku = wynikObj?.GetType().Name;
                            object dokumentId = PobierzDokumentId(wynik);
                            object encja = ZnajdzEncje(menedzerowie, wynik, dok);
                            if (encja != null)
                            {
                                zalacznikBO.DodajPowiazanie((dynamic)encja);
                                encjeDoPowiazania.Add(encja);
                                podpieto = true;
                                _logger.LogInformation("[ZAŁĄCZNIK POWIĄZANIE] Plik={Plik}; Dokument={Numer}; wynikTyp={WynikTyp}; dokumentId={DokumentId}; encjaTyp={EncjaTyp}",
                                    zalacznik.FileName,
                                    nrSystemowy,
                                    typWyniku,
                                    dokumentId,
                                    encja.GetType().FullName);
                            }
                            else
                            {
                                _logger.LogWarning("[ZAŁĄCZNIK BRAK ENCJI] Plik={Plik}; Dokument={Numer}; wynikTyp={WynikTyp}; dokumentId={DokumentId}",
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
                            if (raport != null)
                            {
                                raport.AttachmentStatus = "notAttached";
                                ImportManifestService.DodajWarning(raport, "Nie podpięto załącznika, bo nie znaleziono encji wynikowych do powiązania.");
                            }
                            _logger.LogWarning("[ZAŁĄCZNIK BEZ POWIĄZAŃ] Plik={Plik}; Dokument={Numer}; NIP={Nip}", zalacznik.FileName, nrSystemowy, nipSystemowy);
                        }
                        else
                        {
                            bool zapisano = zalacznikBO.Zapisz();
                            if (zapisano)
                            {
                                string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                                podpieteZalaczniki.Add(wpis);
                                if (raport != null)
                                {
                                    raport.AttachmentStatus = "attached";
                                }
                                _logger.LogInformation("[ZAŁĄCZNIK SUKCES] Podpięto plik={Plik} pod nazwą '{Skan}' dla dokumentu={Numer}; NIP={Nip}",
                                    zalacznik.FileName,
                                    bezpiecznaNazwa,
                                    nrSystemowy,
                                    nipSystemowy);
                            }
                            else
                            {
                                string bledy = WyciagnijBledySfery(zalacznikBO);
                                int zapisaneFallback = ZapiszZalacznikiOsobno(bibliotekaZalacznikow, tempPath, encjeDoPowiazania, out string fallbackBledy);
                                string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";

                                if (zapisaneFallback > 0)
                                {
                                    podpieteZalaczniki.Add(wpis + $" | fallback {zapisaneFallback}/{encjeDoPowiazania.Count}");
                                    if (raport != null)
                                    {
                                        raport.AttachmentStatus = zapisaneFallback == encjeDoPowiazania.Count ? "attached" : "attachedPartial";
                                        if (zapisaneFallback != encjeDoPowiazania.Count)
                                        {
                                            ImportManifestService.DodajWarning(raport, $"Załącznik zapisano tylko częściowo fallbackiem: {zapisaneFallback}/{encjeDoPowiazania.Count}. Błędy: {fallbackBledy}");
                                        }
                                    }

                                    _logger.LogWarning("[ZAŁĄCZNIK SUKCES FALLBACK] Plik={Plik}; Dokument={Numer}; NIP={Nip}; zapisane={Saved}/{Total}; pierwotnyBlad={Bledy}; fallbackBledy={FallbackBledy}",
                                        zalacznik.FileName,
                                        nrSystemowy,
                                        nipSystemowy,
                                        zapisaneFallback,
                                        encjeDoPowiazania.Count,
                                        bledy,
                                        fallbackBledy);
                                }
                                else
                                {
                                    niepodpieteZalaczniki.Add(wpis + $" | Zapisz=false | {bledy} | fallback=false | {fallbackBledy}");
                                    if (raport != null)
                                    {
                                        raport.AttachmentStatus = "notAttached";
                                        ImportManifestService.DodajWarning(raport, $"Nie udało się zapisać załącznika w Sferze: {bledy}. Fallback: {fallbackBledy}");
                                    }
                                    _logger.LogWarning("[ZAŁĄCZNIK ZAPIS NIEUDANY] Plik={Plik}; Dokument={Numer}; NIP={Nip}; Błędy={Bledy}; fallbackBledy={FallbackBledy}",
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

            _logger.LogInformation("[ZAŁĄCZNIKI PODSUMOWANIE] JobId={JobId}; podpięte={PodpieteCount}: {Podpiete}; niepodpięte={NiepodpieteCount}: {Niepodpiete}",
                job.JobId,
                podpieteZalaczniki.Count,
                ListaDoLogu(podpieteZalaczniki),
                niepodpieteZalaczniki.Count,
                ListaDoLogu(niepodpieteZalaczniki));
        }

        private int ZapiszZalacznikiOsobno(dynamic bibliotekaZalacznikow, string tempPath, IEnumerable<object> encje, out string bledy)
        {
            var errors = new List<string>();
            int saved = 0;

            foreach (var encja in encje ?? Enumerable.Empty<object>())
            {
                try
                {
                    using (var pojedynczyBO = bibliotekaZalacznikow.Utworz())
                    {
                        pojedynczyBO.Wczytaj(tempPath);
                        pojedynczyBO.Dane.Opis = "Oryginał ze Scanye";
                        pojedynczyBO.DodajPowiazanie((dynamic)encja);

                        if (pojedynczyBO.Zapisz())
                        {
                            saved++;
                        }
                        else
                        {
                            errors.Add($"{encja.GetType().Name}: {WyciagnijBledySfery(pojedynczyBO)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{encja.GetType().Name}: {ex.GetBaseException().Message}");
                }
            }

            bledy = errors.Count == 0 ? "brak" : string.Join(" | ", errors);
            return saved;
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
                _logger.LogInformation("[ZAŁĄCZNIK VAT FALLBACK] Znaleziono zapis VAT przez relację DokumentDoKsiegowania. Dokument={Numer}; wynikDokumentId={DokumentId}; encjaTyp={EncjaTyp}",
                    dokumentZrodlowy?.NumerDokumentu,
                    dokumentId,
                    encja.GetType().FullName);
                return encja;
            }

            encja = ZnajdzVatPoPowiazaniuZDdk(mgrVat, dokumentZrodlowy, dokumentId);
            if (encja != null)
            {
                _logger.LogInformation("[ZAŁĄCZNIK VAT FALLBACK] Znaleziono zapis VAT przez powiązanie Zrodlowy/DocelowyDokumentDoKsiegowania. Dokument={Numer}; wynikDokumentId={DokumentId}; encjaTyp={EncjaTyp}",
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

        private dynamic PobierzMenedzera(string nazwaInterfejsu)
        {
            var typSzukany = ZnajdzTypInterfejsu(nazwaInterfejsu);
            if (typSzukany != null)
            {
                var metoda = _sfera.GetType().GetMethods().FirstOrDefault(m => m.Name == "PodajObiektTypu" && m.IsGenericMethod && m.GetParameters().Length == 0);
                if (metoda != null) return metoda.MakeGenericMethod(typSzukany).Invoke(_sfera, null);
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
    }
}


