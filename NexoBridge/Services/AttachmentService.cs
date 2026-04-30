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
            if (rezultat == null || zatwierdzone == null || zatwierdzone.Count == 0) return;

            await raportujPostep(95, "Podpinanie załączników do finalnych dekretów...");

            var bibliotekaZalacznikow = _sfera.PodajObiektTypu<InsERT.Moria.BibliotekaZalacznikow.IBibliotekaZalacznikow>();
            dynamic mgrKPiR = PobierzMenedzera("IZapisyWKPiR");
            dynamic mgrVat = PobierzMenedzera("IZapisyWEwidencjiVAT");
            dynamic mgrDekrety = PobierzMenedzera("IDekrety");
            dynamic mgrEP = PobierzMenedzera("IZapisyWEP");

            var listaWynikow = ((System.Collections.IEnumerable)rezultat).Cast<dynamic>().ToList();

            for (int i = 0; i < zatwierdzone.Count; i++)
            {
                var dok = zatwierdzone[i].Item1;
                string numer = dok.NumerDokumentu;
                if (string.IsNullOrEmpty(numer)) numer = dok.Id.ToString();

                var operacja = listaWynikow[i];

                var zalacznik = job.Attachments?.FirstOrDefault(z =>
                    numer.Equals(z.DocumentNumber, StringComparison.OrdinalIgnoreCase) ||
                    numer.ToLower().Contains(z.DocumentNumber.ToLower()) ||
                    z.DocumentNumber.ToLower().Contains(numer.ToLower())
                );

                if (zalacznik != null && zalacznik.Content != null && zalacznik.Content.Length > 0)
                {
                    string tempPath = Path.Combine(Path.GetTempPath(), zalacznik.FileName);
                    File.WriteAllBytes(tempPath, zalacznik.Content);

                    try
                    {
                        dynamic dokumentyWynikowe = operacja.WynikowePoprawneZapisy;

                        if (dokumentyWynikowe != null)
                        {
                            using (var zalacznikBO = bibliotekaZalacznikow.Utworz())
                            {
                                zalacznikBO.Wczytaj(tempPath);
                                zalacznikBO.Dane.Opis = "Oryginał ze Scanye";

                                bool czyPodpieto = false;

                                foreach (var wynikowyElement in dokumentyWynikowe)
                                {
                                    string typWyniku = "Nieznany";
                                    string idDoLoga = "Brak";
                                    try
                                    {
                                        var rawId = wynikowyElement.DokumentId;
                                        typWyniku = wynikowyElement.GetType().Name;
                                        idDoLoga = rawId != null ? rawId.ToString() : "Brak";

                                        object encjaZintegrowana = null;

                                        if (typWyniku.Contains("KPiR")) encjaZintegrowana = ZnajdzFizycznaEncje((object)mgrKPiR, rawId);
                                        else if (typWyniku.Contains("Dekret")) encjaZintegrowana = ZnajdzFizycznaEncje((object)mgrDekrety, rawId);
                                        else if (typWyniku.Contains("Vat") || typWyniku.Contains("EVAT")) encjaZintegrowana = ZnajdzFizycznaEncje((object)mgrVat, rawId);
                                        else if (typWyniku.Contains("EP")) encjaZintegrowana = ZnajdzFizycznaEncje((object)mgrEP, rawId);

                                        if (encjaZintegrowana != null)
                                        {
                                            zalacznikBO.DodajPowiazanie((dynamic)encjaZintegrowana);
                                            czyPodpieto = true;
                                        }
                                    }
                                    catch (Exception exInner)
                                    {
                                        _logger.LogError("Błąd podczas przypisywania do {Typ} (ID: {Id}): {Msg}", typWyniku, idDoLoga, exInner.Message);
                                    }
                                }

                                if (czyPodpieto && zalacznikBO.Zapisz())
                                {
                                    _logger.LogInformation("[ZAŁĄCZNIK SUKCES] Pomyślnie podpięto plik '{FileName}' bezpośrednio do księgi dla: {Numer}", zalacznik.FileName, numer);
                                }
                                else
                                {
                                    _logger.LogError("[ZAŁĄCZNIK BŁĄD] Nie podpięto załącznika do ksiąg dla {Numer}.", numer);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[ZAŁĄCZNIK WYJĄTEK] Nie udało się podpiąć pliku do {Numer}", numer);
                    }
                    finally
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                }
                else
                {
                    _logger.LogWarning("[ZAŁĄCZNIK BRAK] Brak PDF dla dokumentu: {Numer}.", numer);
                }
            }
        }

        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                if (types != null)
                {
                    var t = types.FirstOrDefault(x => x != null && x.Name == nazwa && x.IsInterface);
                    if (t != null) return t;
                }
            }
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

        private object ZnajdzFizycznaEncje(object menedzer, object idWartosc)
        {
            if (menedzer == null || idWartosc == null) return null;
            try
            {
                string targetIdStr = idWartosc.ToString();

                var propDane = menedzer.GetType().GetProperty("Dane");
                if (propDane == null) return null;

                dynamic daneRepo = propDane.GetValue(menedzer);
                if (daneRepo == null) return null;

                dynamic wszystkieWpisy = daneRepo.Wszystkie();

                foreach (var encja in wszystkieWpisy)
                {
                    try
                    {
                        var idEncji = encja.Id;
                        if (idEncji != null && idEncji.ToString() == targetIdStr) return encja;
                    }
                    catch { continue; }
                }
            }
            catch (Exception exRef)
            {
                _logger.LogWarning("Błąd wyszukiwania encji {Id}: {Msg}", idWartosc, exRef.InnerException?.Message ?? exRef.Message);
            }
            return null;
        }
    }
}