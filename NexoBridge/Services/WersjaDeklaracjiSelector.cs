using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static NexoBridge.Services.SferaReflectionHelpers;

namespace NexoBridge.Services
{
    /// <summary>
    /// Wybór wersji wzorca deklaracji (JPK_V7M/V7K, zaliczki PIT) obowiązującej w danym dniu.
    ///
    /// Najpierw IWersjeDeklaracji.Znajdz(typ, data) dla typów kandydatów spełniających filtr nazw (tak
    /// jak VatUeCalculationService.ZnajdzWersjeVatUe), a gdy to nic nie zwróci - najwyższy numer "(N)"
    /// w nazwie. Zastępuje wcześniejsze OrderByDescending po Id, które przy Id typu Guid dawało losową
    /// kolejność wersji. Sprawdzone w NexoBridgeKonsola (--tax-preview) na 16 bazach: typy "V7M", "V7K",
    /// "Zaliczka miesięczna/kwartalna (liniowa/ryczałtowa) PIT".
    /// </summary>
    internal static class WersjaDeklaracjiSelector
    {
        public static object Wybierz(object mgrWersji, IEnumerable<object> wszystkieWersje, Func<string, bool> filtrNazwy, DateTime data, out string regula)
        {
            List<object> kandydaci = (wszystkieWersje ?? Enumerable.Empty<object>())
                .Where(w => filtrNazwy(ReadStringCandidate(w, "Nazwa") ?? string.Empty))
                .ToList();

            List<string> typy = kandydaci
                .Select(w => ReadStringCandidate(w, "Typ"))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            MethodInfo znajdz = mgrWersji == null ? null : ZnajdzMetode(mgrWersji.GetType(), "Znajdz", typeof(string), typeof(DateTime));
            if (znajdz != null)
            {
                foreach (string typ in typy)
                {
                    object wynik;
                    try
                    {
                        wynik = znajdz.Invoke(mgrWersji, new object[] { typ, data });
                    }
                    catch
                    {
                        continue;
                    }

                    object wybrana = Enumeruj(wynik)
                        .Where(w => filtrNazwy(ReadStringCandidate(w, "Nazwa") ?? string.Empty))
                        .OrderByDescending(w => NumerWersji(ReadStringCandidate(w, "Nazwa")))
                        .FirstOrDefault();

                    if (wybrana != null)
                    {
                        regula = $"Znajdz(\"{typ}\", {data:yyyy-MM-dd})";
                        return wybrana;
                    }
                }
            }

            regula = "najwyższy numer (N) w nazwie";
            return kandydaci
                .OrderByDescending(w => NumerWersji(ReadStringCandidate(w, "Nazwa")))
                .FirstOrDefault();
        }

        public static int NumerWersji(string nazwa)
        {
            if (string.IsNullOrWhiteSpace(nazwa)) return 0;
            int start = nazwa.LastIndexOf('(');
            int end = nazwa.LastIndexOf(')');
            return start >= 0 && end > start && int.TryParse(nazwa.Substring(start + 1, end - start - 1), out int numer) ? numer : 0;
        }

        private static IEnumerable<object> Enumeruj(object wynik)
        {
            if (wynik == null)
            {
                return Enumerable.Empty<object>();
            }

            return wynik is IEnumerable lista && wynik is not string
                ? lista.Cast<object>().Where(x => x != null)
                : new[] { wynik };
        }

        private static MethodInfo ZnajdzMetode(Type typ, string nazwa, params Type[] parametry)
        {
            return typ.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Concat(typ.GetInterfaces().SelectMany(i => i.GetMethods()))
                .FirstOrDefault(m => m.Name == nazwa
                    && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parametry));
        }
    }
}
