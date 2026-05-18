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

                if (zalacznik != null)
                {
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
                    string tempPath = Path.Combine(tempDir, $"{bezpiecznaNazwa}.pdf");

                    File.WriteAllBytes(tempPath, zalacznik.Content);

                    try
                    {
                        dynamic dokumentyWynikowe = listaWynikow[i].WynikowePoprawneZapisy;
                        if (dokumentyWynikowe != null)
                        {
                            using (var zalacznikBO = bibliotekaZalacznikow.Utworz())
                            {
                                // Wczytaj() automatycznie nada plikowi nazwę wyciągniętą z tempPath (czyli ładny numer)
                                zalacznikBO.Wczytaj(tempPath);
                                zalacznikBO.Dane.Opis = "Oryginał ze Scanye";

                                bool podpieto = false;
                                foreach (var wynik in dokumentyWynikowe)
                                {
                                    object encja = ZnajdzEncje(menedzerowie, wynik);
                                    if (encja != null)
                                    {
                                        zalacznikBO.DodajPowiazanie((dynamic)encja);
                                        podpieto = true;
                                    }
                                }

                                if (podpieto && zalacznikBO.Zapisz())
                                {
                                    _logger.LogInformation("[ZAŁĄCZNIK SUKCES] Podpięto dokument pod nazwą '{Skan}' dla: {Numer}", bezpiecznaNazwa, nrSystemowy);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[ZAŁĄCZNIK BŁĄD] Wystąpił wyjątek podczas podpinania pliku '{Skan}' do dokumentu: {Numer}", bezpiecznaNazwa, nrSystemowy);
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
                    _logger.LogWarning("[ZAŁĄCZNIK BRAK] Nie znaleziono dopasowania w metadanych lub brak PDF dla dokumentu: {Numer} (NIP: {Nip})", nrSystemowy, nipSystemowy);
                }
            }
        }

        private string Normalizuj(string input) =>
            input == null ? "" : new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLower();

        private object ZnajdzEncje(Dictionary<string, dynamic> menedzerowie, dynamic wynik)
        {
            string typ = wynik.GetType().Name;
            dynamic mgr = null;
            if (typ.Contains("KPiR")) mgr = menedzerowie["KPiR"];
            else if (typ.Contains("Dekret")) mgr = menedzerowie["Dekret"];
            else if (typ.Contains("Vat")) mgr = menedzerowie["Vat"];
            else if (typ.Contains("EP")) mgr = menedzerowie["EP"];

            return ZnajdzFizycznaEncje(mgr, wynik.DokumentId);
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