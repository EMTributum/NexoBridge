using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
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

        public async Task<AmortizationReport> ObliczAmortyzacjeAsync(DateTime dataRozliczenia)
        {
            var raport = new AmortizationReport
            {
                Processed = false,
                DocumentsGenerated = 0,
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
                    return raport;
                }

                var typPodatkowy = ((IEnumerable)mgrTypow.Dane.Wszystkie()).Cast<dynamic>().FirstOrDefault(t => t.Nazwa == "Podatkowy");
                if (typPodatkowy == null)
                {
                    raport.Warning = "W systemie brak wymaganego typu amortyzacji 'Podatkowy'.";
                    _logger.LogWarning(raport.Warning);
                    return raport;
                }

                dynamic mgrST = PobierzMenedzera("ISrodkiTrwale");
                dynamic mgrAM = PobierzMenedzera("IOperacjeAM");

                if (mgrST == null || mgrAM == null)
                {
                    raport.Warning = "Brak licencji Sfery na moduł Środków Trwałych lub brak dostępu do danych.";
                    _logger.LogWarning(raport.Warning);
                    return raport;
                }

                var wszystkieST = ((IEnumerable)mgrST.Dane.Wszystkie()).Cast<dynamic>().ToList();

                if (wszystkieST.Count == 0)
                {
                    _logger.LogInformation("W ewidencji nie znaleziono żadnych środków trwałych. Zwracam kwotę 0 zł.");
                    raport.Processed = true;
                    return raport;
                }

                decimal sumaKosztow = 0;
                int naliczoneDokumenty = 0;

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

                            // Wyciągamy to, co fizycznie stanowi koszt (KUP) do PIT
                            decimal kwotaKoszty = 0;
                            try { kwotaKoszty = amBO.Dane.WartoscStanowiacaKoszty; } catch { try { kwotaKoszty = amBO.Dane.Wartosc; } catch { } }

                            if (kwotaKoszty > 0)
                            {
                                if (amBO.Zapisz())
                                {
                                    _logger.LogInformation("Naliczono i ZAPISANO ratę dla: {NazwaST} | Koszt wliczany do PIT: {Kwota} zł", nazwaST, kwotaKoszty);
                                    sumaKosztow += kwotaKoszty;
                                    naliczoneDokumenty++;
                                }
                                else
                                {
                                    _logger.LogWarning("Błąd zapisu dokumentu amortyzacji dla: {NazwaST}. Prawdopodobnie brak wymaganych danych.", nazwaST);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // System rzuci wyjątkiem, jeśli na ten miesiąc i dany środek nie przewidziano odpisu
                            _logger.LogDebug("Środek '{NazwaST}' pominięty w tym miesiącu. (Powód systemowy: {Wiadomosc})", nazwaST, ex.Message);
                        }
                    }
                }

                raport.Processed = true;
                raport.DocumentsGenerated = naliczoneDokumenty;
                raport.TotalCostAdded = sumaKosztow;

                _logger.LogInformation("[MODUŁ AMORTYZACJI ZAKOŃCZONY] Wygenerowano dokumenty: {Ilosc}. Całkowita kwota wpompowana do PIT: {Koszt} zł", naliczoneDokumenty, sumaKosztow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Krytyczny błąd głównego modułu podczas naliczania operacji amortyzacji.");
                raport.Warning = $"Krytyczny błąd silnika Sfery: {ex.Message}";
            }

            return raport;
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