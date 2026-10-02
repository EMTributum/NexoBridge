using System;
using System.Collections.Concurrent;

namespace NexoBridge.Infrastructure
{
    /// <summary>
    /// Cache typów interfejsów Sfery szukanych po nazwie (ZnajdzTypInterfejsu w serwisach). Każde wyszukanie
    /// bez cache skanuje GetTypes() wszystkich załadowanych assembly (~670 DLL nexo) - a serwisy robią to
    /// przy każdym pobraniu menedżera, także w pętlach po dokumentach.
    ///
    /// Klucz zawiera nazwę serwisu, bo serwisy mają różne reguły wyboru (pomijanie System/Microsoft,
    /// preferowanie przestrzeni nazw spoza UI, dociąganie assembly przed skanem) - cache nie zmienia, który
    /// typ zostanie wybrany, tylko zapamiętuje wynik. Zapamiętywane są wyłącznie trafienia: brak może
    /// wynikać z jeszcze niezaładowanego assembly, więc kolejne wywołanie skanuje ponownie.
    /// </summary>
    public static class SferaInterfaceTypeCache
    {
        private static readonly ConcurrentDictionary<string, Type> Cache = new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        public static Type Get(string owner, string interfaceName, Func<string, Type> resolver)
        {
            if (string.IsNullOrWhiteSpace(interfaceName))
            {
                return null;
            }

            string key = owner + ":" + interfaceName;
            if (Cache.TryGetValue(key, out Type cached))
            {
                return cached;
            }

            Type found = resolver(interfaceName);
            if (found != null)
            {
                Cache[key] = found;
            }

            return found;
        }
    }
}
