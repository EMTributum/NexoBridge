using InsERT.Moria.KontrolaSkarbowa;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NexoBridge.Services
{
    public class VatCalculationService
    {
        private const string DomyslnyAdresEmailJpk = "biuro@emtributum.pl";

        private readonly Uchwyt _sfera;
        private readonly ILogger<VatCalculationService> _logger;

        public VatCalculationService(Uchwyt sfera, ILogger<VatCalculationService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public Task<VatReport> WygenerujJpkVatAsync(DateTime dataRozliczenia)
        {
            var raport = new VatReport
            {
                IsVatPayer = false,
                AmountToPay = 0m,
                AmountToCarryOver = 0m,
                ErrorMsg = null
            };

            try
            {
                _logger.LogInformation("Rozpoczynam weryfikację i generowanie JPK_V7 dla: {Data:yyyy-MM}", dataRozliczenia);

                dynamic mgrOkresyVat = PobierzMenedzera("IOkresyRozliczenVAT");
                if (mgrOkresyVat == null)
                {
                    raport.ErrorMsg = "Brak menedżera okresów VAT. Upewnij się, że firma ma licencję Rachmistrza/Rewizora.";
                    return Task.FromResult(raport);
                }

                DateTime dataOd = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, 1);
                DateTime dataDo = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, DateTime.DaysInMonth(dataRozliczenia.Year, dataRozliczenia.Month));

                var okresyVat = ((IEnumerable)mgrOkresyVat.Dane.Wszystkie()).Cast<object>().ToList();
                LogujOkresyVat(okresyVat, dataOd, dataDo);

                object glownyOkres = ZnajdzOkresVatKrajowyDlaOkresu(okresyVat, dataDo);

                if (glownyOkres == null)
                {
                    raport.IsVatPayer = false;
                    _logger.LogInformation("[VAT POMINIĘTO] Firma nie posiada konfiguracji ewidencji VAT krajowego aktywnej dla okresu {Okres:yyyy-MM}.",
                        dataRozliczenia);
                    return Task.FromResult(raport);
                }

                byte metodaRozliczen = 0;
                if (TryReadIntProperty(glownyOkres, "Metoda", out int metodaRozliczenInt))
                {
                    metodaRozliczen = (byte)metodaRozliczenInt;
                }

                if (metodaRozliczen == 4)
                {
                    raport.IsVatPayer = false;
                    _logger.LogInformation("[VAT POMINIĘTO] Firma jest ustawiona jako ZWOLNIONA z podatku VAT (Metoda = 4).");
                    return Task.FromResult(raport);
                }
                else if (metodaRozliczen == 2)
                {
                    raport.ErrorMsg = "Firma rozlicza VAT KWARTALNIE (Metoda = 2). Wymagany format JPK_V7K, obecny mechanizm wspiera V7M.";
                    raport.IsVatPayer = true;
                    _logger.LogWarning("[VAT ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return Task.FromResult(raport);
                }
                else if (metodaRozliczen != 1)
                {
                    raport.ErrorMsg = $"Firma używa nieobsługiwanej metody rozliczeń VAT (Kod: {metodaRozliczen}).";
                    raport.IsVatPayer = true;
                    _logger.LogWarning("[VAT ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return Task.FromResult(raport);
                }

                raport.IsVatPayer = true;

                dynamic istniejacyJpk = ZnajdzJpkV7M(dataOd, dataDo);
                if (istniejacyJpk != null)
                {
                    WypelnijKwotyZJpkLubFallback(raport, istniejacyJpk, dataOd, dataDo);
                    raport.ErrorMsg = $"Plik JPK_V7 za {dataRozliczenia:MM/yyyy} został już wygenerowany wcześniej.";
                    string opisJpk = OpiszJpk(istniejacyJpk);
                    _logger.LogWarning("[VAT ODRZUCONO] {Msg} Istniejący plik: {Jpk}", raport.ErrorMsg, opisJpk);
                    return Task.FromResult(raport);
                }

                _logger.LogInformation("Zlecam Sferze naliczenie widocznego JPK_V7M przez moduł KontrolaSkarbowa...");
                if (!WygenerujWidocznyJpkV7M(dataOd, dataDo, out dynamic wygenerowanyJpk, out string bladGenerowania))
                {
                    raport.ErrorMsg = bladGenerowania;
                    _logger.LogWarning("[VAT ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return Task.FromResult(raport);
                }

                WypelnijKwotyZJpkLubFallback(raport, wygenerowanyJpk, dataOd, dataDo);

                string opisWygenerowanegoJpk = OpiszJpk(wygenerowanyJpk);
                _logger.LogInformation("[VAT SUKCES] Wygenerowano i zapisano widoczny JPK_V7M. Do zapłaty: {VAT} PLN. JPK={Jpk}",
                    raport.AmountToPay,
                    opisWygenerowanegoJpk);
            }
            catch (Exception ex)
            {
                raport.ErrorMsg = ex.InnerException?.Message ?? ex.Message;
                _logger.LogError(ex, "[VAT BŁĄD KRYTYCZNY] Wystąpił niespodziewany wyjątek w serwisie VAT.");
            }

            return Task.FromResult(raport);
        }

        private dynamic PobierzMojaFirme(dynamic mgrPodmioty)
        {
            try
            {
                dynamic mojaFirmaBO = mgrPodmioty.ZnajdzMojaFirme();
                if (mojaFirmaBO == null) return null;
                return mojaFirmaBO.Dane;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nie udało się pobrać MojaFirma przez IPodmioty.ZnajdzMojaFirme().");
                return null;
            }
        }

        private object ZnajdzOkresVatKrajowyDlaOkresu(IEnumerable<object> okresyVat, DateTime dataDo)
        {
            return (okresyVat ?? Enumerable.Empty<object>())
                .Where(o => TryReadIntProperty(o, "Rodzaj", out int rodzaj) && rodzaj == 1)
                .Where(o => !TryReadDateProperty(o, "Poczatek", out DateTime poczatek) || poczatek <= dataDo)
                .OrderByDescending(o => TryReadDateProperty(o, "Poczatek", out DateTime poczatek) ? poczatek : DateTime.MinValue)
                .ThenByDescending(o => TryReadIntProperty(o, "Id", out int id) ? id : 0)
                .FirstOrDefault();
        }

        private void LogujOkresyVat(IReadOnlyCollection<object> okresyVat, DateTime dataOd, DateTime dataDo)
        {
            if (okresyVat == null || okresyVat.Count == 0)
            {
                _logger.LogInformation("[VAT OKRESY] Okres={Od:yyyy-MM}; Sfera nie zwróciła konfiguracji okresów VAT.", dataOd);
                return;
            }

            var opisy = okresyVat
                .OrderBy(o => TryReadDateProperty(o, "Poczatek", out DateTime poczatek) ? poczatek : DateTime.MaxValue)
                .ThenBy(o => TryReadIntProperty(o, "Id", out int id) ? id : 0)
                .Take(30)
                .Select(o => OpiszWybraneWlasciwosci(o, "Id", "Poczatek", "Rodzaj", "Metoda", "MetodaKasowa", "PrzyczynaZwolnieniaVATId"))
                .ToList();

            _logger.LogInformation("[VAT OKRESY] Okres={Od:yyyy-MM}; zakres={Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}; liczba={Count}; konfiguracje={Konfiguracje}",
                dataOd,
                dataOd,
                dataDo,
                okresyVat.Count,
                string.Join(" || ", opisy));
        }

        private void WypelnijKwotyZJpkLubFallback(VatReport raport, dynamic jpk, DateTime dataOd, DateTime dataDo)
        {
            if (WypelnijKwotyZPowiazanejDeklaracji(raport, jpk))
            {
                _logger.LogInformation("[VAT KWOTY] Odczytano kwoty z deklaracji powiązanej z zapisanym JPK.");
                return;
            }

            if (WypelnijKwotyZXmlJpk(raport, jpk))
            {
                _logger.LogInformation("[VAT KWOTY] Odczytano kwoty z XML zapisanego JPK.");
                return;
            }

            _logger.LogWarning("[VAT KWOTY] Nie udało się odczytać kwot z zapisanego JPK. Używam awaryjnego wyliczenia deklaracyjnego bez zapisu.");
            WypelnijKwotyDeklaracyjnieAwaryjnie(raport, dataOd, dataDo);
        }

        private bool WypelnijKwotyZPowiazanejDeklaracji(VatReport raport, dynamic jpk)
        {
            try
            {
                var czesci = ((IEnumerable)jpk.CzesciDeklaracyjne).Cast<dynamic>().ToList();
                foreach (var czesc in czesci)
                {
                    dynamic deklaracja = czesc.Deklaracja;
                    if (deklaracja == null) continue;

                    bool znalezionoKwote = false;
                    if (TryReadDecimalProperty(deklaracja, "KwotaZobowiazania", out decimal zobowiazanie))
                    {
                        raport.AmountToPay = zobowiazanie;
                        znalezionoKwote = true;
                    }
                    else if (TryReadDecimalProperty(deklaracja, "KwotaDoZaplaty", out decimal doZaplaty))
                    {
                        raport.AmountToPay = doZaplaty;
                        znalezionoKwote = true;
                    }

                    if (TryReadDecimalProperty(deklaracja, "KwotaDoPrzeniesienia", out decimal doPrzeniesienia))
                    {
                        raport.AmountToCarryOver = doPrzeniesienia;
                        znalezionoKwote = true;
                    }

                    if (znalezionoKwote)
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private bool WypelnijKwotyZXmlJpk(VatReport raport, dynamic jpk)
        {
            string xml = PobierzXmlJpk(jpk);
            if (string.IsNullOrWhiteSpace(xml)) return false;

            try
            {
                var document = XDocument.Parse(xml);
                bool znaleziono = false;

                if (TryReadXmlDecimal(document, "P_53", out decimal kwotaDoZaplaty))
                {
                    raport.AmountToPay = kwotaDoZaplaty;
                    znaleziono = true;
                }

                if (TryReadXmlDecimal(document, "P_62", out decimal kwotaDoPrzeniesienia))
                {
                    raport.AmountToCarryOver = kwotaDoPrzeniesienia;
                    znaleziono = true;
                }

                return znaleziono;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VAT KWOTY] Nie udało się sparsować XML zapisanego JPK.");
                return false;
            }
        }

        private void WypelnijKwotyDeklaracyjnieAwaryjnie(VatReport raport, DateTime dataOd, DateTime dataDo)
        {
            try
            {
                dynamic mgrDeklaracji = PobierzMenedzera("IDeklaracjeSkarbowe") ?? PobierzMenedzera("IDeklaracje");
                dynamic mgrWersjiDeklaracji = PobierzMenedzera("IWersjeDeklaracji");
                dynamic mgrPodmioty = PobierzMenedzera("IPodmioty");
                dynamic mojaFirma = mgrPodmioty != null ? PobierzMojaFirme(mgrPodmioty) : null;

                if (mgrDeklaracji == null || mgrWersjiDeklaracji == null || mojaFirma == null)
                {
                    _logger.LogWarning("[VAT KWOTY] Brak menedżerów wymaganych do awaryjnego wyliczenia kwot VAT.");
                    return;
                }

                var wszystkieWersje = ((IEnumerable)mgrWersjiDeklaracji.Wersje).Cast<dynamic>().ToList();
                var wybranyWzorzec = wszystkieWersje
                    .Where(w => { try { return ((string)w.Nazwa).Contains("V7M"); } catch { return false; } })
                    .OrderByDescending(w => w.Id)
                    .FirstOrDefault();

                if (wybranyWzorzec == null)
                {
                    _logger.LogWarning("[VAT KWOTY] Brak wzorca JPK_V7M do awaryjnego wyliczenia kwot VAT.");
                    return;
                }

                using (dynamic jpkBO = mgrDeklaracji.Utworz())
                {
                    jpkBO.Wylicz(mojaFirma, (Guid)wybranyWzorzec.Id, dataOd, dataDo, null);

                    object daneDeklaracji = jpkBO.Dane;
                    if (TryReadDecimalProperty(daneDeklaracji, "KwotaZobowiazania", out decimal kwotaZobowiazania))
                    {
                        raport.AmountToPay = kwotaZobowiazania;
                    }

                    if (raport.AmountToPay == 0m && TryReadDecimalProperty(daneDeklaracji, "KwotaDoZaplaty", out decimal kwotaDoZaplaty))
                    {
                        raport.AmountToPay = kwotaDoZaplaty;
                    }

                    if (TryReadDecimalProperty(daneDeklaracji, "KwotaDoPrzeniesienia", out decimal kwotaDoPrzeniesienia))
                    {
                        raport.AmountToCarryOver = kwotaDoPrzeniesienia;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VAT KWOTY] Awaryjne wyliczenie deklaracyjne kwot VAT nie powiodło się.");
            }
        }

        private bool TryReadDecimalProperty(object source, string propertyName, out decimal value)
        {
            value = 0m;
            if (source == null) return false;

            try
            {
                var property = source.GetType().GetProperty(propertyName);
                if (property == null) return false;

                object rawValue = property.GetValue(source);
                return TryConvertDecimal(rawValue, out value);
            }
            catch
            {
                return false;
            }
        }

        private bool TryAssignDecimalFromDynamic(dynamic source, string propertyName, Action<decimal> assign)
        {
            if (TryReadDecimalProperty((object)source, propertyName, out decimal value))
            {
                assign(value);
                return true;
            }

            return false;
        }

        private string PobierzXmlJpk(dynamic jpk)
        {
            try
            {
                object xmlValue = jpk.Xml;
                if (xmlValue == null) return null;

                if (xmlValue is string xmlText)
                {
                    return xmlText;
                }

                if (xmlValue is byte[] bytes)
                {
                    using (var stream = new MemoryStream(bytes))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }

                return xmlValue.ToString();
            }
            catch
            {
                return null;
            }
        }

        private bool TryReadXmlDecimal(XDocument document, string elementLocalName, out decimal value)
        {
            value = 0m;
            var element = document
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals(elementLocalName, StringComparison.OrdinalIgnoreCase));

            return element != null && TryConvertDecimal(element.Value, out value);
        }

        private bool TryConvertDecimal(object rawValue, out decimal value)
        {
            value = 0m;
            if (rawValue == null) return false;

            try
            {
                if (rawValue is decimal decimalValue)
                {
                    value = decimalValue;
                    return true;
                }

                if (rawValue is IConvertible)
                {
                    value = Convert.ToDecimal(rawValue, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch { }

            string text = rawValue.ToString();
            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
                || decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("pl-PL"), out value);
        }

        private bool WygenerujWidocznyJpkV7M(DateTime dataOd, DateTime dataDo, out dynamic wygenerowanyJpk, out string blad)
        {
            wygenerowanyJpk = null;
            blad = null;
            var bledySciezek = new List<string>();

            dynamic mgrNaliczania = PobierzMenedzera("IMenadzerNaliczaniaPlikowJPK");
            if (mgrNaliczania != null)
            {
                if (WygenerujPrzezMenedzerPlikowJpk(mgrNaliczania, dataOd, dataDo, out wygenerowanyJpk, out blad))
                {
                    return true;
                }

                bledySciezek.Add($"IMenadzerNaliczaniaPlikowJPK: {blad}");
                _logger.LogWarning("[VAT JPK FALLBACK] Ogólny menedżer JPK nie wygenerował V7M. Próbuję menedżera wysyłki V7M. Powód: {Blad}", blad);
            }

            dynamic mgrWysylkiV7M = PobierzMenedzera("IMenadzerNaliczaniaWysylkiV7M");
            if (mgrWysylkiV7M != null)
            {
                if (WygenerujPrzezMenedzerWysylkiV7M(mgrWysylkiV7M, dataOd, dataDo, out wygenerowanyJpk, out blad))
                {
                    return true;
                }

                bledySciezek.Add($"IMenadzerNaliczaniaWysylkiV7M: {blad}");
            }

            if (WygenerujPrzezBezposredniJpkV7M(dataOd, dataDo, out wygenerowanyJpk, out blad))
            {
                return true;
            }

            bledySciezek.Add($"IJednolityPlikKontrolny.Generuj: {blad}");

            blad = bledySciezek.Count > 0
                ? "Nie udało się wygenerować JPK_V7M żadną dostępną ścieżką Sfery. " + string.Join(" || ", bledySciezek)
                : "Nie udało się pobrać menedżera naliczania JPK_V7M (IMenadzerNaliczaniaPlikowJPK / IMenadzerNaliczaniaWysylkiV7M).";
            return false;
        }

        private bool WygenerujPrzezMenedzerPlikowJpk(dynamic mgr, DateTime dataOd, DateTime dataDo, out dynamic wygenerowanyJpk, out string blad)
        {
            wygenerowanyJpk = null;
            blad = null;
            WynikGenerowaniaPlikuJPK wynikNaliczania = null;

            try
            {
                mgr.Inicjalizuj();
                UstawJesliMozna(mgr, "UzyjWlasnychWyrazen", false);
                UstawJesliMozna(mgr, "ZapiszPowiazaniaZEwidencjaZrodlowa", true);
                UstawJesliMozna(mgr, "Nazwa", $"JPK_V7M {dataOd:yyyy-MM}");
                UstawJesliMozna(mgr, "Opis", "Wygenerowano automatycznie przez NexoBridge.");
                UstawJesliMozna(mgr, "DomyslnaDataOd", dataOd);
                UstawJesliMozna(mgr, "DomyslnaDataDo", dataDo);

                dynamic plik = mgr.DodajPlik(RodzajJPK.V7M, dataOd, dataDo, false);
                PrzygotujParametryJpkV7(plik, dataOd, dataDo);
                UzupelnijParametryZManagera(mgr, plik);

                if (CzyDaneNiekompletne(mgr))
                {
                    string bledyWalidacji = PobierzBledyWalidacji(mgr);
                    _logger.LogWarning("[VAT JPK] Menedżer zgłasza niekompletne dane przed wyliczeniem. Szczegóły: {Bledy}", bledyWalidacji);
                    LogujDiagnostykeJpk("IMenadzerNaliczaniaPlikowJPK", mgr, plik);
                }

                Action<WynikGenerowaniaPlikuJPK> ustawWynik = wynik => wynikNaliczania = wynik;
                IDisposable subskrypcja = PodepnijNaliczonoEventHandler((object)mgr, ustawWynik, "IMenadzerNaliczaniaPlikowJPK");
                try
                {
                    mgr.Wylicz();
                }
                finally
                {
                    subskrypcja?.Dispose();
                }

                if (!CzyWynikGenerowaniaJpkPoprawny(wynikNaliczania, out string bladWyniku))
                {
                    blad = bladWyniku;
                    return false;
                }

                wygenerowanyJpk = ZnajdzJpkV7M(dataOd, dataDo) ?? ZnajdzJpkPoId(wynikNaliczania?.Id ?? 0);

                if (wygenerowanyJpk == null)
                {
                    blad = $"Sfera zakończyła naliczanie JPK_V7M, ale po operacji nie znaleziono pliku JPK w bazie. Wynik: {OpiszWynikGenerowaniaJpk(wynikNaliczania)}";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                blad = $"Sfera odrzuciła naliczenie widocznego JPK_V7M. Powód: {ex.InnerException?.Message ?? ex.Message}. Szczegóły: {PobierzBledyWalidacji(mgr)}";
                return false;
            }
            finally
            {
                ZwolnijJesliMozna(mgr);
            }
        }

        private bool WygenerujPrzezMenedzerWysylkiV7M(dynamic mgr, DateTime dataOd, DateTime dataDo, out dynamic wygenerowanyJpk, out string blad)
        {
            wygenerowanyJpk = null;
            blad = null;
            WynikGenerowaniaPlikuJPK wynikNaliczania = null;

            try
            {
                mgr.Inicjalizuj();
                PrzygotujManagerWysylkiV7M(mgr, dataOd);

                dynamic plik = null;
                try { plik = mgr.DodajDomyslnyPlik(); } catch { }
                PrzygotujParametryJpkV7(plik ?? mgr.Plik, dataOd, dataDo);
                UzupelnijParametryZManagera(mgr, plik ?? mgr.Plik);
                LogujBrakiParametrowJpk("IMenadzerNaliczaniaWysylkiV7M", mgr, plik ?? mgr.Plik);

                if (CzyDaneNiekompletne(mgr))
                {
                    string bledyWalidacji = PobierzBledyWalidacji(mgr);
                    _logger.LogWarning("[VAT JPK] Menedżer V7M zgłasza niekompletne dane przed wyliczeniem. Szczegóły: {Bledy}", bledyWalidacji);
                    LogujDiagnostykeJpk("IMenadzerNaliczaniaWysylkiV7M", mgr, plik ?? mgr.Plik);
                }

                Action<WynikGenerowaniaPlikuJPK> ustawWynik = wynik => wynikNaliczania = wynik;
                IDisposable subskrypcja = PodepnijNaliczonoEventHandler((object)mgr, ustawWynik, "IMenadzerNaliczaniaWysylkiV7M");
                try
                {
                    mgr.Wylicz();
                }
                finally
                {
                    subskrypcja?.Dispose();
                }

                if (!CzyWynikGenerowaniaJpkPoprawny(wynikNaliczania, out string bladWyniku))
                {
                    blad = $"{bladWyniku}. Parametry: {OpiszKrytyczneParametryJpk(mgr, plik ?? mgr.Plik)}";
                    return false;
                }

                wygenerowanyJpk = ZnajdzJpkV7M(dataOd, dataDo) ?? ZnajdzJpkPoId(wynikNaliczania?.Id ?? 0);

                if (wygenerowanyJpk == null)
                {
                    blad = $"Sfera zakończyła naliczanie JPK_V7M przez menedżer V7M, ale po operacji nie znaleziono pliku JPK w bazie. Wynik: {OpiszWynikGenerowaniaJpk(wynikNaliczania)}";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                blad = $"Sfera odrzuciła naliczenie JPK_V7M przez menedżer V7M. Powód: {ex.InnerException?.Message ?? ex.Message}. Szczegóły: {PobierzBledyWalidacji(mgr)}";
                return false;
            }
            finally
            {
                ZwolnijJesliMozna(mgr);
            }
        }

        private bool WygenerujPrzezBezposredniJpkV7M(DateTime dataOd, DateTime dataDo, out dynamic wygenerowanyJpk, out string blad)
        {
            wygenerowanyJpk = null;
            blad = null;

            dynamic mgrParametrow = null;
            dynamic mgrJpk = null;
            dynamic jpkBO = null;

            try
            {
                mgrParametrow = PobierzMenedzera("IMenadzerNaliczaniaWysylkiV7M");
                mgrJpk = PobierzMenedzera("IJednolitePlikiKontrolne");
                if (mgrParametrow == null || mgrJpk == null)
                {
                    blad = "Brak menedżera parametrów V7M lub menedżera IJednolitePlikiKontrolne.";
                    return false;
                }

                mgrParametrow.Inicjalizuj();
                PrzygotujManagerWysylkiV7M(mgrParametrow, dataOd);

                dynamic plik = null;
                try { plik = mgrParametrow.DodajDomyslnyPlik(); } catch { }
                dynamic parametry = plik ?? mgrParametrow.Plik;
                PrzygotujParametryJpkV7(parametry, dataOd, dataDo);
                UzupelnijParametryZManagera(mgrParametrow, parametry);
                LogujBrakiParametrowJpk("IJednolityPlikKontrolny.Generuj", mgrParametrow, parametry);

                jpkBO = mgrJpk.Utworz();
                jpkBO.InicjalizujTrybGenerowania(parametry);

                WynikGenerowaniaPlikuJPK wynik = jpkBO.Generuj();
                _logger.LogInformation("[VAT JPK WYNIK] Ścieżka=IJednolityPlikKontrolny.Generuj; {Wynik}", OpiszWynikGenerowaniaJpk(wynik));

                if (!CzyWynikGenerowaniaJpkPoprawny(wynik, out string bladWyniku))
                {
                    blad = $"{bladWyniku}. Parametry: {OpiszKrytyczneParametryJpk(mgrParametrow, parametry)}";
                    return false;
                }

                bool zapisano = false;
                try { zapisano = jpkBO.Zapisz(); } catch { }
                if (!zapisano)
                {
                    blad = $"JPK_V7M został wygenerowany bezpośrednio, ale nie udało się go zapisać jako widocznego pliku. Wynik: {OpiszWynikGenerowaniaJpk(wynik)}";
                    return false;
                }

                try { wygenerowanyJpk = jpkBO.Dane; } catch { }
                wygenerowanyJpk = wygenerowanyJpk ?? ZnajdzJpkPoId(wynik?.Id ?? 0) ?? ZnajdzJpkV7M(dataOd, dataDo);

                if (wygenerowanyJpk == null)
                {
                    blad = $"JPK_V7M został wygenerowany i zapisany bezpośrednio, ale nie udało się odczytać encji JPK po zapisie. Wynik: {OpiszWynikGenerowaniaJpk(wynik)}";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                blad = $"Bezpośrednie generowanie JPK_V7M przez IJednolityPlikKontrolny nie powiodło się. Powód: {ex.InnerException?.Message ?? ex.Message}. Szczegóły parametrów: {OpiszKrytyczneParametryJpk(mgrParametrow, null)}";
                return false;
            }
            finally
            {
                ZwolnijJesliMozna(jpkBO);
                ZwolnijJesliMozna(mgrParametrow);
                ZwolnijJesliMozna(mgrJpk);
            }
        }

        private void PrzygotujManagerWysylkiV7M(dynamic mgr, DateTime dataOd)
        {
            UstawJesliMozna(mgr, "MiesiacNaliczenia", dataOd);
            UstawDateSystemowaJesliMozna(mgr, "DataWystawienia", DateTime.Today);
            UstawJesliMozna(mgr, "Korekta", false);
            UstawJesliMozna(mgr, "EwidencjaVAT", true);
            UstawJesliMozna(mgr, "Nazwa", $"JPK_V7M {dataOd:yyyy-MM}");
            UstawJesliMozna(mgr, "AdresEmail", PobierzAdresEmailJpk(mgr));
            UstawJesliMozna(mgr, "TrybNaliczaniaKorektyCzesciDeklaracyjnej", TrybNaliczaniaKorektyCzesciDeklaracyjnejWysylkiV7.Auto);
            UstawJesliMozna(mgr, "TrybNaliczaniaKorektyCzesciEwidencyjnej", TrybNaliczaniaKorektyCzesciEwidencyjnejWysylkiV7.Auto);
        }

        private void PrzygotujParametryJpkV7(dynamic plik, DateTime dataOd, DateTime dataDo)
        {
            if (plik == null) return;

            UstawJesliMozna(plik, "DataOd", dataOd);
            UstawJesliMozna(plik, "DataDo", dataDo);
            UstawJesliMozna(plik, "DataWystawienia", DateTime.Today);
            UstawJesliMozna(plik, "AdresEmail", DomyslnyAdresEmailJpk);
            UstawJesliMozna(plik, "Korekta", false);
            UstawJesliMozna(plik, "Sprzedaz", true);
            UstawJesliMozna(plik, "Zakup", true);
            UstawJesliMozna(plik, "Importowany", false);
            UstawJesliMozna(plik, "NaZadanie", false);
            UstawJesliMozna(plik, "SymbolWaluty", "PLN");
            UstawJesliMozna(plik, "UzyjWlasnychWyrazen", false);
            UstawJesliMozna(plik, "UwzglednijEwidencjeZrodlowa", true);
            UstawJesliMozna(plik, "UwzglednijFakturyWyslaneDoKsef", true);
            UstawJesliMozna(plik, "SposobKwalifikowaniaZapisowVAT", SposobKwalifikowaniaZapisowVAT.WgMiesiacaNaliczenia);
            UstawJesliMozna(plik, "DataDokumentuSprzedazy", DataKwalifikowaniaDokumentuSprzedazy.DataObowiazkuPodatkowego);
            UstawJesliMozna(plik, "DataDokumentuZakupu", DataKwalifikowaniaDokumentuZakupu.DataUzyskaniaPrawaDoOdliczenia);
            UstawJesliMozna(plik, "TrybNaliczaniaKorektyCzesciDeklaracyjnej", TrybNaliczaniaKorektyCzesciDeklaracyjnejWysylkiV7.Auto);
            UstawJesliMozna(plik, "TrybNaliczaniaKorektyCzesciEwidencyjnej", TrybNaliczaniaKorektyCzesciEwidencyjnejWysylkiV7.Auto);
        }

        private void UzupelnijParametryZManagera(dynamic manager, dynamic plik)
        {
            if (manager == null || plik == null) return;

            if (TryReadStringProperty(manager, "KodUrzeduSkarbowego", out string kodUrzedu))
            {
                UstawJesliMozna(plik, "KodUrzedu", kodUrzedu);
            }

            UstawJesliMozna(plik, "AdresEmail", PobierzAdresEmailJpk(manager));
        }

        private string PobierzAdresEmailJpk(object source)
        {
            return TryReadStringProperty(source, "AdresEmail", out string adresEmail)
                ? adresEmail
                : DomyslnyAdresEmailJpk;
        }

        private void LogujBrakiParametrowJpk(string sciezka, object manager, object plik)
        {
            var braki = ZnajdzBrakiParametrowJpk(manager, plik);
            if (braki.Count == 0)
            {
                _logger.LogInformation("[VAT JPK PARAMETRY] Ścieżka={Sciezka}; {Parametry}", sciezka, OpiszKrytyczneParametryJpk(manager, plik));
                return;
            }

            _logger.LogWarning("[VAT JPK PARAMETRY BRAKI] Ścieżka={Sciezka}; braki={Braki}; {Parametry}",
                sciezka,
                string.Join(", ", braki),
                OpiszKrytyczneParametryJpk(manager, plik));
        }

        private List<string> ZnajdzBrakiParametrowJpk(object manager, object plik)
        {
            var braki = new List<string>();

            string kodUrzedu = PobierzPierwszyTekst(manager, "KodUrzeduSkarbowego")
                ?? PobierzPierwszyTekst(plik, "KodUrzedu");
            if (string.IsNullOrWhiteSpace(kodUrzedu))
            {
                braki.Add("KodUrzedu/KodUrzeduSkarbowego");
            }

            string email = PobierzPierwszyTekst(plik, "AdresEmail")
                ?? PobierzPierwszyTekst(manager, "AdresEmail");
            if (string.IsNullOrWhiteSpace(email))
            {
                braki.Add("AdresEmail");
            }

            if (!CzyDataUstawiona(plik, "DataOd"))
            {
                braki.Add("DataOd");
            }

            if (!CzyDataUstawiona(plik, "DataDo"))
            {
                braki.Add("DataDo");
            }

            bool sprzedaz = TryReadBoolProperty(plik, "Sprzedaz", out bool sprzedazValue) && sprzedazValue;
            bool zakup = TryReadBoolProperty(plik, "Zakup", out bool zakupValue) && zakupValue;
            if (!sprzedaz && !zakup)
            {
                braki.Add("Sprzedaz/Zakup");
            }

            return braki;
        }

        private string OpiszKrytyczneParametryJpk(object manager, object plik)
        {
            object realnyPlik = plik ?? PobierzWartoscWlasciwosci(manager, "Plik");

            string managerOpis = OpiszWybraneWlasciwosci(manager,
                "MiesiacNaliczenia",
                "DataWystawienia",
                "KodUrzeduSkarbowego",
                "AdresEmail",
                "Korekta",
                "EwidencjaVAT",
                "DaneKompletne",
                "Nazwa");

            string plikOpis = OpiszWybraneWlasciwosci(realnyPlik,
                "Rodzaj",
                "Wersja",
                "DataOd",
                "DataDo",
                "DataWystawienia",
                "KodUrzedu",
                "AdresEmail",
                "Korekta",
                "NumerKorekty",
                "Sprzedaz",
                "Zakup",
                "NaZadanie",
                "SymbolWaluty",
                "SposobKwalifikowaniaZapisowVAT",
                "DataDokumentuSprzedazy",
                "DataDokumentuZakupu",
                "TrybNaliczaniaKorektyCzesciDeklaracyjnej",
                "TrybNaliczaniaKorektyCzesciEwidencyjnej");

            return $"Manager[{managerOpis}], Plik[{plikOpis}]";
        }

        private string OpiszWybraneWlasciwosci(object source, params string[] propertyNames)
        {
            if (source == null) return "brak";

            var parts = new List<string>();
            foreach (string propertyName in propertyNames)
            {
                object value = PobierzWartoscWlasciwosci(source, propertyName);
                parts.Add($"{propertyName}={FormatujWartoscDiagnostyczna(value)}");
            }

            return string.Join(", ", parts);
        }

        private object PobierzWartoscWlasciwosci(object source, string propertyName)
        {
            if (source == null) return null;

            try
            {
                var property = source.GetType().GetProperty(propertyName);
                if (property == null || !property.CanRead) return null;
                return property.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private string PobierzPierwszyTekst(object source, string propertyName)
        {
            object value = PobierzWartoscWlasciwosci(source, propertyName);
            string text = value?.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private bool CzyDataUstawiona(object source, string propertyName)
        {
            object value = PobierzWartoscWlasciwosci(source, propertyName);
            return value is DateTime date && date != default;
        }

        private bool TryReadDateProperty(object source, string propertyName, out DateTime value)
        {
            value = default;
            object raw = PobierzWartoscWlasciwosci(source, propertyName);
            if (raw == null) return false;

            if (raw is DateTime date)
            {
                value = date;
                return true;
            }

            return DateTime.TryParse(raw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
                || DateTime.TryParse(raw.ToString(), CultureInfo.GetCultureInfo("pl-PL"), DateTimeStyles.None, out value);
        }

        private bool TryReadIntProperty(object source, string propertyName, out int value)
        {
            value = 0;
            object raw = PobierzWartoscWlasciwosci(source, propertyName);
            if (raw == null) return false;

            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }
        }

        private bool TryReadBoolProperty(object source, string propertyName, out bool value)
        {
            value = false;
            object raw = PobierzWartoscWlasciwosci(source, propertyName);
            if (raw == null) return false;

            try
            {
                value = Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private dynamic ZnajdzJpkV7M(DateTime dataOd, DateTime dataDo)
        {
            dynamic mgrJpk = PobierzMenedzera("IJednolitePlikiKontrolne");
            if (mgrJpk == null) return null;

            try
            {
                var znalezione = ((IEnumerable)mgrJpk.Dane.ZnajdzWysylkeVATRozliczeniowaWOkresie(dataOd, dataDo))
                    .Cast<dynamic>()
                    .Where(CzyJpkV7M)
                    .OrderByDescending(j => { try { return (int)j.Id; } catch { return 0; } })
                    .ToList();

                return znalezione.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nie udało się sprawdzić istniejących plików JPK_V7M dla okresu {Od:yyyy-MM-dd} - {Do:yyyy-MM-dd}.", dataOd, dataDo);
                return null;
            }
        }

        private dynamic ZnajdzJpkPoId(int id)
        {
            if (id <= 0) return null;

            dynamic mgrJpk = PobierzMenedzera("IJednolitePlikiKontrolne");
            if (mgrJpk == null) return null;

            try
            {
                object znaleziony = mgrJpk.Dane.Znajdz(id);
                if (znaleziony != null)
                {
                    _logger.LogInformation("[VAT JPK] Odnaleziono wygenerowany JPK po Id wyniku naliczania: {Jpk}", OpiszJpk(znaleziony));
                    return znaleziony;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VAT JPK] Nie udało się odszukać JPK po Id wyniku naliczania: {Id}.", id);
            }

            return null;
        }

        private bool CzyJpkV7M(dynamic jpk)
        {
            try
            {
                int rodzaj = Convert.ToInt32(jpk.Rodzaj);
                return rodzaj == Convert.ToInt32(RodzajJPK.V7M);
            }
            catch
            {
                return false;
            }
        }

        private IDisposable PodepnijNaliczonoEventHandler(object manager, Action<WynikGenerowaniaPlikuJPK> ustawWynik, string sciezka)
        {
            if (manager == null) return null;

            NaliczonoEventHandler handler = (sender, e) =>
            {
                var wynik = e?.Wynik;
                ustawWynik(wynik);
                _logger.LogInformation("[VAT JPK WYNIK] Ścieżka={Sciezka}; {Wynik}", sciezka, OpiszWynikGenerowaniaJpk(wynik));
            };

            if (manager is IMenadzerNaliczaniaPlikowJPK managerPlikow)
            {
                managerPlikow.NaliczonoEventHandler += handler;
                return new DisposableAction(() => managerPlikow.NaliczonoEventHandler -= handler);
            }

            if (manager is IMenadzerNaliczaniaWysylkiV managerWysylki)
            {
                managerWysylki.NaliczonoEventHandler += handler;
                return new DisposableAction(() => managerWysylki.NaliczonoEventHandler -= handler);
            }

            _logger.LogDebug("[VAT JPK WYNIK] Nie udało się podpiąć NaliczonoEventHandler dla typu {Typ}.", manager.GetType().FullName);
            return null;
        }

        private bool CzyWynikGenerowaniaJpkPoprawny(WynikGenerowaniaPlikuJPK wynik, out string blad)
        {
            blad = null;
            if (wynik == null) return true;

            string status = wynik.Status.ToString();
            if (string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Ostrzezenie", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            blad = $"Sfera zwróciła niepoprawny wynik generowania JPK_V7M: {OpiszWynikGenerowaniaJpk(wynik)}";
            return false;
        }

        private string OpiszWynikGenerowaniaJpk(WynikGenerowaniaPlikuJPK wynik)
        {
            if (wynik == null) return "brak wyniku";

            string szczegolyLogu = PobierzFragmentLoguJpk(wynik.LogSciezka);
            string log = string.IsNullOrWhiteSpace(szczegolyLogu)
                ? $"Log={wynik.LogSciezka ?? "brak"}"
                : $"Log={wynik.LogSciezka ?? "brak"}; LogSzczegoly={szczegolyLogu}";

            return $"Status={wynik.Status}; Id={wynik.Id}; Opis={wynik.Opis ?? "brak"}; XML={wynik.XmlSciezka ?? "brak"}; {log}; Sprzedaz={wynik.Sprzedaz}; Zakup={wynik.Zakup}";
        }

        private string PobierzFragmentLoguJpk(string sciezkaLogu)
        {
            if (string.IsNullOrWhiteSpace(sciezkaLogu)) return null;

            try
            {
                if (!File.Exists(sciezkaLogu)) return null;

                string fragment = string.Join(" | ",
                    File.ReadLines(sciezkaLogu)
                        .Where(linia => !string.IsNullOrWhiteSpace(linia))
                        .Take(12));

                if (fragment.Length > 1200)
                {
                    fragment = fragment.Substring(0, 1200) + "...";
                }

                return fragment;
            }
            catch
            {
                return null;
            }
        }

        private void UstawJesliMozna(dynamic target, string propertyName, object value)
        {
            if (target == null) return;

            try
            {
                var property = target.GetType().GetProperty(propertyName);
                if (property == null || !property.CanWrite) return;

                object converted = value;
                var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (value != null && !targetType.IsAssignableFrom(value.GetType()))
                {
                    converted = targetType.IsEnum
                        ? Enum.ToObject(targetType, Convert.ToInt32(value))
                        : Convert.ChangeType(value, targetType);
                }

                property.SetValue(target, converted);
            }
            catch (Exception ex)
            {
                string targetTypeName = "brak";
                try { targetTypeName = target?.GetType().FullName ?? "brak"; } catch { }
                _logger.LogDebug("Nie udało się ustawić właściwości {Property} na {Type}: {Message}", propertyName, targetTypeName, ex.Message);
            }
        }

        private void UstawDateSystemowaJesliMozna(dynamic target, string propertyName, DateTime data)
        {
            if (target == null) return;

            try
            {
                var property = target.GetType().GetProperty(propertyName);
                if (property == null || !property.CanWrite) return;

                var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (targetType.FullName == "InsERT.Moria.IDataSystemowa")
                {
                    dynamic dataSystemowa = PobierzMenedzera("IDataSystemowa");
                    if (dataSystemowa == null) return;

                    dataSystemowa.UstawKontekstDaty(data);
                    property.SetValue(target, dataSystemowa);
                    return;
                }

                UstawJesliMozna(target, propertyName, data);
            }
            catch (Exception ex)
            {
                string targetTypeName = "brak";
                try { targetTypeName = target?.GetType().FullName ?? "brak"; } catch { }
                _logger.LogDebug("Nie udało się ustawić daty systemowej {Property} na {Type}: {Message}", propertyName, targetTypeName, ex.Message);
            }
        }

        private bool CzyDaneNiekompletne(dynamic manager)
        {
            try { return manager.DaneKompletne == false; }
            catch { return false; }
        }

        private string PobierzBledyWalidacji(object bo)
        {
            var wyniki = new List<string>();
            dynamic boDyn = bo;

            foreach (string propertyName in new[] { "InvalidData", "ErrorInfo", "DataErrorInfo", "Bledy", "Błędy", "Ostrzezenia", "Komunikaty" })
            {
                try
                {
                    var property = bo.GetType().GetProperty(propertyName);
                    if (property == null) continue;

                    object raw = property.GetValue(bo);
                    foreach (string opis in OpiszWartoscWalidacji(raw))
                    {
                        wyniki.Add($"{propertyName}: {opis}");
                    }
                }
                catch { }
            }

            return wyniki.Count > 0
                ? string.Join(" | ", wyniki.Distinct().Take(20))
                : "Brak szczegółów walidacji";
        }

        private IEnumerable<string> OpiszWartoscWalidacji(object raw)
        {
            if (raw == null) yield break;

            if (raw is string text)
            {
                if (!string.IsNullOrWhiteSpace(text)) yield return text;
                yield break;
            }

            if (raw is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    string opis = OpiszElementWalidacji(item);
                    if (!string.IsNullOrWhiteSpace(opis)) yield return opis;
                }
                yield break;
            }

            string single = OpiszElementWalidacji(raw);
            if (!string.IsNullOrWhiteSpace(single)) yield return single;
        }

        private string OpiszElementWalidacji(object item)
        {
            if (item == null) return null;

            foreach (string propertyName in new[] { "Komunikat", "Tresc", "Treść", "Opis", "Message", "ErrorMessage", "WlasnyKomunikatBledu", "TrescBledu" })
            {
                if (TryReadStringProperty(item, propertyName, out string value))
                {
                    return value;
                }
            }

            string opis = item.ToString();
            return string.IsNullOrWhiteSpace(opis) ? null : opis;
        }

        private bool TryReadStringProperty(object source, string propertyName, out string value)
        {
            value = null;
            if (source == null) return false;

            try
            {
                var property = source.GetType().GetProperty(propertyName);
                if (property == null || !property.CanRead) return false;

                object rawValue = property.GetValue(source);
                string text = rawValue?.ToString();
                if (string.IsNullOrWhiteSpace(text)) return false;

                value = text.Trim();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void LogujDiagnostykeJpk(string sciezka, object manager, object plik)
        {
            _logger.LogWarning("[VAT JPK DIAG] Ścieżka={Sciezka}; Manager={Manager}; Plik={Plik}",
                sciezka,
                OpiszObiektDiagnostycznie(manager, 25),
                OpiszObiektDiagnostycznie(plik, 35));
        }

        private string OpiszObiektDiagnostycznie(object obj, int maxProperties)
        {
            if (obj == null) return "brak";

            try
            {
                var props = obj.GetType()
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.CanRead && CzyProstyTyp(p.PropertyType))
                    .OrderBy(p => p.Name)
                    .Take(maxProperties)
                    .Select(p =>
                    {
                        try
                        {
                            object value = p.GetValue(obj);
                            return $"{p.Name}={FormatujWartoscDiagnostyczna(value)}";
                        }
                        catch (Exception ex)
                        {
                            return $"{p.Name}=<błąd odczytu: {ex.Message}>";
                        }
                    })
                    .ToList();

                return $"{obj.GetType().FullName}: {string.Join(", ", props)}";
            }
            catch (Exception ex)
            {
                return $"{obj.GetType().FullName}: błąd diagnostyki {ex.Message}";
            }
        }

        private bool CzyProstyTyp(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(Guid);
        }

        private string FormatujWartoscDiagnostyczna(object value)
        {
            if (value == null) return "brak";
            if (value is DateTime date) return date.ToString("yyyy-MM-dd");
            return value.ToString();
        }

        private string OpiszJpk(object jpk)
        {
            if (jpk == null) return "brak";

            dynamic jpkDyn = jpk;
            try
            {
                return $"Id={jpkDyn.Id}; Rodzaj={jpkDyn.Rodzaj}; DataOd={((DateTime)jpkDyn.DataOd):yyyy-MM-dd}; DataDo={((DateTime)jpkDyn.DataDo):yyyy-MM-dd}; Korekta={jpkDyn.Korekta}";
            }
            catch
            {
                try { return $"Id={jpkDyn.Id}"; } catch { return jpk.ToString(); }
            }
        }

        private void ZwolnijJesliMozna(dynamic obj)
        {
            try
            {
                if (obj is IDisposable disposable) disposable.Dispose();
            }
            catch { }
        }

        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            ZaladujZnaneAssemblySfery();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
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

        private void ZaladujZnaneAssemblySfery()
        {
            string[] nazwyAssembly =
            {
                "InsERT.Moria.Deklaracje",
                "InsERT.Moria.EwidencjaVAT",
                "InsERT.Moria.KontrolaSkarbowa",
                "InsERT.Moria.Klienci"
            };

            foreach (string nazwaAssembly in nazwyAssembly)
            {
                try { System.Reflection.Assembly.Load(nazwaAssembly); }
                catch { }
            }
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

        private sealed class DisposableAction : IDisposable
        {
            private readonly Action _dispose;
            private bool _disposed;

            public DisposableAction(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                if (_disposed) return;

                _disposed = true;
                _dispose?.Invoke();
            }
        }
    }
}
