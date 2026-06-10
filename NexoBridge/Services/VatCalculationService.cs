using InsERT.Moria.KontrolaSkarbowa;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

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

                var okresyVat = ((IEnumerable)mgrOkresyVat.Dane.Wszystkie()).Cast<dynamic>().ToList();
                var glownyOkres = okresyVat
                    .Where(o => { try { return (int)o.Rodzaj == 1; } catch { return false; } })
                    .OrderByDescending(o => { try { return (int)o.Id; } catch { return 0; } })
                    .FirstOrDefault();

                if (glownyOkres == null)
                {
                    raport.IsVatPayer = false;
                    _logger.LogInformation("[VAT POMINIĘTO] Firma nie posiada żadnej konfiguracji ewidencji VAT krajowego.");
                    return Task.FromResult(raport);
                }

                byte metodaRozliczen = 0;
                try { metodaRozliczen = (byte)glownyOkres.Metoda; } catch { }

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

                DateTime dataOd = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, 1);
                DateTime dataDo = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, DateTime.DaysInMonth(dataRozliczenia.Year, dataRozliczenia.Month));

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

            dynamic mgrNaliczania = PobierzMenedzera("IMenadzerNaliczaniaPlikowJPK");
            if (mgrNaliczania != null)
            {
                return WygenerujPrzezMenedzerPlikowJpk(mgrNaliczania, dataOd, dataDo, out wygenerowanyJpk, out blad);
            }

            dynamic mgrWysylkiV7M = PobierzMenedzera("IMenadzerNaliczaniaWysylkiV7M");
            if (mgrWysylkiV7M != null)
            {
                return WygenerujPrzezMenedzerWysylkiV7M(mgrWysylkiV7M, dataOd, dataDo, out wygenerowanyJpk, out blad);
            }

            blad = "Nie udało się pobrać menedżera naliczania JPK_V7M (IMenadzerNaliczaniaPlikowJPK / IMenadzerNaliczaniaWysylkiV7M).";
            return false;
        }

        private bool WygenerujPrzezMenedzerPlikowJpk(dynamic mgr, DateTime dataOd, DateTime dataDo, out dynamic wygenerowanyJpk, out string blad)
        {
            wygenerowanyJpk = null;
            blad = null;

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

                if (CzyDaneNiekompletne(mgr))
                {
                    string bledyWalidacji = PobierzBledyWalidacji(mgr);
                    _logger.LogWarning("[VAT JPK] Menedżer zgłasza niekompletne dane przed wyliczeniem. Szczegóły: {Bledy}", bledyWalidacji);
                }

                mgr.Wylicz();
                wygenerowanyJpk = ZnajdzJpkV7M(dataOd, dataDo);

                if (wygenerowanyJpk == null)
                {
                    blad = "Sfera zakończyła naliczanie JPK_V7M, ale po operacji nie znaleziono pliku JPK w bazie.";
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

            try
            {
                mgr.Inicjalizuj();
                UstawJesliMozna(mgr, "MiesiacNaliczenia", dataOd);
                UstawJesliMozna(mgr, "DataWystawienia", DateTime.Today);
                UstawJesliMozna(mgr, "Korekta", false);
                UstawJesliMozna(mgr, "EwidencjaVAT", true);
                UstawJesliMozna(mgr, "Nazwa", $"JPK_V7M {dataOd:yyyy-MM}");

                dynamic plik = null;
                try { plik = mgr.DodajDomyslnyPlik(); } catch { }
                PrzygotujParametryJpkV7(plik ?? mgr.Plik, dataOd, dataDo);

                if (CzyDaneNiekompletne(mgr))
                {
                    string bledyWalidacji = PobierzBledyWalidacji(mgr);
                    _logger.LogWarning("[VAT JPK] Menedżer V7M zgłasza niekompletne dane przed wyliczeniem. Szczegóły: {Bledy}", bledyWalidacji);
                }

                mgr.Wylicz();
                wygenerowanyJpk = ZnajdzJpkV7M(dataOd, dataDo);

                if (wygenerowanyJpk == null)
                {
                    blad = "Sfera zakończyła naliczanie JPK_V7M przez menedżer V7M, ale po operacji nie znaleziono pliku JPK w bazie.";
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

        private void PrzygotujParametryJpkV7(dynamic plik, DateTime dataOd, DateTime dataDo)
        {
            if (plik == null) return;

            UstawJesliMozna(plik, "DataOd", dataOd);
            UstawJesliMozna(plik, "DataDo", dataDo);
            UstawJesliMozna(plik, "DataWystawienia", DateTime.Today);
            UstawJesliMozna(plik, "Korekta", false);
            UstawJesliMozna(plik, "Sprzedaz", true);
            UstawJesliMozna(plik, "Zakup", true);
            UstawJesliMozna(plik, "Importowany", false);
            UstawJesliMozna(plik, "UzyjWlasnychWyrazen", false);
            UstawJesliMozna(plik, "UwzglednijEwidencjeZrodlowa", true);
            UstawJesliMozna(plik, "TrybNaliczaniaKorektyCzesciDeklaracyjnej", TrybNaliczaniaKorektyCzesciDeklaracyjnejWysylkiV7.Auto);
            UstawJesliMozna(plik, "TrybNaliczaniaKorektyCzesciEwidencyjnej", TrybNaliczaniaKorektyCzesciEwidencyjnejWysylkiV7.Auto);
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

        private bool CzyDaneNiekompletne(dynamic manager)
        {
            try { return manager.DaneKompletne == false; }
            catch { return false; }
        }

        private string PobierzBledyWalidacji(object bo)
        {
            dynamic boDyn = bo;
            try
            {
                var invalid = (IEnumerable)boDyn.InvalidData;
                if (invalid != null)
                {
                    var bledy = invalid.Cast<dynamic>()
                        .Select(e =>
                        {
                            try { return (string)e.Komunikat ?? (string)e.Tresc ?? (string)e.Opis; }
                            catch { return e.ToString(); }
                        })
                        .Where(e => !string.IsNullOrWhiteSpace(e))
                        .ToList();

                    if (bledy.Count > 0) return string.Join(" | ", bledy);
                }
            }
            catch { }

            return "Brak szczegółów w InvalidData";
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
    }
}
