using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class PitCalculationService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<PitCalculationService> _logger;

        public PitCalculationService(Uchwyt sfera, ILogger<PitCalculationService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task<List<PitResult>> WyliczZaliczkiWspolnikowAsync(DateTime dataRozliczenia)
        {
            var wyniki = new List<PitResult>();
            _logger.LogInformation("Rozpoczynam analizę i wyliczanie PIT dla wspólników za {Miesiac}/{Rok}...", dataRozliczenia.Month, dataRozliczenia.Year);

            try
            {
                // --- 1. Pobieranie Menedżerów ---
                dynamic mgrPodmioty = PobierzMenedzera("IPodmioty");
                dynamic mgrWersji = PobierzMenedzera("IWersjeDeklaracji");
                dynamic mgrDeklaracji = PobierzMenedzera("IDeklaracje") ?? PobierzMenedzera("IDeklaracjeSkarbowe");

                if (mgrPodmioty == null || mgrWersji == null || mgrDeklaracji == null)
                {
                    _logger.LogError("BŁĄD KRYTYCZNY: Nie udało się załadować menedżerów deklaracji Sfery.");
                    wyniki.Add(new PitResult { CriticalError = "Błąd inicjalizacji menedżerów Sfery." });
                    return wyniki;
                }

                // --- 2. Pobieranie bazy podmiotów i szukanie właścicieli ---
                var wszystkiePodmioty = ((IEnumerable)mgrPodmioty.Dane.Wszystkie()).Cast<dynamic>();
                var listaWspolnikow = new List<dynamic>();

                foreach (var p in wszystkiePodmioty)
                {
                    try
                    {
                        var testOkresow = p.Osoba.Wspolnik.OkresyRozliczenPIT;
                        if (testOkresow != null) listaWspolnikow.Add(p);
                    }
                    catch { /* Ignorujemy klientów/kontrahentów */ }
                }

                if (!listaWspolnikow.Any())
                {
                    _logger.LogWarning("Brak podmiotów ze skonfigurowanymi danymi wspólnika w bazie.");
                    return wyniki;
                }

                // --- 3. Główna pętla wyliczająca dla każdego wspólnika ---
                foreach (var wspolnik in listaWspolnikow)
                {
                    string nazwaWspolnika = PobierzNazwe(wspolnik);
                    _logger.LogInformation("Analiza wspólnika: {Nazwa}", nazwaWspolnika);

                    var rezultat = new PitResult { PartnerName = nazwaWspolnika, IsGenerated = false };

                    IEnumerable okresyPIT = wspolnik.Osoba.Wspolnik.OkresyRozliczenPIT;
                    var aktywneOkresy = okresyPIT.Cast<dynamic>()
                                                 .Where(o => o.Poczatek != null && dataRozliczenia >= o.Poczatek)
                                                 .OrderByDescending(o => o.Poczatek)
                                                 .ToList();

                    if (!aktywneOkresy.Any())
                    {
                        rezultat.Warning = "Brak aktywnego okresu podatkowego w dacie rozliczenia.";
                        wyniki.Add(rezultat);
                        continue;
                    }

                    var aktywnyOkres = aktywneOkresy.First();
                    int formaId = (int)aktywnyOkres.FormaOpodatkowania; // 1: Liniowa, 2: Skala, 3: Ryczałt
                    int metodaId = (int)aktywnyOkres.MetodaOplacaniaZaliczek; // 0: Miesięcznie, 1: Kwartalnie

                    // --- 4. Logika dat i obsługa kwartałów ---
                    DateTime dataStart;
                    DateTime dataKoniec = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, DateTime.DaysInMonth(dataRozliczenia.Year, dataRozliczenia.Month));

                    if (metodaId == 0) // MIESIĘCZNIE
                    {
                        dataStart = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, 1);
                    }
                    else // KWARTALNIE
                    {
                        if (dataRozliczenia.Month % 3 != 0)
                        {
                            _logger.LogInformation("Wspólnik {Nazwa} rozlicza się kwartalnie. Pomijam miesiąc {Miesiac}.", nazwaWspolnika, dataRozliczenia.Month);
                            rezultat.Warning = "Pominięto (Oczekiwanie na koniec kwartału).";
                            wyniki.Add(rezultat);
                            continue;
                        }
                        dataStart = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month - 2, 1);
                    }

                    // --- ZABEZPIECZENIE 1: KULOODPORNA BRAMKA DUPLIKATÓW ---
                    var wszystkiePity = ((IEnumerable)mgrDeklaracji.Dane.Wszystkie()).Cast<dynamic>().ToList();

                    var istniejacyPit = wszystkiePity
                        .Where(d =>
                        {
                            try
                            {
                                string nazwaDokumentu = "";
                                try { nazwaDokumentu = (string)d.Nazwa; } catch { }
                                try { if (string.IsNullOrEmpty(nazwaDokumentu)) nazwaDokumentu = (string)d.Tytul; } catch { }
                                try { if (string.IsNullOrEmpty(nazwaDokumentu)) nazwaDokumentu = (string)d.Wzorzec?.Nazwa; } catch { }

                                if (string.IsNullOrEmpty(nazwaDokumentu) || (!nazwaDokumentu.Contains("Zaliczka") && !nazwaDokumentu.Contains("PIT"))) return false;

                                string podmiot = "";
                                try { podmiot = (string)d.Podmiot?.Osoba?.Nazwisko; } catch { }
                                try { if (string.IsNullOrEmpty(podmiot)) podmiot = (string)d.Wspolnik?.NazwaSkrocona; } catch { }
                                try { if (string.IsNullOrEmpty(podmiot)) podmiot = (string)d.Podmiot?.NazwaSkrocona; } catch { }

                                if (string.IsNullOrEmpty(podmiot) || !podmiot.Contains(nazwaWspolnika)) return false;

                                DateTime? dataWystawienia = null;
                                try { dataWystawienia = (DateTime?)d.DataWystawienia; } catch { }
                                try { if (dataWystawienia == null) dataWystawienia = (DateTime?)d.Okres?.DataOd; } catch { }

                                if (dataWystawienia.HasValue)
                                {
                                    return (dataWystawienia.Value.Year == dataRozliczenia.Year && dataWystawienia.Value.Month == dataRozliczenia.Month) ||
                                           (dataWystawienia.Value.Year == dataStart.Year && dataWystawienia.Value.Month >= dataStart.Month);
                                }
                                return false;
                            }
                            catch { return false; }
                        })
                        .OrderByDescending(d => { try { return (int)d.Id; } catch { return 0; } })
                        .FirstOrDefault();

                    if (istniejacyPit != null)
                    {
                        decimal kwotaIstniejacego = 0m;
                        try { kwotaIstniejacego = (decimal)istniejacyPit.KwotaZobowiazania; } catch { try { kwotaIstniejacego = (decimal)istniejacyPit.Wartosc; } catch { } }

                        _logger.LogWarning("Zaliczka dla {Nazwa} za dany okres istnieje już w bazie. Omijam generowanie.", nazwaWspolnika);
                        rezultat.AmountDue = kwotaIstniejacego;
                        rezultat.TaxType = "Już wygenerowana";
                        rezultat.IsGenerated = true;
                        wyniki.Add(rezultat);
                        continue;
                    }

                    // --- 5. Pancerny Dobór Wzorca (Regex + Filtry wykluczające) ---
                    string rdzenNazwy = (metodaId == 0) ? "Zaliczka miesięczna" : "Zaliczka kwartalna";
                    var wszystkieWersje = ((IEnumerable)mgrWersji.Wersje).Cast<dynamic>().ToList();

                    var pasujaceWersje = wszystkieWersje.Where(w =>
                    {
                        try
                        {
                            string nazwa = (string)w.Nazwa;

                            if (formaId == 2) // Progresywna (Skala)
                            {
                                return nazwa.StartsWith($"{rdzenNazwy} PIT")
                                    && !nazwa.Contains("liniow")
                                    && !nazwa.Contains("ryczałt");
                            }
                            else if (formaId == 1) // Liniowa
                            {
                                return nazwa.StartsWith(rdzenNazwy) && nazwa.Contains("liniow");
                            }
                            else if (formaId == 3) // Zryczałtowana (Ryczałt)
                            {
                                return nazwa.StartsWith($"{rdzenNazwy} ryczałtowa PIT");
                            }
                            return false;
                        }
                        catch { return false; }
                    }).ToList();

                    // Matematyczne sortowanie wzorców po cyferce w nawiasie (np. "(8)")
                    dynamic znalezionaWersja = pasujaceWersje.OrderByDescending(w =>
                    {
                        string nazwa = (string)w.Nazwa;
                        var match = Regex.Match(nazwa, @"\((\d+)\)$");
                        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
                    }).FirstOrDefault();

                    if (znalezionaWersja == null)
                    {
                        rezultat.CriticalError = $"Nie znaleziono w bazie wzorca formularza MF dla tej formy opodatkowania.";
                        wyniki.Add(rezultat);
                        continue;
                    }

                    rezultat.TaxType = znalezionaWersja.Nazwa;

                    // --- 6. Generowanie i ZABEZPIECZENIE 2 (FALLBACK ZAPISU) ---
                    try
                    {
                        using (dynamic deklBO = mgrDeklaracji.Utworz())
                        {
                            deklBO.Wylicz(wspolnik, (Guid)znalezionaWersja.Id, dataStart, dataKoniec, null);

                            decimal kwotaDoZaplaty = 0m;
                            try { kwotaDoZaplaty = deklBO.Dane.KwotaZobowiazania; }
                            catch { try { kwotaDoZaplaty = deklBO.Dane.Wartosc; } catch { kwotaDoZaplaty = 0m; } }

                            bool zapisano = deklBO.Zapisz();

                            if (!zapisano)
                            {
                                string szczegolyBledu = WyciagnijBledySfery(deklBO);

                                // Jeśli baza odrzuci zapis, bo klucz unikalny SQL znalazł duplikat:
                                if (string.IsNullOrEmpty(szczegolyBledu) || szczegolyBledu.Contains("Unit of work") || szczegolyBledu.Contains("Added"))
                                {
                                    _logger.LogWarning("Zaliczka dla {Nazwa} już istniała w bazie (zablokowano duplikat SQL).", nazwaWspolnika);
                                    rezultat.AmountDue = kwotaDoZaplaty;
                                    rezultat.TaxType = "Już wygenerowana";
                                    rezultat.IsGenerated = true;
                                    wyniki.Add(rezultat);
                                    continue;
                                }
                                else
                                {
                                    throw new Exception($"Sfera wyliczyła kwotę ({kwotaDoZaplaty} zł), ale ODRZUCIŁA zapis do bazy. Powód Sfery: {szczegolyBledu}");
                                }
                            }

                            rezultat.AmountDue = kwotaDoZaplaty;
                            rezultat.IsGenerated = true;

                            _logger.LogInformation("Sukces! Wyliczono {Typ} dla {Nazwa}: {Kwota} zł", rezultat.TaxType, rezultat.PartnerName, rezultat.AmountDue);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Błąd podczas wyliczania deklaracji dla {Nazwa}.", nazwaWspolnika);
                        rezultat.CriticalError = $"Błąd silnika wyliczającego: {ex.Message}";
                    }

                    wyniki.Add(rezultat);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Krytyczny błąd w serwisie PIT.");
                wyniki.Add(new PitResult { CriticalError = $"Krytyczna awaria serwisu PIT: {ex.Message}" });
            }

            return wyniki;
        }

        // --- Metody Pomocnicze ---
        private string WyciagnijBledySfery(dynamic obiektBO)
        {
            try
            {
                var invalidData = (IEnumerable)obiektBO.InvalidData;
                if (invalidData != null)
                {
                    var bledy = invalidData.Cast<dynamic>().Select(e =>
                    {
                        try { return (string)e.Komunikat ?? (string)e.Tresc ?? (string)e.Opis; }
                        catch { return e.ToString(); }
                    }).ToList();

                    if (bledy.Any()) return string.Join(" | ", bledy);
                }
            }
            catch { }
            return "Brak szczegółowych komunikatów.";
        }

        private dynamic PobierzMenedzera(string nazwaInterfejsu)
        {
            Type typSzukany = ZnajdzTypBezpiecznie(nazwaInterfejsu);
            if (typSzukany != null)
            {
                var metoda = _sfera.GetType().GetMethods().FirstOrDefault(m => m.Name == "PodajObiektTypu" && m.GetParameters().Length == 0);
                if (metoda != null) return metoda.MakeGenericMethod(typSzukany).Invoke(_sfera, null);
            }
            return null;
        }

        private Type ZnajdzTypBezpiecznie(string nazwa)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                if (assembly.FullName.StartsWith("System.") || assembly.FullName.StartsWith("Microsoft.")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                var found = types.FirstOrDefault(t => t != null && t.IsInterface && t.Name == nazwa);
                if (found != null) return found;
            }
            return null;
        }

        private string PobierzNazwe(dynamic podmiot)
        {
            string nazwa = "";
            try { nazwa = podmiot.NazwaSkrocona; } catch { }
            if (string.IsNullOrEmpty(nazwa)) try { nazwa = podmiot.Osoba.Nazwisko; } catch { }
            return string.IsNullOrEmpty(nazwa) ? "Nieznany Wspólnik" : nazwa;
        }
    }
}