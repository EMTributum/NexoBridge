using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class VatUeCalculationService
    {
        private const string TypDeklaracjiVatUe = "VAT-UE";
        private const int MetodaMiesieczna = 101;
        private const int MetodaKwartalna = 102;

        private readonly Uchwyt _sfera;
        private readonly ILogger<VatUeCalculationService> _logger;

        public VatUeCalculationService(Uchwyt sfera, ILogger<VatUeCalculationService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public Task<VatUeReport> WygenerujVatUeAsync(DateTime dataRozliczenia)
        {
            var raport = new VatUeReport
            {
                IsVatUePayer = false,
                DeclarationType = TypDeklaracjiVatUe
            };

            DateTime dataOd = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, 1);
            DateTime dataDo = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, DateTime.DaysInMonth(dataRozliczenia.Year, dataRozliczenia.Month));

            try
            {
                _logger.LogInformation("Rozpoczynam weryfikację i generowanie VAT-UE dla: {Data:yyyy-MM}", dataRozliczenia);
                ZaladujZnaneAssemblySfery();

                dynamic mgrOkresyVat = PobierzMenedzera("IOkresyRozliczenVAT");
                dynamic mgrPodmioty = PobierzMenedzera("IPodmioty");
                dynamic mgrWersji = PobierzMenedzera("IWersjeDeklaracji");
                dynamic mgrDeklaracji = PobierzMenedzeraDeklaracji();

                if (mgrOkresyVat == null || mgrPodmioty == null || mgrWersji == null || mgrDeklaracji == null)
                {
                    raport.ErrorMsg = $"Brak menedżerów Sfery dla VAT-UE. IOkresyRozliczenVAT={OpiszTyp((object)mgrOkresyVat)}, IPodmioty={OpiszTyp((object)mgrPodmioty)}, IWersjeDeklaracji={OpiszTyp((object)mgrWersji)}, IDeklaracje={OpiszTyp((object)mgrDeklaracji)}.";
                    _logger.LogWarning("[VAT-UE ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return Task.FromResult(raport);
                }

                object okresVatUe = ZnajdzOkresVatUe(mgrOkresyVat, dataDo);
                if (okresVatUe == null)
                {
                    _logger.LogInformation("[VAT-UE POMINIĘTO] Firma nie posiada konfiguracji VAT-UE aktywnej dla okresu {Okres:yyyy-MM}.", dataRozliczenia);
                    return Task.FromResult(raport);
                }

                raport.IsVatUePayer = true;
                int metoda = PobierzInt(okresVatUe, "Metoda") ?? 0;
                raport.SettlementMethod = OpiszMetodeVatUe(metoda);

                if (metoda == MetodaKwartalna)
                {
                    if (dataRozliczenia.Month % 3 != 0)
                    {
                        raport.Warning = $"Firma rozlicza VAT-UE kwartalnie. Pomijam miesiąc {dataRozliczenia:MM/yyyy}; deklaracja powinna być generowana na koniec kwartału.";
                        _logger.LogInformation("[VAT-UE POMINIĘTO] {Warning}", raport.Warning);
                        return Task.FromResult(raport);
                    }

                    dataOd = dataOd.AddMonths(-2);
                }
                else if (metoda != MetodaMiesieczna)
                {
                    raport.ErrorMsg = $"Firma używa nieobsługiwanej metody rozliczeń VAT-UE (Kod: {metoda}).";
                    _logger.LogWarning("[VAT-UE ODRZUCONO] {Msg}; okres={Okres}", raport.ErrorMsg, OpiszWybraneWlasciwosci(okresVatUe, "Id", "Poczatek", "Rodzaj", "Metoda"));
                    return Task.FromResult(raport);
                }

                object mojaFirma = PobierzMojaFirme(mgrPodmioty);
                if (mojaFirma == null)
                {
                    raport.ErrorMsg = "Nie udało się pobrać podmiotu MojaFirma dla deklaracji VAT-UE.";
                    _logger.LogWarning("[VAT-UE ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return Task.FromResult(raport);
                }

                object wersja = ZnajdzWersjeVatUe(mgrWersji, dataDo);
                if (wersja == null)
                {
                    raport.ErrorMsg = $"Nie znaleziono wersji deklaracji {TypDeklaracjiVatUe} obowiązującej dla {dataDo:yyyy-MM-dd}.";
                    _logger.LogWarning("[VAT-UE ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return Task.FromResult(raport);
                }

                Guid wersjaId = PobierzGuid(wersja, "Id") ?? Guid.Empty;
                raport.DeclarationVersion = PobierzString(wersja, "Nazwa") ?? PobierzString(wersja, "Tytul") ?? wersjaId.ToString();
                if (wersjaId == Guid.Empty)
                {
                    raport.ErrorMsg = $"Wersja deklaracji VAT-UE nie ma poprawnego Id. Wersja={OpiszWybraneWlasciwosci(wersja, "Id", "Nazwa", "Typ")}.";
                    _logger.LogWarning("[VAT-UE ODRZUCONO] {Msg}", raport.ErrorMsg);
                    return Task.FromResult(raport);
                }

                List<object> istniejace = PobierzDeklaracjeVatUeWOkresie(mgrDeklaracji, mojaFirma, dataOd, dataDo, "precheck");
                if (istniejace.Count > 0)
                {
                    object deklaracja = istniejace.First();
                    raport.AlreadyExists = true;
                    raport.IsGenerated = true;
                    raport.DeclarationId = PobierzString(deklaracja, "Id");
                    _logger.LogWarning("[VAT-UE DUPLIKAT] Deklaracja VAT-UE za okres {Od:yyyy-MM-dd}-{Do:yyyy-MM-dd} już istnieje. Pomijam generowanie. Deklaracja={Deklaracja}",
                        dataOd,
                        dataDo,
                        OpiszDeklaracje(deklaracja));
                    return Task.FromResult(raport);
                }

                dynamic deklBO = UtworzDeklaracjeVatUe(mgrDeklaracji, dataOd, dataDo, raport.DeclarationVersion, wersjaId);
                _logger.LogDebug("[VAT-UE WYLICZ START] okres={Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}; wersja={Wersja}; wersjaId={WersjaId}; podmiot={Podmiot}",
                    dataOd,
                    dataDo,
                    raport.DeclarationVersion,
                    wersjaId,
                    OpiszWybraneWlasciwosci(mojaFirma, "Id", "NazwaSkrocona", "NIP"));

                deklBO.Wylicz((dynamic)mojaFirma, wersjaId, dataOd, dataDo, null);

                bool zapisano = deklBO.Zapisz();
                if (!zapisano)
                {
                    string invalidData = WyciagnijBledySfery(deklBO);
                    List<object> poOdrzuceniu = PobierzDeklaracjeVatUeWOkresie(PobierzMenedzeraDeklaracji(), mojaFirma, dataOd, dataDo, "after-save-false");
                    if (poOdrzuceniu.Count > 0)
                    {
                        object deklaracja = poOdrzuceniu.First();
                        raport.AlreadyExists = true;
                        raport.IsGenerated = true;
                        raport.DeclarationId = PobierzString(deklaracja, "Id");
                        raport.Warning = "Sfera odrzuciła zapis jako duplikat, ale świeży odczyt potwierdził istniejącą deklarację VAT-UE.";
                        _logger.LogWarning("[VAT-UE ZAPIS ODRZUCONY - DUPLIKAT POTWIERDZONY] okres={Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}; wersja={Wersja}; invalidData={InvalidData}; deklaracja={Deklaracja}",
                            dataOd,
                            dataDo,
                            raport.DeclarationVersion,
                            invalidData,
                            OpiszDeklaracje(deklaracja));
                        return Task.FromResult(raport);
                    }

                    raport.ErrorMsg = $"Sfera wyliczyła VAT-UE, ale odrzuciła zapis. Szczegóły: {invalidData}.";
                    _logger.LogWarning("[VAT-UE ODRZUCONO] {Msg}; okres={Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}; wersja={Wersja}; manager={Manager}",
                        raport.ErrorMsg,
                        dataOd,
                        dataDo,
                        raport.DeclarationVersion,
                        OpiszTyp((object)mgrDeklaracji));
                    return Task.FromResult(raport);
                }

                object daneDeklaracji = PobierzWlasciwosc((object)deklBO, "Dane");
                raport.DeclarationId = PobierzString(daneDeklaracji, "Id");
                raport.IsGenerated = true;

                _logger.LogInformation("[VAT-UE SUKCES] Wygenerowano i zapisano VAT-UE za okres {Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}. Wersja={Wersja}; deklaracja={Deklaracja}",
                    dataOd,
                    dataDo,
                    raport.DeclarationVersion,
                    OpiszDeklaracje(daneDeklaracji));
            }
            catch (Exception ex)
            {
                raport.ErrorMsg = ex.GetBaseException().Message;
                _logger.LogError(ex, "[VAT-UE BŁĄD KRYTYCZNY] Nie udało się wygenerować VAT-UE za okres {Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}. baseException={BaseException}",
                    dataOd,
                    dataDo,
                    OpiszWyjatekBazowy(ex));
            }

            return Task.FromResult(raport);
        }

        private object ZnajdzOkresVatUe(dynamic mgrOkresyVat, DateTime dataDo)
        {
            object rodzajVatUe = UtworzWartoscEnum("RodzajRozliczenVAT", "VAT_UE");
            if (rodzajVatUe == null)
            {
                _logger.LogWarning("[VAT-UE OKRES] Nie udało się odnaleźć enumu RodzajRozliczenVAT.VAT_UE.");
                return null;
            }

            try
            {
                object okres = WywolajNajlepszaMetode((object)mgrOkresyVat.Dane, "ZnajdzObowiazujacyWDniu", rodzajVatUe, dataDo);
                if (okres != null)
                {
                    _logger.LogInformation("[VAT-UE OKRES] Odnaleziono aktywny okres VAT-UE dla {Data:yyyy-MM-dd}: {Okres}",
                        dataDo,
                        OpiszWybraneWlasciwosci(okres, "Id", "Poczatek", "Rodzaj", "Metoda"));
                }

                return okres;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VAT-UE OKRES] Nie udało się odczytać okresu VAT-UE przez ZnajdzObowiazujacyWDniu. baseException={BaseException}",
                    OpiszWyjatekBazowy(ex));
                return null;
            }
        }

        private object PobierzMojaFirme(dynamic mgrPodmioty)
        {
            try
            {
                dynamic mojaFirmaBO = mgrPodmioty.ZnajdzMojaFirme();
                return mojaFirmaBO?.Dane;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VAT-UE PODMIOT] Nie udało się pobrać MojaFirma przez IPodmioty.ZnajdzMojaFirme(). baseException={BaseException}",
                    OpiszWyjatekBazowy(ex));
                return null;
            }
        }

        private object ZnajdzWersjeVatUe(dynamic mgrWersji, DateTime dataDo)
        {
            var kandydaci = new List<object>();

            try
            {
                object wynik = WywolajNajlepszaMetode((object)mgrWersji, "Znajdz", TypDeklaracjiVatUe, dataDo);
                kandydaci.AddRange(Enumeruj(wynik));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VAT-UE WERSJA] Nie udało się użyć IWersjeDeklaracji.Znajdz(VAT-UE, data). baseException={BaseException}",
                    OpiszWyjatekBazowy(ex));
            }

            if (kandydaci.Count == 0)
            {
                try
                {
                    object wynik = WywolajNajlepszaMetode((object)mgrWersji, "ZnajdzDlaTypu", TypDeklaracjiVatUe);
                    kandydaci.AddRange(Enumeruj(wynik));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[VAT-UE WERSJA] Nie udało się użyć IWersjeDeklaracji.ZnajdzDlaTypu(VAT-UE). baseException={BaseException}",
                        OpiszWyjatekBazowy(ex));
                }
            }

            object wybrana = kandydaci
                .OrderByDescending(w => PobierzNumerWersji(PobierzString(w, "Nazwa")))
                .FirstOrDefault();

            _logger.LogInformation("[VAT-UE WERSJA] Kandydaci={Count}; wybrana={Wersja}",
                kandydaci.Count,
                wybrana == null ? "brak" : OpiszWybraneWlasciwosci(wybrana, "Id", "Nazwa", "Typ"));

            return wybrana;
        }

        private List<object> PobierzDeklaracjeVatUeWOkresie(dynamic mgrDeklaracji, object mojaFirma, DateTime dataOd, DateTime dataDo, string etap)
        {
            try
            {
                object dane = PobierzWlasciwosc((object)mgrDeklaracji, "Dane");
                if (dane == null || mojaFirma == null)
                {
                    return new List<object>();
                }

                var wynik = new List<object>();
                foreach (object rezultat in WywolajZnajdzDeklaracjeVatUe(dane, mojaFirma, dataOd, dataDo))
                {
                    wynik.Add(rezultat);
                }

                _logger.LogDebug("[VAT-UE DUPLIKAT] etap={Etap}; okres={Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}; liczba={Liczba}; deklaracje={Deklaracje}",
                    etap,
                    dataOd,
                    dataDo,
                    wynik.Count,
                    string.Join(" || ", wynik.Select(OpiszDeklaracje).Take(20)));

                return wynik;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VAT-UE DUPLIKAT] Nie udało się pobrać istniejących deklaracji VAT-UE. etap={Etap}; okres={Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}; baseException={BaseException}",
                    etap,
                    dataOd,
                    dataDo,
                    OpiszWyjatekBazowy(ex));
                return new List<object>();
            }
        }

        private IEnumerable<object> WywolajZnajdzDeklaracjeVatUe(object dane, object mojaFirma, DateTime dataOd, DateTime dataDo)
        {
            foreach (MethodInfo metoda in dane.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "ZnajdzDeklaracjeWOkresie"))
            {
                ParameterInfo[] parametry = metoda.GetParameters();
                object[] args = null;

                if (parametry.Length == 4
                    && parametry[0].ParameterType == typeof(string)
                    && parametry[1].ParameterType == typeof(DateTime)
                    && parametry[2].ParameterType == typeof(DateTime)
                    && CzyMoznaPrzekazac(parametry[3].ParameterType, mojaFirma))
                {
                    args = new[] { (object)TypDeklaracjiVatUe, dataOd, dataDo, mojaFirma };
                }
                else if (parametry.Length == 3
                    && parametry[0].ParameterType == typeof(string)
                    && parametry[1].ParameterType == typeof(DateTime)
                    && CzyMoznaPrzekazac(parametry[2].ParameterType, mojaFirma))
                {
                    args = new[] { (object)TypDeklaracjiVatUe, dataOd, mojaFirma };
                }

                if (args == null)
                {
                    continue;
                }

                object wynik = metoda.Invoke(dane, args);
                foreach (object item in Enumeruj(wynik))
                {
                    yield return item;
                }
                yield break;
            }
        }

        private dynamic UtworzDeklaracjeVatUe(dynamic mgrDeklaracji, DateTime dataOd, DateTime dataDo, string wersjaNazwa, Guid wersjaId)
        {
            try
            {
                _logger.LogDebug("[VAT-UE UTWÓRZ START] okres={Od:yyyy-MM-dd}-{Do:yyyy-MM-dd}; wersja={Wersja}; wersjaId={WersjaId}; manager={Manager}",
                    dataOd,
                    dataDo,
                    wersjaNazwa,
                    wersjaId,
                    OpiszTyp((object)mgrDeklaracji));
                return mgrDeklaracji.Utworz();
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex.GetBaseException() is ObjectDisposedException)
            {
                _logger.LogWarning(ex, "[VAT-UE RETRY] Manager deklaracji zgłosił zamknięty ObjectContext przy Utworz(). Pobieram świeży manager i ponawiam. baseException={BaseException}",
                    OpiszWyjatekBazowy(ex));
                dynamic swiezyMgr = PobierzMenedzeraDeklaracji();
                return swiezyMgr.Utworz();
            }
        }

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
                    }).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

                    if (bledy.Any()) return string.Join(" | ", bledy);
                }
            }
            catch { }

            return "Brak szczegółowych komunikatów.";
        }

        private dynamic PobierzMenedzeraDeklaracji()
        {
            return PobierzMenedzera("IDeklaracje") ?? PobierzMenedzera("IDeklaracjeSkarbowe");
        }

        private dynamic PobierzMenedzera(string nazwaInterfejsu)
        {
            Type typSzukany = ZnajdzTypInterfejsu(nazwaInterfejsu);
            if (typSzukany != null)
            {
                MethodInfo metoda = _sfera.GetType()
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "PodajObiektTypu" && m.IsGenericMethod && m.GetParameters().Length == 0);
                if (metoda != null) return metoda.MakeGenericMethod(typSzukany).Invoke(_sfera, null);
            }

            return null;
        }

        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            ZaladujZnaneAssemblySfery();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.FullName.StartsWith("System.") || assembly.FullName.StartsWith("Microsoft.")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                Type found = types.FirstOrDefault(t => t != null && t.IsInterface && t.Name == nazwa);
                if (found != null) return found;
            }

            return null;
        }

        private void ZaladujZnaneAssemblySfery()
        {
            foreach (string nazwaAssembly in new[]
            {
                "InsERT.Moria.Deklaracje",
                "InsERT.Moria.EwidencjaVAT",
                "InsERT.Moria.Klienci"
            })
            {
                try { Assembly.Load(nazwaAssembly); }
                catch { }
            }
        }

        private object UtworzWartoscEnum(string enumName, string valueName)
        {
            Type enumType = ZnajdzTyp(enumName);
            if (enumType?.IsEnum != true)
            {
                return null;
            }

            try
            {
                return Enum.Parse(enumType, valueName, ignoreCase: true);
            }
            catch
            {
                return null;
            }
        }

        private Type ZnajdzTyp(string shortOrFullName)
        {
            ZaladujZnaneAssemblySfery();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                Type found = types.FirstOrDefault(t => t != null
                    && (string.Equals(t.Name, shortOrFullName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.FullName, shortOrFullName, StringComparison.OrdinalIgnoreCase)
                        || t.FullName?.EndsWith("." + shortOrFullName, StringComparison.OrdinalIgnoreCase) == true));
                if (found != null) return found;
            }

            return null;
        }

        private object WywolajNajlepszaMetode(object target, string methodName, params object[] args)
        {
            if (target == null) return null;

            foreach (MethodInfo method in target.GetType().GetMethods().Where(m => m.Name == methodName && m.GetParameters().Length == args.Length))
            {
                ParameterInfo[] parameters = method.GetParameters();
                bool accepted = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (!CzyMoznaPrzekazac(parameters[i].ParameterType, args[i]))
                    {
                        accepted = false;
                        break;
                    }
                }

                if (accepted)
                {
                    return method.Invoke(target, args);
                }
            }

            throw new MissingMethodException(target.GetType().FullName, $"{methodName}({args.Length})");
        }

        private bool CzyMoznaPrzekazac(Type parameterType, object value)
        {
            if (value == null)
            {
                return !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;
            }

            return parameterType.IsInstanceOfType(value);
        }

        private IEnumerable<object> Enumeruj(object result)
        {
            if (result is not IEnumerable enumerable || result is string)
            {
                yield break;
            }

            foreach (object item in enumerable)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }

        private object PobierzWlasciwosc(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            try
            {
                return source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private object PobierzWartoscSciezki(object source, string path)
        {
            object current = source;
            foreach (string segment in (path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                current = PobierzWlasciwosc(current, segment);
                if (current == null) return null;
            }

            return current;
        }

        private string PobierzString(object source, string path)
        {
            object value = PobierzWartoscSciezki(source, path);
            return value?.ToString();
        }

        private int? PobierzInt(object source, string path)
        {
            object value = PobierzWartoscSciezki(source, path);
            if (value == null) return null;
            try { return Convert.ToInt32(value); }
            catch { return null; }
        }

        private Guid? PobierzGuid(object source, string path)
        {
            object value = PobierzWartoscSciezki(source, path);
            if (value is Guid guid) return guid;
            return Guid.TryParse(value?.ToString(), out Guid parsed) ? parsed : null;
        }

        private int PobierzNumerWersji(string nazwa)
        {
            if (string.IsNullOrWhiteSpace(nazwa)) return 0;
            int start = nazwa.LastIndexOf('(');
            int end = nazwa.LastIndexOf(')');
            if (start >= 0 && end > start && int.TryParse(nazwa.Substring(start + 1, end - start - 1), out int result))
            {
                return result;
            }

            return 0;
        }

        private string OpiszMetodeVatUe(int metoda)
        {
            return metoda switch
            {
                MetodaMiesieczna => "Miesięczna",
                MetodaKwartalna => "Kwartalna",
                0 => "brak",
                _ => $"Nieobsługiwana ({metoda})"
            };
        }

        private string OpiszDeklaracje(object deklaracja)
        {
            return OpiszWybraneWlasciwosci(
                deklaracja,
                "Id",
                "Nazwa",
                "Tytul",
                "WersjaDeklaracji.Nazwa",
                "Wersja.Nazwa",
                "Typ",
                "TypDeklaracji",
                "Okres.DataOd",
                "Okres.DataDo",
                "DataOd",
                "DataDo",
                "Podmiot.NazwaSkrocona",
                "Status",
                "Stan");
        }

        private string OpiszWybraneWlasciwosci(object value, params string[] paths)
        {
            if (value == null) return "brak";

            var parts = new List<string> { $"typ={OpiszTyp(value)}" };
            foreach (string path in paths)
            {
                object propertyValue = PobierzWartoscSciezki(value, path);
                if (propertyValue != null && !string.IsNullOrWhiteSpace(propertyValue.ToString()))
                {
                    parts.Add($"{path}={propertyValue}");
                }
            }

            return string.Join("; ", parts);
        }

        private string OpiszTyp(object value)
        {
            return value?.GetType().FullName ?? "brak";
        }

        private string OpiszWyjatekBazowy(Exception ex)
        {
            Exception bazowy = ex?.GetBaseException();
            return bazowy == null ? "brak" : $"{bazowy.GetType().FullName}: {bazowy.Message}";
        }
    }
}
