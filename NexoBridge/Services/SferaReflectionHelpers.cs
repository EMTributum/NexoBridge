using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using InsERT.Moria.Klienci;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using PodmiotyDane = InsERT.Moria.Klienci.IPodmiotyDane;
using PodmiotyManager = InsERT.Mox.ObiektyBiznesowe.IObiektyBiznesowe<InsERT.Moria.Klienci.IPodmiot, InsERT.Moria.ModelDanych.Podmiot, InsERT.Moria.Klienci.IPodmiotyDane>;

namespace NexoBridge.Services
{
    /// <summary>
    /// Wspólne narzędzia do defensywnego odczytu grafu obiektów Sfery przez refleksję oraz do pobierania
    /// menedżerów Sfery. Port logiki z prototypu NexoBillingKonsola/Program.cs, używany zarówno przez
    /// BillingConfigurationService (odczyt), jak i InvoiceCreationService (zapis faktury).
    /// </summary>
    internal static class SferaReflectionHelpers
    {
        public static List<Podmiot> LoadClients(PodmiotyDane podmiotyDane)
        {
            string[][] includeSets =
            {
                new[]
                {
                    "Osoba",
                    "AdresPodstawowy",
                    "Cechy",
                    "DomyslneFormyPlatnosci.FormaPlatnosci.TypPlatnosci",
                    "PolaWlasne",
                    "PolaWlasneAdv2",
                    "KlientBiura.CennikUslug.PozycjeCennikaUslug.ObiektPozycjiCennikaUslug.UslugaKsiegowa",
                    "KlientBiura.PolaWlasne"
                },
                new[]
                {
                    "Osoba",
                    "AdresPodstawowy",
                    "Cechy",
                    "DomyslneFormyPlatnosci.FormaPlatnosci",
                    "PolaWlasne",
                    "PolaWlasneAdv2",
                    "KlientBiura.CennikUslug",
                    "KlientBiura.PolaWlasne"
                },
                new[] { "Osoba", "AdresPodstawowy", "Cechy", "DomyslneFormyPlatnosci", "PolaWlasne", "PolaWlasneAdv2", "KlientBiura" },
                new[] { "Osoba" },
                Array.Empty<string>()
            };

            foreach (string[] includeSet in includeSets)
            {
                try
                {
                    return podmiotyDane.WszystkieDostepne(includeSet).ToList();
                }
                catch
                {
                }
            }

            throw new InvalidOperationException("Nie udało się pobrać listy podmiotów z IPodmiotyDane.");
        }

        private const string InvoicingFeatureName = "Do fakturowania";

        public static List<string> GetFeatureNames(object podmiot)
        {
            return ReadObjectCollection(podmiot, "Cechy")
                .Select(feature => ReadStringCandidate(feature, "Nazwa"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }

        public static bool HasFeature(object podmiot, string featureName)
        {
            return GetFeatureNames(podmiot).Any(name => NormalizeText(name) == NormalizeText(featureName));
        }

        /// <summary>
        /// Do billingu bierzemy WYŁĄCZNIE klienta aktywnego i oznaczonego cechą "Do fakturowania" -
        /// to jednocześnie jedyna reguła kwalifikacji I sposób rozwiązania duplikatów NIP potwierdzonych
        /// na produkcji (kilku klientów ma po 2-3 rekordy Podmiot pod tym samym NIP - stare/testowe
        /// rekordy zwykle nie są aktywne albo nie mają tej cechy). Brak pasującego, kwalifikującego się
        /// rekordu = klient "nie znaleziony" z punktu widzenia billingu, nawet jeśli NIP technicznie
        /// istnieje w bazie - osoba obsługująca zobaczy to i wie, że trzeba poprawić dane klienta w nexo.
        /// </summary>
        public static Podmiot FindClientByNip(IEnumerable<Podmiot> clients, string nip)
        {
            string expectedNip = NormalizeDigits(nip);
            if (string.IsNullOrWhiteSpace(expectedNip))
            {
                return null;
            }

            return clients
                .Where(client => NormalizeDigits(ReadStringCandidate(client, "NIP", "Nip")) == expectedNip)
                .Where(IsEligibleForBilling)
                .OrderBy(client => ReadIntCandidate(client, "Id") ?? int.MaxValue)
                .FirstOrDefault();
        }

        /// <summary>
        /// Wszyscy klienci kwalifikujący się do billingu (aktywny + cecha "Do fakturowania"), po jednym na
        /// NIP (przy duplikatach NIP wybiera rekord z najniższym Id - ta sama reguła co FindClientByNip).
        /// </summary>
        public static List<Podmiot> FindEligibleClients(IEnumerable<Podmiot> clients)
        {
            return clients
                .Where(IsEligibleForBilling)
                .Where(client => !string.IsNullOrWhiteSpace(ReadStringCandidate(client, "NIP", "Nip")))
                .GroupBy(client => NormalizeDigits(ReadStringCandidate(client, "NIP", "Nip")))
                .Select(group => group.OrderBy(client => ReadIntCandidate(client, "Id") ?? int.MaxValue).First())
                .ToList();
        }

        private static bool IsEligibleForBilling(Podmiot client)
        {
            return ReadBoolCandidate(client, "Aktywny") == true && HasFeature(client, InvoicingFeatureName);
        }

        public static PodmiotyManager GetPodmiotyManager(Uchwyt sfera, DateTime operationDate)
        {
            try
            {
                return (PodmiotyManager)UchwytRozszerzenia.Podmioty(sfera);
            }
            catch
            {
                return GetRequiredService<PodmiotyManager>(sfera, operationDate);
            }
        }

        public static TData GetManagerDataOrContainer<TData>(Uchwyt sfera, object manager, string label)
            where TData : class
        {
            PropertyInfo dataProperty = manager.GetType().GetProperty("Dane", BindingFlags.Instance | BindingFlags.Public);
            if (dataProperty != null)
            {
                try
                {
                    if (dataProperty.GetValue(manager) is TData data)
                    {
                        return data;
                    }
                }
                catch
                {
                }
            }

            TData fromContainer = TryGetServiceFromContainer<TData>(sfera);
            if (fromContainer != null)
            {
                return fromContainer;
            }

            throw new InvalidOperationException($"Nie udało się pobrać {label} ani z managera, ani z kontenera Sfery.");
        }

        public static T GetRequiredService<T>(Uchwyt sfera, DateTime operationDate)
            where T : class
        {
            T service = TryGetServiceFromSfera<T>(sfera, operationDate)
                ?? TryGetServiceFromContainer<T>(sfera);

            return service ?? throw new InvalidOperationException($"Nie udało się pobrać {typeof(T).FullName} ze Sfery.");
        }

        private static T TryGetServiceFromSfera<T>(Uchwyt sfera, DateTime operationDate)
            where T : class
        {
            MethodInfo method = sfera.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == "PodajObiektTypu"
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == 0);

            if (method == null)
            {
                return null;
            }

            try
            {
                object resolved = method.MakeGenericMethod(typeof(T)).Invoke(sfera, null);
                if (resolved is not T typed)
                {
                    return null;
                }

                TrySetSystemDateContext(typed, operationDate);
                return typed;
            }
            catch
            {
                return null;
            }
        }

        private static T TryGetServiceFromContainer<T>(Uchwyt sfera)
            where T : class
        {
            InsERT.Mox.Runtime.IInjectionContainer container = GetSferaContainer(sfera);
            if (container == null)
            {
                return null;
            }

            try
            {
                if (container.GetObject(typeof(T)) is T typed)
                {
                    return typed;
                }
            }
            catch
            {
            }

            try
            {
                if (container.GetNamedObject(typeof(T), "NoTracking") is T typed)
                {
                    return typed;
                }
            }
            catch
            {
            }

            return null;
        }

        private static void TrySetSystemDateContext(object service, DateTime date)
        {
            try
            {
                MethodInfo setContextMethod = service.GetType().GetMethod(
                    "UstawKontekstDaty",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(DateTime) },
                    modifiers: null);

                if (setContextMethod != null)
                {
                    setContextMethod.Invoke(service, new object[] { date });
                    return;
                }

                PropertyInfo dateProperty = service.GetType().GetProperty("DataSystemowa", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (dateProperty?.CanWrite == true)
                {
                    dateProperty.SetValue(service, date);
                }
            }
            catch
            {
            }
        }

        private static InsERT.Mox.Runtime.IInjectionContainer GetSferaContainer(Uchwyt sfera)
        {
            foreach (FieldInfo field in GetInstanceFields(sfera.GetType()))
            {
                try
                {
                    if (field.GetValue(sfera) is InsERT.Mox.Runtime.IInjectionContainer container)
                    {
                        return container;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static IEnumerable<FieldInfo> GetInstanceFields(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    yield return field;
                }
            }
        }

        public static bool TryReadPropertyPath(object target, string propertyPath, out object value)
        {
            value = target;
            foreach (string part in propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (value == null)
                {
                    return false;
                }

                PropertyInfo property = value.GetType().GetProperty(
                    part,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);

                if (property == null)
                {
                    return false;
                }

                try
                {
                    value = property.GetValue(value);
                }
                catch
                {
                    return false;
                }
            }

            return value != null;
        }

        public static bool TryResolvePropertyPath(object target, string propertyPath, out object owner, out PropertyInfo property)
        {
            owner = target;
            property = null;

            string[] parts = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            for (int index = 0; index < parts.Length - 1; index++)
            {
                string part = parts[index];
                if (owner == null)
                {
                    return false;
                }

                PropertyInfo currentProperty = owner.GetType().GetProperty(
                    part,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (currentProperty == null)
                {
                    return false;
                }

                try
                {
                    object next = currentProperty.GetValue(owner);
                    if (next == null)
                    {
                        if (!currentProperty.CanWrite)
                        {
                            return false;
                        }

                        Type nestedType = Nullable.GetUnderlyingType(currentProperty.PropertyType) ?? currentProperty.PropertyType;
                        ConstructorInfo constructor = nestedType.GetConstructor(Type.EmptyTypes);
                        if (constructor == null)
                        {
                            return false;
                        }

                        next = constructor.Invoke(null);
                        currentProperty.SetValue(owner, next);
                    }

                    owner = next;
                }
                catch
                {
                    return false;
                }
            }

            if (owner == null)
            {
                return false;
            }

            property = owner.GetType().GetProperty(
                parts[^1],
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            return property != null;
        }

        public static bool TrySetFirstPropertyPath(object target, object value, params string[] propertyPaths)
        {
            foreach (string propertyPath in propertyPaths)
            {
                if (TrySetPropertyPath(target, propertyPath, value))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TrySetPropertyPath(object target, string propertyPath, object value)
        {
            if (!TryResolvePropertyPath(target, propertyPath, out object owner, out PropertyInfo property)
                || owner == null
                || property == null
                || !property.CanWrite
                || !TryConvertValue(value, property.PropertyType, out object convertedValue))
            {
                return false;
            }

            try
            {
                property.SetValue(owner, convertedValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryConvertValue(object value, Type targetType, out object converted)
        {
            Type actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (value == null)
            {
                converted = targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null
                    ? Activator.CreateInstance(actualType)
                    : null;
                return true;
            }

            if (actualType.IsInstanceOfType(value))
            {
                converted = value;
                return true;
            }

            try
            {
                if (actualType.IsEnum)
                {
                    if (value is string text)
                    {
                        converted = Enum.Parse(actualType, text, ignoreCase: true);
                        return true;
                    }

                    Type enumUnderlyingType = Enum.GetUnderlyingType(actualType);
                    object numericValue = Convert.ChangeType(value, enumUnderlyingType, CultureInfo.InvariantCulture);
                    converted = Enum.ToObject(actualType, numericValue);
                    return true;
                }

                if (actualType == typeof(string))
                {
                    converted = value.ToString();
                    return true;
                }

                if (actualType == typeof(DateTime) && value is DateOnly dateOnly)
                {
                    converted = dateOnly.ToDateTime(TimeOnly.MinValue);
                    return true;
                }

                converted = Convert.ChangeType(value, actualType, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        public static object SafeGetPropertyValue(object target, PropertyInfo property)
        {
            try
            {
                return property.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        public static bool IsDefaultValue(object value)
        {
            Type type = value.GetType();

            if (!type.IsValueType)
            {
                return false;
            }

            object defaultValue = Activator.CreateInstance(type);
            return value.Equals(defaultValue);
        }

        public static string ReadStringCandidate(object target, params string[] propertyPaths)
        {
            foreach (string propertyPath in propertyPaths)
            {
                if (!TryReadPropertyPath(target, propertyPath, out object value) || value == null)
                {
                    continue;
                }

                string text = value.ToString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        public static bool? ReadBoolCandidate(object target, params string[] propertyPaths)
        {
            foreach (string propertyPath in propertyPaths)
            {
                if (!TryReadPropertyPath(target, propertyPath, out object value) || value == null)
                {
                    continue;
                }

                if (value is bool boolValue)
                {
                    return boolValue;
                }

                if (bool.TryParse(value.ToString(), out bool parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        public static int? ReadIntCandidate(object target, params string[] propertyPaths)
        {
            foreach (string propertyPath in propertyPaths)
            {
                if (!TryReadPropertyPath(target, propertyPath, out object value) || value == null)
                {
                    continue;
                }

                switch (value)
                {
                    case byte byteValue:
                        return byteValue;
                    case short shortValue:
                        return shortValue;
                    case int intValue:
                        return intValue;
                    case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                        return (int)longValue;
                }

                if (int.TryParse(value.ToString(), out int parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        public static decimal? ReadDecimalCandidate(object target, params string[] propertyPaths)
        {
            foreach (string propertyPath in propertyPaths)
            {
                if (!TryReadPropertyPath(target, propertyPath, out object value) || value == null)
                {
                    continue;
                }

                switch (value)
                {
                    case decimal decimalValue:
                        return decimalValue;
                    case byte byteValue:
                        return byteValue;
                    case short shortValue:
                        return shortValue;
                    case int intValue:
                        return intValue;
                    case long longValue:
                        return longValue;
                    case float floatValue:
                        return (decimal)floatValue;
                    case double doubleValue:
                        return (decimal)doubleValue;
                }

                if (decimal.TryParse(value.ToString(), out decimal parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        public static List<object> ReadObjectCollection(object target, string propertyPath)
        {
            if (!TryReadPropertyPath(target, propertyPath, out object value) || value == null)
            {
                return new List<object>();
            }

            if (value is string)
            {
                return new List<object>();
            }

            if (value is not IEnumerable enumerable)
            {
                return new List<object>();
            }

            List<object> items = new();
            foreach (object item in enumerable)
            {
                if (item != null)
                {
                    items.Add(item);
                }
            }

            return items;
        }

        public static string NormalizeDigits(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Where(char.IsDigit).ToArray());
        }

        public static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = value.Trim().Normalize(NormalizationForm.FormD);
            StringBuilder builder = new(normalized.Length);

            foreach (char character in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(character switch
                {
                    'ł' => 'l',
                    'Ł' => 'L',
                    _ => character
                });
            }

            return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
        }
    }
}
