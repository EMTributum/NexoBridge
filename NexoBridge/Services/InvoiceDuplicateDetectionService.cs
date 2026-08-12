using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NexoBridge.Services
{
    public class InvoiceDuplicateDetectionService
    {
        private const decimal ThresholdPercent = 70m;

        private static readonly string[] InvoiceNumberPaths =
        {
            "NumerDokumentu",
            "Numer",
            "NrDokumentu",
            "NumerWlasny",
            "NumerPelny",
            "Dokument.Numer",
            "Dokument.NumerWlasny",
            "Dokument.NumerPelny",
            "Dowod.Numer",
            "Dowod.NumerWlasny",
            "DowodKsiegowy.Numer",
            "DowodKsiegowy.NumerWlasny",
            "DokumentKsiegowy.NumerDokumentu",
            "DokumentKsiegowy.NumerPelny",
            "DokumentKsiegowy.NumerWewnetrzny",
            "ZapisKsiegowy.NumerDokumentu",
            "DokumentDoKsiegowania.NumerDokumentu",
            "ZrodlowyDokumentDoKsiegowania.NumerDokumentu",
            "DocelowyDokumentDoKsiegowania.NumerDokumentu"
        };

        private static readonly string[] NipPaths =
        {
            "Podmiot.NIP",
            "PodmiotHistoria.NIP",
            "PodmiotZapisu.NIP",
            "PreviewNIP",
            "Kontrahent.NIP",
            "ZapisKsiegowy.Podmiot.NIP",
            "ZapisKsiegowy.PodmiotHistoria.NIP"
        };

        private static readonly string[] NamePaths =
        {
            "Podmiot.Nazwa",
            "Podmiot.NazwaSkrocona",
            "PodmiotHistoria.Nazwa",
            "PodmiotHistoria.NazwaSkrocona",
            "PodmiotZapisu.Nazwa",
            "PodmiotZapisu.NazwaSkrocona",
            "Kontrahent.Nazwa",
            "Kontrahent.NazwaSkrocona",
            "ZapisKsiegowy.Podmiot.Nazwa",
            "ZapisKsiegowy.PodmiotHistoria.Nazwa"
        };

        private static readonly string[] DatePaths =
        {
            "MiesiacNaliczenia",
            "Data",
            "DataZdarzenia",
            "DataWpisu",
            "DataOtrzymania",
            "DataZakupu",
            "DataSprzedazy",
            "DataWystawienia",
            "DataDokumentu",
            "Dokument.DataWystawienia",
            "Dokument.DataDokumentu",
            "DokumentKsiegowy.DataWystawienia",
            "ZapisKsiegowy.DataZdarzenia",
            "ZapisKsiegowy.DataWpisu"
        };

        private static readonly string[] AmountPaths =
        {
            "Kwota",
            "Wartosc",
            "WartoscBrutto",
            "KwotaBrutto",
            "Brutto",
            "RazemBrutto",
            "WartoscNetto",
            "Netto",
            "ZapisKsiegowy.Wartosc",
            "ZapisKsiegowy.Kwota",
            "ZapisKsiegowy.WartoscBrutto"
        };

        private static readonly string[] KsefPaths =
        {
            "NumerKSeF",
            "ZapisKsiegowy.NumerKSeF"
        };

        private readonly Uchwyt _sfera;
        private readonly ILogger<InvoiceDuplicateDetectionService> _logger;

        public InvoiceDuplicateDetectionService(Uchwyt sfera, ILogger<InvoiceDuplicateDetectionService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public Task<List<PotentialInvoiceDuplicate>> SprawdzDuplikatyAsync(DateTime dataRozliczenia)
        {
            try
            {
                DateTime dataOd = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, 1);
                DateTime dataDo = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, DateTime.DaysInMonth(dataRozliczenia.Year, dataRozliczenia.Month));

                var records = new List<DuplicateInvoiceRecord>();
                records.AddRange(PobierzRekordy("VAT", "IZapisyWEwidencjiVAT", dataOd, dataDo));
                records.AddRange(PobierzRekordy("KPiR", "IZapisyWKPiR", dataOd, dataDo));
                records.AddRange(PobierzRekordy("EP", "IZapisyWEP", dataOd, dataDo));

                records = records
                    .GroupBy(KluczRekordu)
                    .Select(g => g.First())
                    .ToList();

                var duplicateMatches = ZnajdzPotencjalneDuplikaty(records, out int checkedPairs);
                var reportItems = duplicateMatches
                    .Select(MapujDoRaportu)
                    .ToList();

                _logger.LogInformation("[DUPLIKATY FAKTUR] Okres={Okres:yyyy-MM}; rekordy={Records}; pary={Pairs}; potencjalne={Duplicates}",
                    dataRozliczenia,
                    records.Count,
                    checkedPairs,
                    reportItems.Count);

                if (reportItems.Count > 0)
                {
                    _logger.LogWarning("[DUPLIKATY FAKTUR WYKRYTO] Okres={Okres:yyyy-MM}; potencjalne={Duplicates}",
                        dataRozliczenia,
                        OpiszDuplikaty(duplicateMatches));
                }

                return Task.FromResult(reportItems);
            }
            catch (Exception ex)
            {
                string message = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "[DUPLIKATY FAKTUR BŁĄD] Nie udało się wykonać audytu duplikatów faktur: {Message}", message);
                return Task.FromResult(new List<PotentialInvoiceDuplicate>());
            }
        }

        private List<DuplicateInvoiceRecord> PobierzRekordy(string source, string managerInterfaceName, DateTime dataOd, DateTime dataDo)
        {
            var records = new List<DuplicateInvoiceRecord>();
            object manager = PobierzMenedzera(managerInterfaceName);
            if (manager == null)
            {
                _logger.LogDebug("[DUPLIKATY FAKTUR] Brak menedżera {Manager} dla źródła {Source}.", managerInterfaceName, source);
                return records;
            }

            object dane = PobierzWlasciwosc(manager, "Dane");
            if (dane == null)
            {
                _logger.LogDebug("[DUPLIKATY FAKTUR] Menedżer {Manager} nie udostępnia Dane.", managerInterfaceName);
                return records;
            }

            bool queryScopedToMonth;
            string queryName;
            var rawRecords = PobierzSuroweRekordy(dane, dataOd, dataDo, out queryScopedToMonth, out queryName);

            foreach (object raw in rawRecords)
            {
                var record = ZbudujRekord(source, raw);
                if (record == null)
                {
                    continue;
                }

                if (!queryScopedToMonth && !CzyDataWOkresie(record.Date, dataOd, dataDo))
                {
                    continue;
                }

                records.Add(record);
            }

            _logger.LogDebug("[DUPLIKATY FAKTUR ŹRÓDŁO] Source={Source}; manager={Manager}; query={Query}; monthScoped={MonthScoped}; records={Records}",
                source,
                managerInterfaceName,
                queryName,
                queryScopedToMonth,
                records.Count);

            return records;
        }

        private List<object> PobierzSuroweRekordy(object dane, DateTime dataOd, DateTime dataDo, out bool queryScopedToMonth, out string queryName)
        {
            object wynik;
            queryScopedToMonth = true;

            if (TryInvokeEnumerable(dane, "PobierzZapisyZMiesiaca", new object[] { dataOd }, out wynik))
            {
                queryName = "PobierzZapisyZMiesiaca(DateTime)";
                return CastEnumerable(wynik);
            }

            if (TryInvokeEnumerable(dane, "PobierzZapisyZMiesiaca", new object[] { dataOd.Month }, out wynik))
            {
                queryName = "PobierzZapisyZMiesiaca(int)";
                queryScopedToMonth = false;
                return CastEnumerable(wynik);
            }

            if (TryInvokeEnumerable(dane, "PobierzZapisyZOkresu", new object[] { dataOd, dataDo }, out wynik))
            {
                queryName = "PobierzZapisyZOkresu";
                return CastEnumerable(wynik);
            }

            if (TryInvokeEnumerable(dane, "PobierzZapisyZOkresuWgDatyZdarzenia", new object[] { dataOd, dataDo }, out wynik))
            {
                queryName = "PobierzZapisyZOkresuWgDatyZdarzenia";
                return CastEnumerable(wynik);
            }

            queryScopedToMonth = false;
            queryName = "Wszystkie";
            if (TryInvokeEnumerable(dane, "Wszystkie", Array.Empty<object>(), out wynik))
            {
                return CastEnumerable(wynik);
            }

            queryName = "brak";
            return new List<object>();
        }

        private DuplicateInvoiceRecord ZbudujRekord(string source, object entity)
        {
            if (entity == null)
            {
                return null;
            }

            string invoiceNumber = FirstString(entity, InvoiceNumberPaths);
            string nip = InvoiceDocumentMatcher.NormalizeNip(FirstString(entity, NipPaths));
            string name = NormalizeText(FirstString(entity, NamePaths));
            DateTime? date = FirstDate(entity, DatePaths);
            decimal? amount = FirstDecimal(entity, AmountPaths);
            string ksef = FirstString(entity, KsefPaths);
            string id = PobierzWartoscSciezki(entity, "Id")?.ToString()
                ?? PobierzWartoscSciezki(entity, "Nr")?.ToString();

            if (string.IsNullOrWhiteSpace(invoiceNumber) &&
                string.IsNullOrWhiteSpace(nip) &&
                string.IsNullOrWhiteSpace(name) &&
                !amount.HasValue)
            {
                return null;
            }

            return new DuplicateInvoiceRecord
            {
                Source = source,
                EntityType = entity.GetType().Name,
                Id = id,
                InvoiceNumber = invoiceNumber,
                NormalizedInvoiceNumber = InvoiceDocumentMatcher.Normalize(invoiceNumber),
                CounterpartyNip = string.IsNullOrWhiteSpace(nip) ? null : nip,
                CounterpartyName = string.IsNullOrWhiteSpace(name) ? null : name,
                Amount = amount.HasValue ? Math.Round(amount.Value, 2, MidpointRounding.AwayFromZero) : null,
                Date = date?.Date,
                KsefNumber = string.IsNullOrWhiteSpace(ksef) ? null : ksef.Trim()
            };
        }

        private List<DuplicateInvoiceMatch> ZnajdzPotencjalneDuplikaty(List<DuplicateInvoiceRecord> records, out int checkedPairs)
        {
            var duplicates = new List<DuplicateInvoiceMatch>();
            checkedPairs = 0;

            foreach (var group in records.GroupBy(r => r.Source ?? string.Empty))
            {
                var list = group.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        checkedPairs++;
                        var duplicate = Porownaj(list[i], list[j]);
                        if (duplicate != null)
                        {
                            duplicates.Add(duplicate);
                        }
                    }
                }
            }

            return duplicates
                .OrderByDescending(d => d.ScorePercent)
                .ThenBy(d => d.First?.Source)
                .ThenBy(d => d.First?.InvoiceNumber)
                .ToList();
        }

        private DuplicateInvoiceMatch Porownaj(DuplicateInvoiceRecord first, DuplicateInvoiceRecord second)
        {
            var matched = new List<string>();

            if (KwotySaTakieSame(first.Amount, second.Amount))
            {
                matched.Add("amount");
            }

            if (StronySaTakieSame(first, second))
            {
                matched.Add("parties");
            }

            if (!string.IsNullOrWhiteSpace(first.NormalizedInvoiceNumber) &&
                string.Equals(first.NormalizedInvoiceNumber, second.NormalizedInvoiceNumber, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add("invoiceNumber");
            }

            if (first.Date.HasValue &&
                second.Date.HasValue &&
                first.Date.Value.Date == second.Date.Value.Date)
            {
                matched.Add("date");
            }

            int points = matched.Count;
            const int maxPoints = 4;
            decimal score = Math.Round(points * 100m / maxPoints, 2, MidpointRounding.AwayFromZero);
            if (score < ThresholdPercent)
            {
                return null;
            }

            return new DuplicateInvoiceMatch
            {
                ScorePercent = score,
                MatchedCriteria = matched,
                First = first,
                Second = second
            };
        }

        private bool KwotySaTakieSame(decimal? first, decimal? second)
        {
            return first.HasValue &&
                   second.HasValue &&
                   Math.Round(first.Value, 2, MidpointRounding.AwayFromZero) == Math.Round(second.Value, 2, MidpointRounding.AwayFromZero);
        }

        private bool StronySaTakieSame(DuplicateInvoiceRecord first, DuplicateInvoiceRecord second)
        {
            if (!string.IsNullOrWhiteSpace(first.CounterpartyNip) &&
                !string.IsNullOrWhiteSpace(second.CounterpartyNip))
            {
                return string.Equals(first.CounterpartyNip, second.CounterpartyNip, StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrWhiteSpace(first.CounterpartyName) &&
                   !string.IsNullOrWhiteSpace(second.CounterpartyName) &&
                   string.Equals(first.CounterpartyName, second.CounterpartyName, StringComparison.OrdinalIgnoreCase);
        }

        private bool CzyDataWOkresie(DateTime? date, DateTime dataOd, DateTime dataDo)
        {
            return date.HasValue && date.Value.Date >= dataOd.Date && date.Value.Date <= dataDo.Date;
        }

        private string KluczRekordu(DuplicateInvoiceRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.Id))
            {
                return $"{record.Source}|{record.EntityType}|{record.Id}";
            }

            return $"{record.Source}|{record.EntityType}|{record.NormalizedInvoiceNumber}|{record.CounterpartyNip}|{record.CounterpartyName}|{record.Amount}|{record.Date:yyyy-MM-dd}";
        }

        private PotentialInvoiceDuplicate MapujDoRaportu(DuplicateInvoiceMatch match)
        {
            return new PotentialInvoiceDuplicate
            {
                InvoiceNumber = match.First?.InvoiceNumber,
                DuplicateInvoiceNumber = match.Second?.InvoiceNumber,
                SimilarityPercent = match.ScorePercent
            };
        }

        private string OpiszDuplikaty(IEnumerable<DuplicateInvoiceMatch> duplicates)
        {
            return string.Join(" || ", duplicates.Take(50).Select(d =>
                $"{d.ScorePercent}% [{string.Join(",", d.MatchedCriteria)}] {OpiszRekord(d.First)} <-> {OpiszRekord(d.Second)}"));
        }

        private string OpiszRekord(DuplicateInvoiceRecord record)
        {
            if (record == null)
            {
                return "brak";
            }

            return $"{record.Source}:{record.EntityType}:{record.Id ?? "brak"} nr={record.InvoiceNumber ?? "brak"} nip={record.CounterpartyNip ?? "brak"} kwota={record.Amount?.ToString(CultureInfo.InvariantCulture) ?? "brak"} data={record.Date?.ToString("yyyy-MM-dd") ?? "brak"}";
        }

        private object PobierzMenedzera(string nazwaInterfejsu)
        {
            var typSzukany = ZnajdzTypInterfejsu(nazwaInterfejsu);
            if (typSzukany == null)
            {
                return null;
            }

            var metoda = _sfera.GetType().GetMethods().FirstOrDefault(m => m.Name == "PodajObiektTypu" && m.IsGenericMethod && m.GetParameters().Length == 0);
            return metoda?.MakeGenericMethod(typSzukany).Invoke(_sfera, null);
        }

        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                var typ = types?.FirstOrDefault(x => x != null && x.Name == nazwa && x.IsInterface);
                if (typ != null)
                {
                    return typ;
                }
            }

            return null;
        }

        private bool TryInvokeEnumerable(object target, string methodName, object[] args, out object result)
        {
            result = null;
            if (target == null)
            {
                return false;
            }

            var methods = target.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length)
                .ToList();

            foreach (var method in methods)
            {
                try
                {
                    var convertedArgs = DopasujArgumenty(method, args);
                    if (convertedArgs == null)
                    {
                        continue;
                    }

                    result = method.Invoke(target, convertedArgs);
                    if (result is IEnumerable)
                    {
                        return true;
                    }
                }
                catch
                {
                    result = null;
                }
            }

            return false;
        }

        private object[] DopasujArgumenty(MethodInfo method, object[] args)
        {
            var parameters = method.GetParameters();
            var converted = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                object value = args[i];
                Type targetType = Nullable.GetUnderlyingType(parameters[i].ParameterType) ?? parameters[i].ParameterType;

                if (value == null)
                {
                    converted[i] = null;
                    continue;
                }

                if (targetType.IsInstanceOfType(value))
                {
                    converted[i] = value;
                    continue;
                }

                try
                {
                    converted[i] = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
                }
            }

            return converted;
        }

        private List<object> CastEnumerable(object value)
        {
            try
            {
                return ((IEnumerable)value).Cast<object>().ToList();
            }
            catch
            {
                return new List<object>();
            }
        }

        private string FirstString(object entity, IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                object value = PobierzWartoscSciezki(entity, path);
                string text = value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        private DateTime? FirstDate(object entity, IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                object value = PobierzWartoscSciezki(entity, path);
                DateTime? date = ConvertDate(value);
                if (date.HasValue)
                {
                    return date.Value.Date;
                }
            }

            return null;
        }

        private decimal? FirstDecimal(object entity, IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                object value = PobierzWartoscSciezki(entity, path);
                decimal? amount = ConvertDecimal(value);
                if (amount.HasValue)
                {
                    return amount.Value;
                }
            }

            return null;
        }

        private object PobierzWartoscSciezki(object obiekt, string sciezka)
        {
            object aktualny = obiekt;
            foreach (string nazwaWlasciwosci in sciezka.Split('.'))
            {
                if (aktualny == null)
                {
                    return null;
                }

                var prop = aktualny.GetType().GetProperty(nazwaWlasciwosci, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null)
                {
                    return null;
                }

                try { aktualny = prop.GetValue(aktualny); }
                catch { return null; }
            }

            return aktualny;
        }

        private object PobierzWlasciwosc(object obiekt, string nazwa)
        {
            if (obiekt == null)
            {
                return null;
            }

            try
            {
                return obiekt.GetType().GetProperty(nazwa, BindingFlags.Instance | BindingFlags.Public)?.GetValue(obiekt);
            }
            catch
            {
                return null;
            }
        }

        private DateTime? ConvertDate(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is DateTime dt)
            {
                return dt.Date;
            }

            string text = value.ToString();
            if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("pl-PL"), DateTimeStyles.None, out DateTime plDate))
            {
                return plDate.Date;
            }

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime invariantDate))
            {
                return invariantDate.Date;
            }

            return null;
        }

        private decimal? ConvertDecimal(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is decimal dec)
            {
                return dec;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            if (value is double doubleValue)
            {
                return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
            }

            if (value is float floatValue)
            {
                return Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture);
            }

            decimal? nested = ConvertDecimal(PobierzWlasciwosc(value, "Value"))
                ?? ConvertDecimal(PobierzWlasciwosc(value, "Wartosc"))
                ?? ConvertDecimal(PobierzWlasciwosc(value, "Kwota"));
            if (nested.HasValue)
            {
                return nested;
            }

            string text = value.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("pl-PL"), out decimal plDecimal))
            {
                return plDecimal;
            }

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal invariantDecimal))
            {
                return invariantDecimal;
            }

            return null;
        }

        private string NormalizeText(string value)
        {
            string text = value?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return string.Join(" ", text.ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private class DuplicateInvoiceMatch
        {
            public decimal ScorePercent { get; set; }
            public List<string> MatchedCriteria { get; set; } = new List<string>();
            public DuplicateInvoiceRecord First { get; set; }
            public DuplicateInvoiceRecord Second { get; set; }
        }

        private class DuplicateInvoiceRecord
        {
            public string Source { get; set; }
            public string EntityType { get; set; }
            public string Id { get; set; }
            public string InvoiceNumber { get; set; }
            public string NormalizedInvoiceNumber { get; set; }
            public string CounterpartyNip { get; set; }
            public string CounterpartyName { get; set; }
            public decimal? Amount { get; set; }
            public DateTime? Date { get; set; }
            public string KsefNumber { get; set; }
        }
    }
}
