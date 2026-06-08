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

        public async Task PodepnijZalacznikiAsync(ImportJob job, dynamic rezultat, List<Tuple<DokumentDoKsiegowania, InsERT.Moria.ModelDanych.SchematImportu>> zatwierdzone, Func<int, string, Task> raportujPostep)
        {
            _logger.LogInformation("[DEBUG] Uruchomiono usługę załączników dla zadania: {JobId}", job.JobId);
            if (rezultat == null || zatwierdzone == null || zatwierdzone.Count == 0) return;

            await raportujPostep(95, "Podpinanie załączników (NIP + Numer)...");
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

                string czystyNrSystemowy = Normalizuj(nrSystemowy);
                string czystyNipSystemowy = Normalizuj(nipSystemowy);

                var meta = job.InvoicesMetadata?.FirstOrDefault(m =>
                {
                    string czystyNrFront = Normalizuj(m.InvoiceNumber);
                    string czystyNipFront = Normalizuj(m.VendorNip).Replace("pl", "");

                    if (string.IsNullOrEmpty(czystyNrFront) || string.IsNullOrEmpty(czystyNipFront)) return false;

                    return czystyNrSystemowy.EndsWith(czystyNrFront) &&
                           czystyNipSystemowy.EndsWith(czystyNipFront);
                });

                var zalacznik = job.Attachments?.FirstOrDefault(z => meta != null && z.FileName == meta.PdfFileName);
                if (zalacznik == null)
                {
                    zalacznik = job.Attachments?.FirstOrDefault(z => PasujeZalacznikDoDokumentu(z, czystyNrSystemowy, czystyNipSystemowy));
                }

                if (zalacznik != null)
                {
                    _logger.LogInformation("[ZAŁĄCZNIK DOPASOWANY] Dokument={Numer}; NIP={Nip}; plik={Plik}; documentNumber={DocumentNumber}; vendorNip={VendorNip}; bytes={Bytes}",
                        nrSystemowy,
                        nipSystemowy,
                        zalacznik.FileName,
                        zalacznik.DocumentNumber,
                        zalacznik.VendorNip,
                        zalacznik.Content?.Length ?? 0);

                    // ========================================================
                    // NOWOŚĆ: Piękna nazwa załącznika + izolacja w unikalnym folderze
                    // ========================================================

                    // 1. Czyścimy numer z niedozwolonych znaków (np. ukośników z "FV 1/2026")
                    string bezpiecznaNazwa = nrSystemowy.Replace("/", "_").Replace("\\", "_").Replace(":", "_").Replace(" ", "_");
                    bezpiecznaNazwa = string.Join("_", bezpiecznaNazwa.Split(Path.GetInvalidFileNameChars()));
                    if (string.IsNullOrWhiteSpace(bezpiecznaNazwa)) bezpiecznaNazwa = $"Skan_{Guid.NewGuid():N}";

                    // 2. Unikalny folder tymczasowy zapobiega kolizjom na dysku
                    string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    // 3. Pełna ścieżka - plik nazywa się dokładnie jak faktura!
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
                        dynamic dokumentyWynikowe = listaWynikow[i].WynikowePoprawneZapisy;
                        if (dokumentyWynikowe == null)
                        {
                            string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                            niepodpieteZalaczniki.Add(wpis + " | brak WynikowePoprawneZapisy");
                            _logger.LogWarning("[ZAŁĄCZNIK BRAK WYNIKÓW] Plik={Plik}; Dokument={Numer}; NIP={Nip}; listaWynikowIndex={Index}",
                                zalacznik.FileName,
                                nrSystemowy,
                                nipSystemowy,
                                i);
                        }
                        else
                        {
                            var wynikowe = ((System.Collections.IEnumerable)dokumentyWynikowe).Cast<dynamic>().ToList();
                            _logger.LogInformation("[ZAŁĄCZNIK WYNIKOWE] Plik={Plik}; Dokument={Numer}; liczba={Count}; wyniki={Wyniki}",
                                zalacznik.FileName,
                                nrSystemowy,
                                wynikowe.Count,
                                OpiszWyniki(wynikowe));

                            using (var zalacznikBO = bibliotekaZalacznikow.Utworz())
                            {
                                // Wczytaj() automatycznie nada plikowi nazwę wyciągniętą z tempPath (czyli ładny numer)
                                zalacznikBO.Wczytaj(tempPath);
                                zalacznikBO.Dane.Opis = "Oryginał ze Scanye";

                                bool podpieto = false;
                                foreach (var wynik in wynikowe)
                                {
                                    object wynikObj = (object)wynik;
                                    string typWyniku = wynikObj?.GetType().Name;
                                    object dokumentId = PobierzDokumentId(wynik);
                                    object encja = ZnajdzEncje(menedzerowie, wynik);
                                    if (encja != null)
                                    {
                                        zalacznikBO.DodajPowiazanie((dynamic)encja);
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
                                    _logger.LogWarning("[ZAŁĄCZNIK BEZ POWIĄZAŃ] Plik={Plik}; Dokument={Numer}; NIP={Nip}", zalacznik.FileName, nrSystemowy, nipSystemowy);
                                }
                                else
                                {
                                    bool zapisano = zalacznikBO.Zapisz();
                                    if (zapisano)
                                    {
                                        string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                                        podpieteZalaczniki.Add(wpis);
                                        _logger.LogInformation("[ZAŁĄCZNIK SUKCES] Podpięto plik={Plik} pod nazwą '{Skan}' dla dokumentu={Numer}; NIP={Nip}",
                                            zalacznik.FileName,
                                            bezpiecznaNazwa,
                                            nrSystemowy,
                                            nipSystemowy);
                                    }
                                    else
                                    {
                                        string bledy = WyciagnijBledySfery(zalacznikBO);
                                        string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                                        niepodpieteZalaczniki.Add(wpis + $" | Zapisz=false | {bledy}");
                                        _logger.LogWarning("[ZAŁĄCZNIK ZAPIS NIEUDANY] Plik={Plik}; Dokument={Numer}; NIP={Nip}; Błędy={Bledy}",
                                            zalacznik.FileName,
                                            nrSystemowy,
                                            nipSystemowy,
                                            bledy);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string wpis = $"{zalacznik.FileName} -> {nrSystemowy} ({nipSystemowy})";
                        niepodpieteZalaczniki.Add(wpis + $" | wyjątek: {ex.GetBaseException().Message}");
                        _logger.LogError(ex, "[ZAŁĄCZNIK BŁĄD] Wystąpił wyjątek podczas podpinania pliku '{Skan}' do dokumentu: {Numer}; plik={Plik}; NIP={Nip}",
                            bezpiecznaNazwa,
                            nrSystemowy,
                            zalacznik.FileName,
                            nipSystemowy);
                    }
                    finally
                    {
                        // Sprzątamy zarówno plik, jak i nasz folder izolujący
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                        if (Directory.Exists(tempDir)) Directory.Delete(tempDir);
                    }
                }
                else
                {
                    string wpis = $"BRAK DOPASOWANIA -> {nrSystemowy} ({nipSystemowy})";
                    niepodpieteZalaczniki.Add(wpis);
                    _logger.LogWarning("[ZAŁĄCZNIK BRAK] Nie znaleziono dopasowania w metadanych ani attachments dla dokumentu: {Numer} (NIP: {Nip}). Dostępne pliki: {Pliki}",
                        nrSystemowy,
                        nipSystemowy,
                        OpiszZalaczniki(job.Attachments));
                }
            }

            _logger.LogInformation("[ZAŁĄCZNIKI PODSUMOWANIE] JobId={JobId}; podpięte={PodpieteCount}: {Podpiete}; niepodpięte={NiepodpieteCount}: {Niepodpiete}",
                job.JobId,
                podpieteZalaczniki.Count,
                ListaDoLogu(podpieteZalaczniki),
                niepodpieteZalaczniki.Count,
                ListaDoLogu(niepodpieteZalaczniki));
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
                .Select(m => $"invoiceNumber={m.InvoiceNumber}, vendorNip={m.VendorNip}, ksefNumber={m.KsefNumber}, pdfFileName={m.PdfFileName}")
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
        private bool PasujeZalacznikDoDokumentu(AttachmentPayload zalacznik, string czystyNrSystemowy, string czystyNipSystemowy)
        {
            if (zalacznik == null) return false;

            string czystyNrZalacznika = Normalizuj(zalacznik.DocumentNumber);
            string czystyNipZalacznika = Normalizuj(zalacznik.VendorNip).Replace("pl", "");

            if (string.IsNullOrEmpty(czystyNrZalacznika)) return false;

            bool numerPasuje = czystyNrSystemowy.EndsWith(czystyNrZalacznika);
            bool nipPasuje = string.IsNullOrEmpty(czystyNipZalacznika) || czystyNipSystemowy.EndsWith(czystyNipZalacznika);

            return numerPasuje && nipPasuje;
        }
        private string Normalizuj(string input) =>
            input == null ? "" : new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLower();

        private object ZnajdzEncje(Dictionary<string, dynamic> menedzerowie, dynamic wynik)
        {
            string typ = wynik.GetType().Name;
            dynamic mgr = null;
            if (ZawieraTyp(typ, "KPiR")) mgr = menedzerowie["KPiR"];
            else if (ZawieraTyp(typ, "Dekret")) mgr = menedzerowie["Dekret"];
            else if (ZawieraTyp(typ, "VAT")) mgr = menedzerowie["Vat"];
            else if (ZawieraTyp(typ, "EP")) mgr = menedzerowie["EP"];

            return ZnajdzFizycznaEncje(mgr, wynik.DokumentId);
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
