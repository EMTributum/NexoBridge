using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class AmortizationService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<AmortizationService> _logger;

        public AmortizationService(Uchwyt sfera, ILogger<AmortizationService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public Task<AmortizationReport> ObliczAmortyzacjeAsync(DateTime dataRozliczenia)
        {
            var raport = new AmortizationReport
            {
                Processed = false,
                DocumentsGenerated = 0,
                PartialDocumentsGenerated = 0,
                CollectiveDocumentGenerated = false,
                CollectiveOperationId = null,
                CollectiveDocumentNumber = null,
                TotalCostAdded = 0m,
                Warning = null
            };

            try
            {
                // Ustawiamy datę na ostatni dzień analizowanego miesiąca (Nexo wymaga tego do poprawnego księgowania rat)
                int ostatniDzien = DateTime.DaysInMonth(dataRozliczenia.Year, dataRozliczenia.Month);
                DateTime dataAmortyzacji = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, ostatniDzien);

                _logger.LogInformation("Rozpoczynam zautomatyzowane naliczanie amortyzacji za {Data}", dataAmortyzacji.ToShortDateString());

                dynamic mgrTypow = PobierzMenedzera("ITypyAmortyzacji");
                if (mgrTypow == null)
                {
                    raport.Warning = "Brak menedżera typów amortyzacji.";
                    _logger.LogWarning(raport.Warning);
                    return Task.FromResult(raport);
                }

                TypAmortyzacji typPodatkowy = ((IEnumerable)mgrTypow.Dane.Wszystkie())
                    .Cast<TypAmortyzacji>()
                    .FirstOrDefault(t => string.Equals(t.Nazwa, "Podatkowy", StringComparison.OrdinalIgnoreCase));
                if (typPodatkowy == null)
                {
                    raport.Warning = "W systemie brak wymaganego typu amortyzacji 'Podatkowy'.";
                    _logger.LogWarning(raport.Warning);
                    return Task.FromResult(raport);
                }

                dynamic mgrST = PobierzMenedzera("ISrodkiTrwale");
                dynamic mgrAM = PobierzMenedzera("IOperacjeAM");

                if (mgrST == null || mgrAM == null)
                {
                    raport.Warning = "Brak licencji Sfery na moduł Środków Trwałych lub brak dostępu do danych.";
                    _logger.LogWarning(raport.Warning);
                    return Task.FromResult(raport);
                }

                var wszystkieST = ((IEnumerable)mgrST.Dane.Wszystkie()).Cast<dynamic>().ToList();

                if (wszystkieST.Count == 0)
                {
                    _logger.LogInformation("W ewidencji nie znaleziono żadnych środków trwałych. Zwracam kwotę 0 zł.");
                    raport.Processed = true;
                    return Task.FromResult(raport);
                }

                decimal sumaKosztow = 0;
                int naliczoneDokumenty = 0;
                var naliczoneOperacje = new List<OperacjaAM>();

                // Iterujemy przez wszystkie środki trwałe i próbujemy naliczyć ratę dla każdego z nich
                foreach (var st in wszystkieST)
                {
                    string nazwaST = "Nieznany ST";
                    try { nazwaST = st.Nazwa; } catch { }

                    using (dynamic amBO = mgrAM.Utworz())
                    {
                        try
                        {
                            // Główna, ukryta metoda silnika wyliczająca matematykę amortyzacji!
                            amBO.NaliczAmortyzacje(st, typPodatkowy, dataAmortyzacji);

                            // Bezpieczne i szerokie wyciąganie kwoty z obiektu (różne wersje nexo różnie to nazywają)
                            decimal kwotaKoszty = 0m;
                            try { kwotaKoszty = (decimal)amBO.Dane.Wartosc; } catch { }
                            try { if (kwotaKoszty == 0m) kwotaKoszty = (decimal)amBO.Dane.WartoscStanowiacaKoszty; } catch { }
                            try { if (kwotaKoszty == 0m) kwotaKoszty = (decimal)amBO.Dane.Kwota; } catch { }

                            if (kwotaKoszty > 0)
                            {
                                if (amBO.Zapisz())
                                {
                                    _logger.LogInformation("Naliczono i ZAPISANO ratę dla: {NazwaST} | Koszt wliczany do PIT: {Kwota} zł", nazwaST, kwotaKoszty);
                                    sumaKosztow += kwotaKoszty;
                                    naliczoneDokumenty++;

                                    try
                                    {
                                        naliczoneOperacje.Add((OperacjaAM)amBO.Dane);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning("Nie udało się pobrać zapisanej operacji AM dla: {NazwaST}. Powód: {Blad}", nazwaST, ex.Message);
                                    }
                                }
                                else
                                {
                                    string bladWalidacji = PobierzBledyWalidacji(amBO);
                                    _logger.LogWarning("Odrzucono zapis amortyzacji dla: {NazwaST}. Powód: {Blad}", nazwaST, bladWalidacji);
                                }
                            }
                            else
                            {
                                _logger.LogInformation("Pominięto zapis dla: {NazwaST}. Wyliczona rata wynosi 0 zł.", nazwaST);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Podbijamy z LogDebug na LogWarning, aby widzieć powód w konsoli!
                            _logger.LogWarning("Środek '{NazwaST}' pominięty (Prawdopodobnie rata już istnieje). Powód Sfery: {Wiadomosc}", nazwaST, ex.Message);
                        }
                    }
                }

                raport.Processed = true;
                raport.PartialDocumentsGenerated = naliczoneDokumenty;
                raport.TotalCostAdded = sumaKosztow;

                // Informacja dla frontendu, jeśli proces wyliczył 0 sztuk
                if (naliczoneDokumenty == 0)
                {
                    raport.Warning = "Nie wygenerowano nowych odpisów (środki zamortyzowane lub raty już istnieją).";
                }
                else
                {
                    var okresZbiorczej = UtworzOkresMiesiaca(dataAmortyzacji);
                    var operacjeSpozaOkresu = naliczoneOperacje
                        .Where(o => !CzyDataWOkresie(o.Data, okresZbiorczej))
                        .ToList();

                    if (operacjeSpozaOkresu.Count > 0)
                    {
                        _logger.LogWarning(
                            "Nie utworzę amortyzacji zbiorczej AMZ, bo {Ilosc} odpisów cząstkowych ma datę poza okresem {Od}-{Do}. Odpisy: {Odpisy}",
                            operacjeSpozaOkresu.Count,
                            okresZbiorczej.DataPoczatkowa.ToShortDateString(),
                            okresZbiorczej.DataKoncowa.ToShortDateString(),
                            string.Join(" || ", operacjeSpozaOkresu.Select(OpiszOperacjeAM)));

                        raport.DocumentsGenerated = 0;
                        raport.Warning = "Naliczono odpisy cząstkowe, ale nie udało się utworzyć amortyzacji zbiorczej: część odpisów ma datę poza rozliczanym miesiącem.";
                        return Task.FromResult(raport);
                    }

                    var zbiorcza = UtworzAmortyzacjeZbiorcza(naliczoneOperacje, typPodatkowy, okresZbiorczej, dataAmortyzacji);
                    if (zbiorcza != null)
                    {
                        raport.DocumentsGenerated = 1;
                        raport.CollectiveDocumentGenerated = true;
                        raport.CollectiveOperationId = zbiorcza.Id;
                        raport.CollectiveDocumentNumber = zbiorcza.DokumentDoKsiegowania?.NumerDokumentu ?? zbiorcza.Id.ToString();
                    }
                    else
                    {
                        raport.DocumentsGenerated = 0;
                        raport.Warning = "Naliczono odpisy cząstkowe, ale nie udało się utworzyć amortyzacji zbiorczej. Dekretacja cząstkowych odpisów została pominięta.";
                    }
                }

                _logger.LogInformation("[MODUŁ AMORTYZACJI ZAKOŃCZONY] Odpisy cząstkowe: {Czastkowe}. Zbiorcza: {Zbiorcza}. Całkowita kwota wpompowana do PIT: {Koszt} zł",
                    raport.PartialDocumentsGenerated,
                    raport.CollectiveDocumentGenerated ? raport.CollectiveDocumentNumber ?? raport.CollectiveOperationId?.ToString() ?? "TAK" : "NIE",
                    sumaKosztow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Krytyczny błąd głównego modułu podczas naliczania operacji amortyzacji.");
                raport.Warning = $"Krytyczny błąd silnika Sfery: {ex.Message}";
            }

            return Task.FromResult(raport);
        }

        private OperacjaAMZ UtworzAmortyzacjeZbiorcza(List<OperacjaAM> operacjeAM, TypAmortyzacji typAmortyzacji, OkresWymagany okresAmortyzacji, DateTime dataAmortyzacji)
        {
            if (operacjeAM == null || operacjeAM.Count == 0)
            {
                return null;
            }

            if (typAmortyzacji == null)
            {
                _logger.LogWarning("Nie utworzę amortyzacji zbiorczej AMZ, bo nie przekazano typu amortyzacji.");
                return null;
            }

            dynamic mgrAMZ = PobierzMenedzera("IOperacjeAMZ");
            if (mgrAMZ == null)
            {
                _logger.LogWarning("Nie udało się pobrać menedżera IOperacjeAMZ. Nie utworzę amortyzacji zbiorczej.");
                return null;
            }

            using (dynamic amzBO = mgrAMZ.Utworz())
            {
                try
                {
                    amzBO.Dane.TypAmortyzacji = typAmortyzacji;
                    amzBO.Dane.Okres = okresAmortyzacji;
                    amzBO.Dane.Data = dataAmortyzacji;

                    _logger.LogInformation(
                        "[AMORTYZACJA ZBIORCZA PLAN] Okres={Od}-{Do}; Data={Data}; Odpisy={Odpisy}",
                        okresAmortyzacji.DataPoczatkowa.ToShortDateString(),
                        okresAmortyzacji.DataKoncowa.ToShortDateString(),
                        dataAmortyzacji.ToShortDateString(),
                        string.Join(" || ", operacjeAM.Select(OpiszOperacjeAM)));

                    amzBO.DodajAmortyzacje(operacjeAM);
                    amzBO.InicjujOpisKsiegowy();

                    if (!amzBO.Zapisz())
                    {
                        string bladWalidacji = PobierzBledyWalidacji(amzBO);
                        _logger.LogWarning("Nie udało się zapisać amortyzacji zbiorczej AMZ. Powód: {Blad}", bladWalidacji);
                        return null;
                    }

                    OperacjaAMZ operacjaAMZ = (OperacjaAMZ)amzBO.Dane;
                    DokumentDoKsiegowania dokument = operacjaAMZ.DokumentDoKsiegowania;

                    if (dokument == null)
                    {
                        _logger.LogWarning("Amortyzacja zbiorcza AMZ {Id} została zapisana, ale nie ma dokumentu do księgowania.", operacjaAMZ.Id);
                        return null;
                    }

                    if ((int)dokument.StatusKsiegowy != (int)StatusKsiegowyDokumentuDoKsiegowania.Opisany)
                    {
                        dokument.StatusKsiegowy = (byte)StatusKsiegowyDokumentuDoKsiegowania.Opisany;
                        if (!amzBO.Zapisz())
                        {
                            string bladWalidacji = PobierzBledyWalidacji(amzBO);
                            _logger.LogWarning("Nie udało się ustawić statusu Opisany dla dokumentu AMZ {Numer}. Powód: {Blad}",
                                dokument.NumerDokumentu,
                                bladWalidacji);
                            return null;
                        }
                    }

                    _logger.LogInformation("[AMORTYZACJA ZBIORCZA] Utworzono AMZ Id={Id}; Dokument={Numer}; StatusKsiegowy={Status}; Data={Data}; Odpisy={Ilosc}; Kwota={Kwota}",
                        operacjaAMZ.Id,
                        dokument.NumerDokumentu,
                        dokument.StatusKsiegowy,
                        dataAmortyzacji.ToShortDateString(),
                        operacjeAM.Count,
                        operacjaAMZ.WartoscStanowiacaKoszty);

                    return operacjaAMZ;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Nie udało się utworzyć amortyzacji zbiorczej AMZ dla {Ilosc} odpisów cząstkowych.", operacjeAM.Count);
                    return null;
                }
            }
        }

        private OkresWymagany UtworzOkresMiesiaca(DateTime data)
        {
            DateTime dataPoczatkowa = new DateTime(data.Year, data.Month, 1);
            DateTime dataKoncowa = new DateTime(data.Year, data.Month, DateTime.DaysInMonth(data.Year, data.Month));

            return new OkresWymagany
            {
                DataPoczatkowa = dataPoczatkowa,
                DataKoncowa = dataKoncowa
            };
        }

        private bool CzyDataWOkresie(DateTime data, OkresWymagany okres)
        {
            return data.Date >= okres.DataPoczatkowa.Date && data.Date <= okres.DataKoncowa.Date;
        }

        private string OpiszOperacjeAM(OperacjaAM operacja)
        {
            if (operacja == null)
            {
                return "null";
            }

            string nazwaST = "brak ST";
            try { nazwaST = operacja.SrodekTrwaly?.Nazwa ?? nazwaST; } catch { }

            return $"Id={operacja.Id}, Data={operacja.Data:yyyy-MM-dd}, ST={nazwaST}, Kwota={operacja.WartoscStanowiacaKoszty}";
        }

        private string PobierzBledyWalidacji(dynamic bo)
        {
            try
            {
                var invalid = (IEnumerable)bo.InvalidData;
                if (invalid != null)
                {
                    var bledy = invalid.Cast<dynamic>()
                        .Select(e => (string)(e.Komunikat ?? e.Tresc ?? e.ToString()))
                        .Where(e => !string.IsNullOrWhiteSpace(e))
                        .ToList();

                    if (bledy.Count > 0) return string.Join(" | ", bledy);
                }
            }
            catch { }

            return "Brak szczegółów w InvalidData";
        }

        // --- PRYWATNE METODY REFLEKSYJNE ---
        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Type bestMatch = null;

            foreach (var assembly in assemblies)
            {
                if (assembly.FullName.StartsWith("System.") || assembly.FullName.StartsWith("Microsoft.")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                var found = types.FirstOrDefault(t => t != null && t.IsInterface && t.Name == nazwa);
                if (found != null)
                {
                    string ns = found.Namespace ?? "";
                    if (ns.Contains(".UI") || ns.Contains(".Web") || ns.Contains(".Klient") || ns.Contains(".Raporty"))
                    {
                        if (bestMatch == null) bestMatch = found;
                        continue;
                    }
                    return found;
                }
            }
            return bestMatch;
        }

        private dynamic PobierzMenedzera(string nazwaInterfejsu)
        {
            var typSzukany = ZnajdzTypInterfejsu(nazwaInterfejsu);
            if (typSzukany != null)
            {
                var metoda = _sfera.GetType().GetMethods().FirstOrDefault(m => m.Name == "PodajObiektTypu" && m.GetParameters().Length == 0);
                if (metoda != null) return metoda.MakeGenericMethod(typSzukany).Invoke(_sfera, null);
            }
            return null;
        }
    }
}
