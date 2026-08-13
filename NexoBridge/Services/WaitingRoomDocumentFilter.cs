using InsERT.Moria;
using InsERT.Moria.DokumentyDoKsiegowania;
using InsERT.Moria.ModelDanych;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NexoBridge.Services
{
    public sealed class WaitingRoomDocumentSelection
    {
        public List<DokumentDoKsiegowania> Included { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> IncludedByNewMarker { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> IncludedPayrollException { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> SkippedNotNew { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> PartialAmortization { get; } = new List<DokumentDoKsiegowania>();
        public List<DokumentDoKsiegowania> EmployeeBillsWithSubject { get; } = new List<DokumentDoKsiegowania>();

        public int Total =>
            Included.Count +
            SkippedNotNew.Count +
            PartialAmortization.Count +
            EmployeeBillsWithSubject.Count;
    }

    public sealed class NewDocumentMarkerContext
    {
        private readonly Func<DokumentDoKsiegowania, bool> _czyNowy;

        public NewDocumentMarkerContext(ParametrImportuKsiegowego parametrImportu, IDataSystemowa dataSystemowa)
        {
            ParametrImportu = parametrImportu ?? throw new InvalidOperationException("Nie udało się pobrać parametrów importu księgowego wymaganych do odczytu statusu N.");
            DataSystemowa = dataSystemowa ?? throw new InvalidOperationException("Nie udało się pobrać IDataSystemowa wymaganego do odczytu statusu N.");
            _czyNowy = dokument => DokumentDoKsiegowaniaExtensions.CzyNowy(dokument, ParametrImportu, DataSystemowa);
        }

        internal NewDocumentMarkerContext(Func<DokumentDoKsiegowania, bool> czyNowy)
        {
            _czyNowy = czyNowy ?? throw new ArgumentNullException(nameof(czyNowy));
        }

        public ParametrImportuKsiegowego ParametrImportu { get; }
        public IDataSystemowa DataSystemowa { get; }

        public bool CzyNowy(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return false;

            try
            {
                return _czyNowy(dokument);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nie udało się odczytać statusu N dla dokumentu: {InvoiceDocumentMatcher.Describe(dokument)}.", ex);
            }
        }
    }

    public static class WaitingRoomDocumentFilter
    {
        private static readonly string[] EmployeeBillPropertyNames =
        {
            "RachunekDoUmowyPracowniczej",
            "SkladkiZUSRachunkuDoUmowyPracowniczej",
            "ZaliczkaRachunkuDoUmowyPracowniczej"
        };

        public static WaitingRoomDocumentSelection SelectForNewMarker(IEnumerable<DokumentDoKsiegowania> documents, NewDocumentMarkerContext markerContext)
        {
            if (markerContext == null)
            {
                throw new InvalidOperationException("Nie udało się zainicjalizować resolvera statusu N. Dekretacja została przerwana, żeby nie wybrać dokumentów po błędnym kryterium.");
            }

            var selection = new WaitingRoomDocumentSelection();
            var allDocuments = (documents ?? Enumerable.Empty<DokumentDoKsiegowania>()).Where(d => d != null).ToList();

            foreach (var document in allDocuments)
            {
                if (CzyCzastkowaAmortyzacja(document))
                {
                    selection.PartialAmortization.Add(document);
                    continue;
                }

                if (CzyListaPlacLubListaRachunkow(document) || CzyRachunekDoUmowyPracowniczejBezPodmiotu(document))
                {
                    selection.Included.Add(document);
                    selection.IncludedPayrollException.Add(document);
                    continue;
                }

                if (CzyRachunekDoUmowyPracowniczejZPodmiotem(document))
                {
                    selection.EmployeeBillsWithSubject.Add(document);
                    continue;
                }

                if (markerContext.CzyNowy(document))
                {
                    selection.Included.Add(document);
                    selection.IncludedByNewMarker.Add(document);
                    continue;
                }

                selection.SkippedNotNew.Add(document);
            }

            return selection;
        }

        public static bool CzyCzastkowaAmortyzacja(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return false;

            try
            {
                if (PobierzWlasciwosc(dokument, "OperacjaAMZ") != null) return false;
            }
            catch { }

            try
            {
                var operacja = PobierzWlasciwosc(dokument, "OperacjaST");
                if (operacja == null) return false;

                return operacja is OperacjaAM || operacja.GetType().Name.Contains("OperacjaAM");
            }
            catch
            {
                return false;
            }
        }

        public static bool CzyListaPlacLubListaRachunkow(DokumentDoKsiegowania dokument)
        {
            var rodzaj = PobierzRodzajDokumentuDoKsiegowania(dokument);
            if (!rodzaj.HasValue) return false;

            switch (rodzaj.Value)
            {
                case RodzajDokumentuDoKsiegowania.ListaPlac:
                case RodzajDokumentuDoKsiegowania.ListaPlacBezSkladekIZaliczek:
                case RodzajDokumentuDoKsiegowania.ListaRachunkow:
                case RodzajDokumentuDoKsiegowania.ListaRachunkowBezSkladekIZaliczek:
                    return true;
                default:
                    return false;
            }
        }

        public static bool CzyRachunekDoUmowyPracowniczej(DokumentDoKsiegowania dokument)
        {
            var rodzaj = PobierzRodzajDokumentuDoKsiegowania(dokument);
            if (rodzaj.HasValue)
            {
                return rodzaj.Value == RodzajDokumentuDoKsiegowania.RachunekDoUmowyPracowniczej ||
                       rodzaj.Value == RodzajDokumentuDoKsiegowania.RachunekDoUmowyPracowniczejBezSkladekIZaliczek;
            }

            return PobierzRachunkiPracownicze(dokument).Any();
        }

        public static bool CzyRachunekDoUmowyPracowniczejBezPodmiotu(DokumentDoKsiegowania dokument)
        {
            if (!CzyRachunekDoUmowyPracowniczej(dokument)) return false;
            return !MaPodmiot(dokument) && !PobierzRachunkiPracownicze(dokument).Any(MaPodmiot);
        }

        public static bool CzyRachunekDoUmowyPracowniczejZPodmiotem(DokumentDoKsiegowania dokument)
        {
            if (!CzyRachunekDoUmowyPracowniczej(dokument)) return false;
            return MaPodmiot(dokument) || PobierzRachunkiPracownicze(dokument).Any(MaPodmiot);
        }

        public static RodzajDokumentuDoKsiegowania? PobierzRodzajDokumentuDoKsiegowania(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) return null;

            try
            {
                var typ = dokument.TypDokumentuDoKsiegowania;
                if (typ == null) return null;

                return (RodzajDokumentuDoKsiegowania)typ.RodzajDokumentuDoKsiegowania;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<object> PobierzRachunkiPracownicze(DokumentDoKsiegowania dokument)
        {
            if (dokument == null) yield break;

            foreach (string propertyName in EmployeeBillPropertyNames)
            {
                var value = PobierzWlasciwosc(dokument, propertyName);
                if (value != null) yield return value;
            }
        }

        private static bool MaPodmiot(object source)
        {
            if (source == null) return false;

            return PobierzWlasciwosc(source, "Podmiot") != null ||
                   PobierzWlasciwosc(source, "PodmiotHistoria") != null;
        }

        private static object PobierzWlasciwosc(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName)) return null;

            try
            {
                var property = source.GetType().GetProperty(propertyName);
                if (property != null) return property.GetValue(source);

                var field = source.GetType().GetField(propertyName);
                return field?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        // Diagnostyka na żądanie dla incydentu "znacznik nowości zablokowany" - nie zmienia logiki wyboru,
        // tylko zrzuca proste (nie-referencyjne) właściwości kontekstu i pominiętych dokumentów, żeby przy
        // następnym incydencie mieć realne dane (daty/parametry) a nie tylko licznik SkippedNotNew.
        public static string DescribeNewMarkerDiagnostics(NewDocumentMarkerContext context, IEnumerable<DokumentDoKsiegowania> skippedNotNew, int maxDocuments = 30)
        {
            if (context == null) return "brak kontekstu";

            var parts = new List<string>
            {
                $"parametrImportu=[{DescribeObjectShallow(context.ParametrImportu)}]",
                $"dataSystemowa=[{DescribeObjectShallow(context.DataSystemowa)}]"
            };

            var skipped = (skippedNotNew ?? Enumerable.Empty<DokumentDoKsiegowania>()).Take(maxDocuments).ToList();
            if (skipped.Count > 0)
            {
                var dokumentySzczegoly = skipped.Select(d => $"{InvoiceDocumentMatcher.Describe(d)}: [{DescribeObjectShallow(d)}]");
                parts.Add($"pominieteBezN=[{string.Join(" || ", dokumentySzczegoly)}]");
            }

            return string.Join("; ", parts);
        }

        private static string DescribeObjectShallow(object value)
        {
            if (value == null) return "brak";

            var dump = value.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 && JestProstymTypem(p.PropertyType))
                .Select(p =>
                {
                    try
                    {
                        object propertyValue = p.GetValue(value);
                        return $"{p.Name}={(propertyValue == null ? "null" : propertyValue.ToString())}";
                    }
                    catch (Exception ex)
                    {
                        return $"{p.Name}=<odczyt nieudany: {ex.GetType().Name}>";
                    }
                })
                .ToList();

            return dump.Count == 0 ? value.GetType().FullName : string.Join(", ", dump);
        }

        private static bool JestProstymTypem(Type type)
        {
            var podstawowy = Nullable.GetUnderlyingType(type) ?? type;
            return podstawowy.IsPrimitive || podstawowy.IsEnum ||
                   podstawowy == typeof(string) || podstawowy == typeof(DateTime) ||
                   podstawowy == typeof(decimal) || podstawowy == typeof(Guid) ||
                   podstawowy == typeof(TimeSpan);
        }
    }
}
