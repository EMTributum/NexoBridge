using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class VatCalculationService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<VatCalculationService> _logger;

        public VatCalculationService(Uchwyt sfera, ILogger<VatCalculationService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task<VatReport> WygenerujJpkVatAsync(DateTime dataRozliczenia)
        {
            var raport = new VatReport
            {
                IsVatPayer = false,
                AmountToPay = 0m,
                AmountToCarryOver = 0m,
                ErrorMsg = null
                // ZALECENIE: Dodaj do modelu VatReport właściwość `InfoMsg`, 
                // żeby móc przekazywać łagodne komunikaty na front, bez wywoływania ErrorMsg.
                // InfoMsg = null 
            };

            try
            {
                _logger.LogInformation("Rozpoczynam weryfikację i generowanie JPK_V7 dla: {Data:yyyy-MM}", dataRozliczenia);

                // ==============================================================================
                // FAZA 1: PANCERNA WERYFIKACJA STATUSU PODATNIKA VAT W BAZIE
                // ==============================================================================
                dynamic mgrOkresyVat = PobierzMenedzera("IOkresyRozliczenVAT");
                if (mgrOkresyVat == null)
                {
                    raport.ErrorMsg = "Brak menedżera okresów VAT. Upewnij się, że firma ma licencję Rachmistrza/Rewizora.";
                    return raport;
                }

                var okresyVat = ((IEnumerable)mgrOkresyVat.Dane.Wszystkie()).Cast<dynamic>().ToList();

                // Szukamy najnowszej konfiguracji dla krajowego VAT (Rodzaj = 1)
                var glownyOkres = okresyVat
                    .Where(o => { try { return (int)o.Rodzaj == 1; } catch { return false; } })
                    .OrderByDescending(o => { try { return (int)o.Id; } catch { return 0; } })
                    .FirstOrDefault();

                if (glownyOkres == null)
                {
                    // Brak ErrorMsg = pełen sukces procesu na froncie
                    raport.IsVatPayer = false;
                    _logger.LogInformation("[VAT POMINIĘTO] Firma nie posiada żadnej konfiguracji ewidencji VAT krajowego.");
                    return raport;
                }

                // Odczytujemy surowy kod Metody z bazy (1 = Miesięcznie, 2 = Kwartalnie, 4 = Zwolniony)
                byte metodaRozliczen = 0;
                try { metodaRozliczen = (byte)glownyOkres.Metoda; } catch { }

                if (metodaRozliczen == 4)
                {
                    // ZWOLNIONY Z VAT - Czyste wyjście, bez błędów
                    raport.IsVatPayer = false;
                    _logger.LogInformation("[VAT POMINIĘTO] Firma jest ustawiona jako ZWOLNIONA z podatku VAT (Metoda = 4).");
                    return raport;
                }
                else if (metodaRozliczen == 2)
                {
                    // Tutaj rzucamy błąd, bo automatyzacja nie obsłuży mu kwartalnego JPK_V7K (jeszcze)
                    raport.ErrorMsg = "Firma rozlicza VAT KWARTALNIE (Metoda = 2). Wymagany format JPK_V7K, obecny mechanizm wspiera V7M.";
                    raport.IsVatPayer = true;
                    _logger.LogWarning("[VAT ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return raport;
                }
                else if (metodaRozliczen != 1)
                {
                    raport.ErrorMsg = $"Firma używa nieobsługiwanej metody rozliczeń VAT (Kod: {metodaRozliczen}).";
                    raport.IsVatPayer = true;
                    _logger.LogWarning("[VAT ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return raport;
                }

                // W tym miejscu wiemy na 100%, że firma to czynny "VAT-owiec" z rozliczeniem miesięcznym (Metoda == 1)
                raport.IsVatPayer = true;

                // ==============================================================================
                // FAZA 2: ZABEZPIECZENIE PRZED DUPLIKATAMI I POBRANIE WZORCÓW
                // ==============================================================================
                dynamic mgrDeklaracji = PobierzMenedzera("IDeklaracjeSkarbowe") ?? PobierzMenedzera("IDeklaracje");
                dynamic mgrWersji = PobierzMenedzera("IWersjeDeklaracji");
                dynamic mgrPodmioty = PobierzMenedzera("IPodmioty");

                if (mgrDeklaracji == null || mgrWersji == null || mgrPodmioty == null)
                {
                    raport.ErrorMsg = "Nie udało się załadować w Sferze podstawowych menedżerów (Deklaracje / Podmioty).";
                    return raport;
                }

                // Sprawdzanie duplikatów: Czy JPK za ten miesiąc już istnieje w bazie?
                var wszystkieDeklaracje = ((IEnumerable)mgrDeklaracji.Dane.Wszystkie()).Cast<dynamic>().ToList();
                var istniejacyJpk = wszystkieDeklaracje.FirstOrDefault(j =>
                {
                    try
                    {
                        string tytul = (string)j.Tytul ?? (string)j.Wzorzec?.Nazwa ?? "";
                        return tytul.Contains("V7") && j.Miesiac == dataRozliczenia.Month && j.Rok == dataRozliczenia.Year;
                    }
                    catch { return false; }
                });

                if (istniejacyJpk != null)
                {
                    raport.ErrorMsg = $"Plik JPK_V7 za {dataRozliczenia:MM/yyyy} został już wygenerowany wcześniej.";
                    _logger.LogWarning("[VAT ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return raport;
                }

                // ==============================================================================
                // FAZA 3: GENEROWANIE DOKUMENTU JPK_V7M (PRZEZ SILNIK WYWYLICZAJĄCY SFERY)
                // ==============================================================================

                var mojaFirma = ((IEnumerable)mgrPodmioty.Dane.Wszystkie()).Cast<dynamic>().FirstOrDefault();

                var wszystkieWersje = ((IEnumerable)mgrWersji.Wersje).Cast<dynamic>().ToList();
                var wybranyWzorzec = wszystkieWersje
                    .Where(w => { try { return ((string)w.Nazwa).Contains("V7M"); } catch { return false; } })
                    .OrderByDescending(w => w.Id)
                    .FirstOrDefault();

                if (wybranyWzorzec == null || mojaFirma == null)
                {
                    raport.ErrorMsg = "Błąd inicjalizacji: Brak wzorca JPK_V7M lub podmiotu w bazie SQL.";
                    return raport;
                }

                DateTime dataOd = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, 1);
                DateTime dataDo = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, DateTime.DaysInMonth(dataRozliczenia.Year, dataRozliczenia.Month));

                using (dynamic jpkBO = mgrDeklaracji.Utworz())
                {
                    try
                    {
                        _logger.LogInformation("Zlecam Sferze wyliczenie JPK z ewidencji VAT...");
                        jpkBO.Wylicz(mojaFirma, (Guid)wybranyWzorzec.Id, dataOd, dataDo, null);
                    }
                    catch (Exception ex)
                    {
                        raport.ErrorMsg = $"Sfera odrzuciła wyliczenie JPK. Powód: {ex.InnerException?.Message ?? ex.Message}";
                        return raport;
                    }

                    // Pobieramy kwoty po wyliczeniu
                    try { raport.AmountToPay = (decimal)jpkBO.Dane.KwotaZobowiazania; } catch { }
                    try { if (raport.AmountToPay == 0m) raport.AmountToPay = (decimal)jpkBO.Dane.KwotaDoZaplaty; } catch { }
                    try { raport.AmountToCarryOver = (decimal)jpkBO.Dane.KwotaDoPrzeniesienia; } catch { }

                    // Właściwy Zapis do Bazy SQL
                    if (jpkBO.Zapisz())
                    {
                        _logger.LogInformation("[VAT SUKCES] Wygenerowano i zapisano JPK_V7. Do zapłaty: {VAT} PLN", raport.AmountToPay);
                    }
                    else
                    {
                        string opisBledu = "Sfera odrzuciła zapis dokumentu. Błąd walidacji wewnętrznej.";
                        try
                        {
                            var bledyKolekcja = (IEnumerable)jpkBO.InvalidData;
                            if (bledyKolekcja != null)
                            {
                                var bledyList = bledyKolekcja.Cast<dynamic>().Select(e =>
                                {
                                    try { return (string)e.Komunikat ?? (string)e.Tresc ?? (string)e.Opis; }
                                    catch { return e.ToString(); }
                                }).ToList();

                                if (bledyList.Any()) opisBledu = "Odrzucono. Powody: " + string.Join(" | ", bledyList);
                            }
                        }
                        catch { }

                        raport.ErrorMsg = opisBledu;
                        _logger.LogWarning("[VAT ODRZUCONO] {Msg}", raport.ErrorMsg);
                    }
                }
            }
            catch (Exception ex)
            {
                raport.ErrorMsg = ex.InnerException?.Message ?? ex.Message;
                _logger.LogError(ex, "[VAT BŁĄD KRYTYCZNY] Wystąpił niespodziewany wyjątek w serwisie VAT.");
            }

            return raport;
        }

        // ==============================================================================
        // METODY POMOCNICZE WZORCOWE DLA SFERY
        // ==============================================================================
        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Omijamy standardowe biblioteki systemowe dla szybkości
                if (assembly.FullName.StartsWith("System.") || assembly.FullName.StartsWith("Microsoft.")) continue;

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
    }
}