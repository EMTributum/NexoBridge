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
                dynamic mgrDeklaracji = PobierzMenedzeraDeklaracji();

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
                    Guid wersjaId = (Guid)znalezionaWersja.Id;
                    string wersjaNazwa = rezultat.TaxType;

                    // --- ZABEZPIECZENIE 1: precyzyjna bramka duplikatów ---
                    var wszystkiePity = PobierzWszystkieDeklaracje((object)mgrDeklaracji);
                    int liczbaIstniejacychPitow = wszystkiePity.Count;
                    var pityWOkresie = PobierzDeklaracjePitWOkresie(
                        (object)mgrDeklaracji,
                        (object)wspolnik,
                        nazwaWspolnika,
                        dataStart,
                        dataKoniec,
                        "precheck");

                    object istniejacyPit = ZnajdzDokladnyDuplikatPit(
                        pityWOkresie,
                        (object)wspolnik,
                        nazwaWspolnika,
                        wersjaId,
                        wersjaNazwa,
                        dataStart,
                        dataKoniec,
                        potwierdzOkresZMetodySfery: true,
                        out List<PitDuplicateCandidate> kandydaciDuplikatu);

                    LogujKandydatowDuplikatuPit(
                        "precheck-period-api",
                        nazwaWspolnika,
                        dataStart,
                        dataKoniec,
                        wersjaNazwa,
                        kandydaciDuplikatu);

                    if (istniejacyPit == null)
                    {
                        istniejacyPit = ZnajdzDokladnyDuplikatPit(
                            wszystkiePity,
                            (object)wspolnik,
                            nazwaWspolnika,
                            wersjaId,
                            wersjaNazwa,
                            dataStart,
                            dataKoniec,
                            potwierdzOkresZMetodySfery: false,
                            out kandydaciDuplikatu);

                        LogujKandydatowDuplikatuPit(
                            "precheck-all",
                            nazwaWspolnika,
                            dataStart,
                            dataKoniec,
                            wersjaNazwa,
                            kandydaciDuplikatu);
                    }

                    if (istniejacyPit != null)
                    {
                        decimal kwotaIstniejacego = PobierzKwoteDeklaracji(istniejacyPit);

                        _logger.LogWarning("[PIT DUPLIKAT] Dokładna zaliczka dla {Nazwa} za okres {DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd} już istnieje. Omijam generowanie. Deklaracja={Deklaracja}",
                            nazwaWspolnika,
                            dataStart,
                            dataKoniec,
                            OpiszDeklaracjePit(istniejacyPit));
                        rezultat.AmountDue = kwotaIstniejacego;
                        rezultat.TaxType = "Już wygenerowana";
                        rezultat.IsGenerated = true;
                        wyniki.Add(rezultat);
                        continue;
                    }

                    // --- 6. Generowanie i ZABEZPIECZENIE 2 (FALLBACK ZAPISU) ---
                    try
                    {
                        dynamic mgrDeklaracjiDoUtworzenia = PobierzMenedzeraDeklaracji();
                        dynamic deklBO = UtworzDeklaracjePit(
                            mgrDeklaracjiDoUtworzenia,
                            nazwaWspolnika,
                            wersjaNazwa,
                            wersjaId,
                            dataStart,
                            dataKoniec,
                            liczbaIstniejacychPitow,
                            pasujaceWersje.Count);

                        deklBO.Wylicz(wspolnik, wersjaId, dataStart, dataKoniec, null);

                        decimal kwotaDoZaplaty = 0m;
                        try { kwotaDoZaplaty = deklBO.Dane.KwotaZobowiazania; }
                        catch { try { kwotaDoZaplaty = deklBO.Dane.Wartosc; } catch { kwotaDoZaplaty = 0m; } }

                        bool zapisano = deklBO.Zapisz();

                        if (!zapisano)
                        {
                            string szczegolyBledu = WyciagnijBledySfery(deklBO);

                            dynamic swiezyMgrDeklaracji = PobierzMenedzeraDeklaracji();
                            var pityPoOdrzuceniuZapisu = PobierzWszystkieDeklaracje((object)swiezyMgrDeklaracji);
                            var pityPoOdrzuceniuZapisuWOkresie = PobierzDeklaracjePitWOkresie(
                                (object)swiezyMgrDeklaracji,
                                (object)wspolnik,
                                nazwaWspolnika,
                                dataStart,
                                dataKoniec,
                                "after-save-false");
                            object potwierdzonyDuplikatPoZapisie = ZnajdzDokladnyDuplikatPit(
                                pityPoOdrzuceniuZapisuWOkresie,
                                (object)wspolnik,
                                nazwaWspolnika,
                                wersjaId,
                                wersjaNazwa,
                                dataStart,
                                dataKoniec,
                                potwierdzOkresZMetodySfery: true,
                                out List<PitDuplicateCandidate> kandydaciPoOdrzuceniuZapisu);

                            LogujKandydatowDuplikatuPit(
                                "after-save-false-period-api",
                                nazwaWspolnika,
                                dataStart,
                                dataKoniec,
                                wersjaNazwa,
                                kandydaciPoOdrzuceniuZapisu);

                            if (potwierdzonyDuplikatPoZapisie == null)
                            {
                                potwierdzonyDuplikatPoZapisie = ZnajdzDokladnyDuplikatPit(
                                    pityPoOdrzuceniuZapisu,
                                    (object)wspolnik,
                                    nazwaWspolnika,
                                    wersjaId,
                                    wersjaNazwa,
                                    dataStart,
                                    dataKoniec,
                                    potwierdzOkresZMetodySfery: false,
                                    out kandydaciPoOdrzuceniuZapisu);

                                LogujKandydatowDuplikatuPit(
                                    "after-save-false-all",
                                    nazwaWspolnika,
                                    dataStart,
                                    dataKoniec,
                                    wersjaNazwa,
                                    kandydaciPoOdrzuceniuZapisu);
                            }

                            if (potwierdzonyDuplikatPoZapisie != null)
                            {
                                _logger.LogWarning("[PIT ZAPIS ODRZUCONY - DUPLIKAT POTWIERDZONY] Wspolnik={Nazwa}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; wzorzec={WersjaNazwa}; invalidData={InvalidData}; deklaracja={Deklaracja}",
                                    nazwaWspolnika,
                                    dataStart,
                                    dataKoniec,
                                    wersjaNazwa,
                                    string.IsNullOrWhiteSpace(szczegolyBledu) ? "brak" : szczegolyBledu,
                                    OpiszDeklaracjePit(potwierdzonyDuplikatPoZapisie));
                                rezultat.AmountDue = PobierzKwoteDeklaracji(potwierdzonyDuplikatPoZapisie);
                                if (rezultat.AmountDue == 0m) rezultat.AmountDue = kwotaDoZaplaty;
                                rezultat.TaxType = "Już wygenerowana";
                                rezultat.IsGenerated = true;
                                wyniki.Add(rezultat);
                                continue;
                            }

                            throw new Exception($"Sfera wyliczyła kwotę ({kwotaDoZaplaty} zł), ale ODRZUCIŁA zapis do bazy. Nie potwierdzono dokładnego duplikatu po świeżym odczycie. Powód Sfery: {szczegolyBledu}. Kandydaci: {OpiszKandydatowDuplikatuPit(kandydaciPoOdrzuceniuZapisu)}");
                        }

                        rezultat.AmountDue = kwotaDoZaplaty;
                        rezultat.IsGenerated = true;

                        _logger.LogInformation("Sukces! Wyliczono {Typ} dla {Nazwa}: {Kwota} zł", rezultat.TaxType, rezultat.PartnerName, rezultat.AmountDue);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[PIT BŁĄD] Nie udało się wyliczyć deklaracji. Wspolnik={Nazwa}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; forma={FormaId}; metoda={MetodaId}; wzorzec={WersjaNazwa}; wersjaId={WersjaId}; istniejaceDeklaracje={IstniejaceDeklaracje}; pasujaceWersje={PasujaceWersje}; managerDeklaracji={Manager}; sfera={Sfera}; baseException={BaseException}",
                            nazwaWspolnika,
                            dataStart,
                            dataKoniec,
                            formaId,
                            metodaId,
                            wersjaNazwa,
                            wersjaId,
                            liczbaIstniejacychPitow,
                            pasujaceWersje.Count,
                            OpiszTyp((object)mgrDeklaracji),
                            OpiszTyp(_sfera),
                            OpiszWyjatekBazowy(ex));
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

        private List<object> PobierzWszystkieDeklaracje(dynamic mgrDeklaracji)
        {
            try
            {
                if (mgrDeklaracji == null)
                {
                    return new List<object>();
                }

                return ((IEnumerable)mgrDeklaracji.Dane.Wszystkie()).Cast<object>().ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PIT DUPLIKAT] Nie udało się odczytać listy deklaracji do sprawdzenia duplikatów. baseException={BaseException}",
                    OpiszWyjatekBazowy(ex));
                return new List<object>();
            }
        }

        private List<object> PobierzDeklaracjePitWOkresie(
            object mgrDeklaracji,
            object podmiot,
            string nazwaWspolnika,
            DateTime dataStart,
            DateTime dataKoniec,
            string etap)
        {
            try
            {
                object daneDeklaracji = PobierzWlasciwosc(mgrDeklaracji, "Dane");
                if (daneDeklaracji == null || podmiot == null)
                {
                    _logger.LogDebug("[PIT DUPLIKAT OKRES API] etap={Etap}; wspolnik={Wspolnik}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; pomijam, bo brakuje Dane lub podmiotu. manager={Manager}; podmiot={Podmiot}",
                        etap,
                        nazwaWspolnika,
                        dataStart,
                        dataKoniec,
                        OpiszTyp(mgrDeklaracji),
                        OpiszTyp(podmiot));
                    return new List<object>();
                }

                object wynik = WywolajZnajdzDeklaracjeWOkresie(daneDeklaracji, dataStart, dataKoniec, podmiot);
                var lista = ((IEnumerable)wynik)?.Cast<object>().ToList() ?? new List<object>();

                _logger.LogDebug("[PIT DUPLIKAT OKRES API] etap={Etap}; wspolnik={Wspolnik}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; liczba={Liczba}; managerDane={ManagerDane}; podmiot={Podmiot}",
                    etap,
                    nazwaWspolnika,
                    dataStart,
                    dataKoniec,
                    lista.Count,
                    OpiszTyp(daneDeklaracji),
                    OpiszTyp(podmiot));

                return lista;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PIT DUPLIKAT OKRES API] Nie udało się pobrać deklaracji przez ZnajdzDeklaracjeWOkresie. etap={Etap}; wspolnik={Wspolnik}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; baseException={BaseException}",
                    etap,
                    nazwaWspolnika,
                    dataStart,
                    dataKoniec,
                    OpiszWyjatekBazowy(ex));
                return new List<object>();
            }
        }

        private object WywolajZnajdzDeklaracjeWOkresie(object daneDeklaracji, DateTime dataStart, DateTime dataKoniec, object podmiot)
        {
            var metody = daneDeklaracji
                .GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "ZnajdzDeklaracjeWOkresie" && m.GetParameters().Length == 3)
                .ToList();

            foreach (MethodInfo metoda in metody)
            {
                ParameterInfo[] parametry = metoda.GetParameters();
                if (parametry[0].ParameterType != typeof(DateTime) || parametry[1].ParameterType != typeof(DateTime))
                {
                    continue;
                }

                if (parametry[2].ParameterType.IsAssignableFrom(podmiot.GetType()))
                {
                    return metoda.Invoke(daneDeklaracji, new[] { (object)dataStart, dataKoniec, podmiot });
                }
            }

            int? podmiotId = PobierzIntPoSciezkach(podmiot, "Id");
            if (podmiotId.HasValue)
            {
                foreach (MethodInfo metoda in metody)
                {
                    ParameterInfo[] parametry = metoda.GetParameters();
                    Type nullable = Nullable.GetUnderlyingType(parametry[2].ParameterType);
                    bool thirdParameterIsInt = parametry[2].ParameterType == typeof(int) || nullable == typeof(int);
                    if (parametry[0].ParameterType == typeof(DateTime) &&
                        parametry[1].ParameterType == typeof(DateTime) &&
                        thirdParameterIsInt)
                    {
                        return metoda.Invoke(daneDeklaracji, new object[] { dataStart, dataKoniec, podmiotId.Value });
                    }
                }
            }

            dynamic daneDyn = daneDeklaracji;
            dynamic podmiotDyn = podmiot;
            return daneDyn.ZnajdzDeklaracjeWOkresie(dataStart, dataKoniec, podmiotDyn);
        }

        private object ZnajdzDokladnyDuplikatPit(
            IEnumerable<object> deklaracje,
            object wspolnik,
            string nazwaWspolnika,
            Guid wersjaId,
            string wersjaNazwa,
            DateTime dataStart,
            DateTime dataKoniec,
            bool potwierdzOkresZMetodySfery,
            out List<PitDuplicateCandidate> kandydaci)
        {
            kandydaci = new List<PitDuplicateCandidate>();
            var identyfikatoryWspolnika = PobierzIdentyfikatoryWspolnika(wspolnik, sourceIsDeclaration: false);

            foreach (var deklaracja in deklaracje ?? Enumerable.Empty<object>())
            {
                var kandydat = OdczytajDeklaracjePit(deklaracja);
                bool matchPeriodFromFields = kandydat.DataOd?.Date == dataStart.Date && kandydat.DataDo?.Date == dataKoniec.Date;
                kandydat.MatchPeriod = matchPeriodFromFields || potwierdzOkresZMetodySfery;
                kandydat.PeriodMatchSource = matchPeriodFromFields
                    ? "fields"
                    : potwierdzOkresZMetodySfery ? "ZnajdzDeklaracjeWOkresie" : "none";
                kandydat.MatchVersion = CzyTenSamWzorzec(kandydat, wersjaId, wersjaNazwa);
                kandydat.MatchPartner = CzyTenSamWspolnik(kandydat, identyfikatoryWspolnika, nazwaWspolnika);
                kandydat.ActiveForDuplicateCheck = !CzyStatusWykluczaDuplikat(kandydat.Status);
                kandydat.LooksLikePit = CzyDeklaracjaWygladaNaPit(kandydat);
                kandydat.ExactDuplicate = kandydat.LooksLikePit
                    && kandydat.ActiveForDuplicateCheck
                    && kandydat.MatchPeriod
                    && kandydat.MatchVersion
                    && kandydat.MatchPartner;

                if (kandydat.LooksLikePit && (kandydat.MatchPeriod || kandydat.MatchVersion || kandydat.MatchPartner))
                {
                    kandydaci.Add(kandydat);
                }
            }

            return kandydaci
                .Where(k => k.ExactDuplicate)
                .OrderByDescending(k => k.IdSort ?? 0)
                .Select(k => k.Declaration)
                .FirstOrDefault();
        }

        private void LogujKandydatowDuplikatuPit(
            string etap,
            string nazwaWspolnika,
            DateTime dataStart,
            DateTime dataKoniec,
            string wersjaNazwa,
            List<PitDuplicateCandidate> kandydaci)
        {
            _logger.LogDebug("[PIT DUPLIKAT KANDYDACI] etap={Etap}; wspolnik={Wspolnik}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; wzorzec={Wzorzec}; liczba={Liczba}; kandydaci={Kandydaci}",
                etap,
                nazwaWspolnika,
                dataStart,
                dataKoniec,
                wersjaNazwa,
                kandydaci?.Count ?? 0,
                OpiszKandydatowDuplikatuPit(kandydaci));
        }

        private string OpiszKandydatowDuplikatuPit(IEnumerable<PitDuplicateCandidate> kandydaci)
        {
            var lista = (kandydaci ?? Enumerable.Empty<PitDuplicateCandidate>())
                .Take(100)
                .Select(OpiszKandydataDuplikatuPit)
                .ToList();

            return lista.Count == 0 ? "brak" : string.Join(" || ", lista);
        }

        private string OpiszDeklaracjePit(object deklaracja)
        {
            return OpiszKandydataDuplikatuPit(OdczytajDeklaracjePit(deklaracja));
        }

        private string OpiszKandydataDuplikatuPit(PitDuplicateCandidate kandydat)
        {
            if (kandydat == null)
            {
                return "brak";
            }

            return $"Id={kandydat.Id ?? "brak"}; nazwa={kandydat.Name ?? "brak"}; wzorzec={kandydat.VersionName ?? "brak"}; wzorzecId={FormatNullableGuid(kandydat.VersionId)}; status={kandydat.Status ?? "brak"}; okres={FormatNullableDate(kandydat.DataOd)}-{FormatNullableDate(kandydat.DataDo)}; zrodloOkresu={kandydat.PeriodMatchSource ?? "brak"}; wspolnik={kandydat.PartnerName ?? "brak"}; matchOkres={kandydat.MatchPeriod}; matchWzorzec={kandydat.MatchVersion}; matchWspolnik={kandydat.MatchPartner}; aktywny={kandydat.ActiveForDuplicateCheck}; exact={kandydat.ExactDuplicate}";
        }

        private PitDuplicateCandidate OdczytajDeklaracjePit(object deklaracja)
        {
            var kandydat = new PitDuplicateCandidate
            {
                Declaration = deklaracja,
                Id = PobierzStringPoSciezkach(deklaracja, "Id"),
                Name = PobierzStringPoSciezkach(deklaracja, "Nazwa", "Tytul", "Wzorzec.Nazwa", "WersjaDeklaracji.Nazwa", "Wersja.Nazwa"),
                VersionName = PobierzStringPoSciezkach(deklaracja, "Wzorzec.Nazwa", "WersjaDeklaracji.Nazwa", "Wersja.Nazwa"),
                VersionId = PobierzGuidPoSciezkach(deklaracja, "Wzorzec.Id", "WersjaDeklaracji.Id", "Wersja.Id", "WersjaDeklaracjiId", "WersjaId"),
                Status = PobierzStringPoSciezkach(deklaracja, "Status", "Stan", "StatusDeklaracji", "StanDeklaracji"),
                DataOd = PobierzDatePoSciezkach(deklaracja, "DataOd", "Okres.DataOd", "OkresDeklaracji.DataOd", "Zakres.DataOd"),
                DataDo = PobierzDatePoSciezkach(deklaracja, "DataDo", "Okres.DataDo", "OkresDeklaracji.DataDo", "Zakres.DataDo"),
                PartnerName = PobierzNazweWspolnikaZDeklaracji(deklaracja),
                PartnerIds = PobierzIdentyfikatoryWspolnika(deklaracja, sourceIsDeclaration: true),
                IdSort = PobierzIntPoSciezkach(deklaracja, "Id")
            };

            if (string.IsNullOrWhiteSpace(kandydat.VersionName))
            {
                kandydat.VersionName = kandydat.Name;
            }

            return kandydat;
        }

        private bool CzyDeklaracjaWygladaNaPit(PitDuplicateCandidate kandydat)
        {
            string tekst = NormalizujTekst($"{kandydat?.Name} {kandydat?.VersionName}");
            return tekst.Contains("PIT") || tekst.Contains("ZALICZKA");
        }

        private bool CzyTenSamWzorzec(PitDuplicateCandidate kandydat, Guid wersjaId, string wersjaNazwa)
        {
            if (kandydat == null)
            {
                return false;
            }

            if (kandydat.VersionId.HasValue && kandydat.VersionId.Value == wersjaId)
            {
                return true;
            }

            string oczekiwany = NormalizujTekst(wersjaNazwa);
            return !string.IsNullOrWhiteSpace(oczekiwany)
                && (NormalizujTekst(kandydat.VersionName) == oczekiwany || NormalizujTekst(kandydat.Name) == oczekiwany);
        }

        private bool CzyTenSamWspolnik(PitDuplicateCandidate kandydat, HashSet<string> identyfikatoryWspolnika, string nazwaWspolnika)
        {
            if (kandydat == null)
            {
                return false;
            }

            if (kandydat.PartnerIds.Any() && identyfikatoryWspolnika.Any() && kandydat.PartnerIds.Overlaps(identyfikatoryWspolnika))
            {
                return true;
            }

            return CzyTaSamaNazwaOsoby(kandydat.PartnerName, nazwaWspolnika);
        }

        private bool CzyTaSamaNazwaOsoby(string a, string b)
        {
            string normA = NormalizujTekst(a);
            string normB = NormalizujTekst(b);
            if (string.IsNullOrWhiteSpace(normA) || string.IsNullOrWhiteSpace(normB))
            {
                return false;
            }

            if (normA == normB)
            {
                return true;
            }

            var tokenyA = TokenyNazwy(normA);
            var tokenyB = TokenyNazwy(normB);
            return tokenyA.Count > 0 && tokenyA.SetEquals(tokenyB);
        }

        private HashSet<string> TokenyNazwy(string value)
        {
            return Regex.Matches(value ?? "", "[A-Z0-9]+")
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private bool CzyStatusWykluczaDuplikat(string status)
        {
            string normalized = NormalizujTekst(status);
            return normalized.Contains("ANUL")
                || normalized.Contains("USUN")
                || normalized.Contains("WYCOF")
                || normalized.Contains("NIEAKT");
        }

        private decimal PobierzKwoteDeklaracji(object deklaracja)
        {
            return PobierzDecimalPoSciezkach(
                deklaracja,
                "KwotaZobowiazania",
                "Wartosc",
                "KwotaDoZaplaty",
                "Dane.KwotaZobowiazania",
                "Dane.Wartosc",
                "Dane.KwotaDoZaplaty") ?? 0m;
        }

        private string PobierzNazweWspolnikaZDeklaracji(object deklaracja)
        {
            string nazwa = PobierzStringPoSciezkach(
                deklaracja,
                "Wspolnik.NazwaSkrocona",
                "Wspolnik.Podmiot.NazwaSkrocona",
                "Podmiot.NazwaSkrocona",
                "Podmiot.Nazwa",
                "Podmiot.Osoba.Nazwisko",
                "Wspolnik.Osoba.Nazwisko");

            if (!string.IsNullOrWhiteSpace(nazwa))
            {
                return nazwa;
            }

            string imie = PobierzStringPoSciezkach(deklaracja, "Podmiot.Osoba.Imie", "Wspolnik.Osoba.Imie");
            string nazwisko = PobierzStringPoSciezkach(deklaracja, "Podmiot.Osoba.Nazwisko", "Wspolnik.Osoba.Nazwisko");
            return $"{imie} {nazwisko}".Trim();
        }

        private HashSet<string> PobierzIdentyfikatoryWspolnika(object source, bool sourceIsDeclaration)
        {
            var wynik = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] sciezki = sourceIsDeclaration
                ? new[]
                {
                    "Podmiot.Id",
                    "Wspolnik.Id",
                    "Wspolnik.Podmiot.Id",
                    "Podmiot.Osoba.Id",
                    "Podmiot.Osoba.Wspolnik.Id",
                    "Wspolnik.Osoba.Id",
                    "Wspolnik.Podmiot.Osoba.Id",
                    "Wspolnik.Podmiot.Osoba.Wspolnik.Id"
                }
                : new[]
                {
                    "Id",
                    "Osoba.Id",
                    "Osoba.Wspolnik.Id",
                    "Wspolnik.Id",
                    "Podmiot.Id"
                };

            foreach (string sciezka in sciezki)
            {
                object value = PobierzPoSciezce(source, sciezka);
                string id = value?.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    wynik.Add(id.Trim());
                }
            }

            return wynik;
        }

        private string PobierzStringPoSciezkach(object source, params string[] paths)
        {
            foreach (string path in paths ?? Array.Empty<string>())
            {
                object value = PobierzPoSciezce(source, path);
                string text = value?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            return null;
        }

        private int? PobierzIntPoSciezkach(object source, params string[] paths)
        {
            foreach (string path in paths ?? Array.Empty<string>())
            {
                object value = PobierzPoSciezce(source, path);
                if (value == null)
                {
                    continue;
                }

                if (value is int intValue) return intValue;
                if (int.TryParse(value.ToString(), out int parsed)) return parsed;
            }

            return null;
        }

        private Guid? PobierzGuidPoSciezkach(object source, params string[] paths)
        {
            foreach (string path in paths ?? Array.Empty<string>())
            {
                object value = PobierzPoSciezce(source, path);
                if (value == null)
                {
                    continue;
                }

                if (value is Guid guidValue) return guidValue;
                if (Guid.TryParse(value.ToString(), out Guid parsed)) return parsed;
            }

            return null;
        }

        private DateTime? PobierzDatePoSciezkach(object source, params string[] paths)
        {
            foreach (string path in paths ?? Array.Empty<string>())
            {
                object value = PobierzPoSciezce(source, path);
                if (value == null)
                {
                    continue;
                }

                if (value is DateTime dateValue) return dateValue;
                if (DateTime.TryParse(value.ToString(), out DateTime parsed)) return parsed;
            }

            return null;
        }

        private decimal? PobierzDecimalPoSciezkach(object source, params string[] paths)
        {
            foreach (string path in paths ?? Array.Empty<string>())
            {
                object value = PobierzPoSciezce(source, path);
                if (value == null)
                {
                    continue;
                }

                try
                {
                    return Convert.ToDecimal(value);
                }
                catch
                {
                    if (decimal.TryParse(value.ToString(), out decimal parsed))
                    {
                        return parsed;
                    }
                }
            }

            return null;
        }

        private object PobierzPoSciezce(object source, string path)
        {
            object current = source;
            foreach (string segment in (path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                current = PobierzWlasciwosc(current, segment);
                if (current == null)
                {
                    return null;
                }
            }

            return current;
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

        private string NormalizujTekst(string value)
        {
            return Regex.Replace(value ?? "", "\\s+", " ").Trim().ToUpperInvariant();
        }

        private string FormatNullableGuid(Guid? value)
        {
            return value.HasValue ? value.Value.ToString() : "brak";
        }

        private string FormatNullableDate(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "brak";
        }

        private dynamic PobierzMenedzeraDeklaracji()
        {
            return PobierzMenedzera("IDeklaracje") ?? PobierzMenedzera("IDeklaracjeSkarbowe");
        }

        private dynamic UtworzDeklaracjePit(
            dynamic mgrDeklaracji,
            string nazwaWspolnika,
            string wersjaNazwa,
            Guid wersjaId,
            DateTime dataStart,
            DateTime dataKoniec,
            int liczbaIstniejacychPitow,
            int liczbaPasujacychWersji)
        {
            if (mgrDeklaracji == null)
            {
                throw new InvalidOperationException("Nie udało się pobrać managera deklaracji PIT ze Sfery.");
            }

            _logger.LogDebug("[PIT UTWÓRZ START] Wspolnik={Nazwa}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; wzorzec={WersjaNazwa}; wersjaId={WersjaId}; manager={Manager}; istniejaceDeklaracje={IstniejaceDeklaracje}; pasujaceWersje={PasujaceWersje}",
                nazwaWspolnika,
                dataStart,
                dataKoniec,
                wersjaNazwa,
                wersjaId,
                OpiszTyp((object)mgrDeklaracji),
                liczbaIstniejacychPitow,
                liczbaPasujacychWersji);

            try
            {
                // Nie dispose'ujemy BO ręcznie. Uchwyt Sfery zamyka całą sesję po zakończeniu joba.
                return mgrDeklaracji.Utworz();
            }
            catch (Exception ex) when (CzyObjectDisposed(ex))
            {
                _logger.LogWarning(ex, "[PIT RETRY] Manager deklaracji zgłosił zamknięty ObjectContext przy Utworz(). Pobieram świeży manager i ponawiam. Wspolnik={Nazwa}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; wzorzec={WersjaNazwa}; staryManager={Manager}; baseException={BaseException}",
                    nazwaWspolnika,
                    dataStart,
                    dataKoniec,
                    wersjaNazwa,
                    OpiszTyp((object)mgrDeklaracji),
                    OpiszWyjatekBazowy(ex));

                dynamic swiezyMgrDeklaracji = PobierzMenedzeraDeklaracji();
                if (swiezyMgrDeklaracji == null)
                {
                    throw new InvalidOperationException("Po ObjectDisposedException nie udało się pobrać świeżego managera deklaracji PIT.", ex);
                }

                try
                {
                    return swiezyMgrDeklaracji.Utworz();
                }
                catch (Exception retryEx)
                {
                    _logger.LogError(retryEx, "[PIT RETRY BŁĄD] Ponowne Utworz() na świeżym managerze deklaracji też się nie powiodło. Wspolnik={Nazwa}; okres={DataOd:yyyy-MM-dd}-{DataDo:yyyy-MM-dd}; wzorzec={WersjaNazwa}; nowyManager={Manager}; baseException={BaseException}",
                        nazwaWspolnika,
                        dataStart,
                        dataKoniec,
                        wersjaNazwa,
                        OpiszTyp((object)swiezyMgrDeklaracji),
                        OpiszWyjatekBazowy(retryEx));
                    throw;
                }
            }
        }

        private bool CzyObjectDisposed(Exception ex)
        {
            return ex is ObjectDisposedException || ex.GetBaseException() is ObjectDisposedException;
        }

        private string OpiszWyjatekBazowy(Exception ex)
        {
            Exception bazowy = ex?.GetBaseException();
            return bazowy == null ? "brak" : $"{bazowy.GetType().FullName}: {bazowy.Message}";
        }

        private string OpiszTyp(object value)
        {
            return value?.GetType().FullName ?? "brak";
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

        private sealed class PitDuplicateCandidate
        {
            public object Declaration { get; set; }
            public string Id { get; set; }
            public int? IdSort { get; set; }
            public string Name { get; set; }
            public string VersionName { get; set; }
            public Guid? VersionId { get; set; }
            public string Status { get; set; }
            public DateTime? DataOd { get; set; }
            public DateTime? DataDo { get; set; }
            public string PartnerName { get; set; }
            public HashSet<string> PartnerIds { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool LooksLikePit { get; set; }
            public bool MatchPeriod { get; set; }
            public bool MatchVersion { get; set; }
            public bool MatchPartner { get; set; }
            public bool ActiveForDuplicateCheck { get; set; }
            public bool ExactDuplicate { get; set; }
            public string PeriodMatchSource { get; set; }
        }
    }
}
