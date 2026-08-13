using InsERT.Moria.Klienci;
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
    public class VatStatusVerificationService
    {
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

        private static readonly string[] VerificationDatePaths =
        {
            "DataSprzedazy",
            "DataZakupu",
            "DataOtrzymania",
            "DataWystawienia",
            "DataDokumentu",
            "DataZdarzenia",
            "Data",
            "DataWpisu",
            "Dokument.DataWystawienia",
            "Dokument.DataDokumentu",
            "DokumentKsiegowy.DataWystawienia",
            "ZapisKsiegowy.DataZdarzenia",
            "ZapisKsiegowy.DataWpisu"
        };

        private readonly Uchwyt _sfera;
        private readonly ILogger<VatStatusVerificationService> _logger;

        public VatStatusVerificationService(Uchwyt sfera, ILogger<VatStatusVerificationService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public Task<List<VatStatusVerificationResult>> SprawdzStatusyVatAsync(DateTime dataRozliczenia)
        {
            DateTime dataOd = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, 1);
            DateTime dataDo = new DateTime(dataRozliczenia.Year, dataRozliczenia.Month, DateTime.DaysInMonth(dataRozliczenia.Year, dataRozliczenia.Month));

            try
            {
                var records = new List<VatStatusVerificationRecord>();
                records.AddRange(PobierzRekordy("VAT", "IZapisyWEwidencjiVAT", dataOd, dataDo));
                records.AddRange(PobierzRekordy("KPiR", "IZapisyWKPiR", dataOd, dataDo));
                records.AddRange(PobierzRekordy("EP", "IZapisyWEP", dataOd, dataDo));

                records = records
                    .Where(r => !string.IsNullOrWhiteSpace(r.InvoiceNumber) || !string.IsNullOrWhiteSpace(r.CounterpartyNip))
                    .GroupBy(KluczRekordu)
                    .Select(g => g.First())
                    .ToList();

                if (records.Count == 0)
                {
                    _logger.LogInformation("[VAT STATUS AUDYT] Okres={Okres:yyyy-MM}; brak dokumentów do sprawdzenia.", dataRozliczenia);
                    return Task.FromResult(new List<VatStatusVerificationResult>());
                }

                IBialaListaPodatnikowConnector connector;
                try
                {
                    connector = SferaReflectionHelpers.GetRequiredService<IBialaListaPodatnikowConnector>(_sfera, dataDo);
                }
                catch (Exception ex)
                {
                    string message = $"Nie udało się pobrać connectora Białej Listy VAT ze Sfery: {ex.GetBaseException().Message}";
                    _logger.LogWarning(ex, "[VAT STATUS AUDYT BŁĄD] {Message}", message);
                    return Task.FromResult(records.Select(r => ZbudujBlad(r, message)).ToList());
                }

                var cache = new Dictionary<string, VatStatusLookupResult>(StringComparer.OrdinalIgnoreCase);
                var results = new List<VatStatusVerificationResult>();
                foreach (var record in records)
                {
                    results.Add(SprawdzRekord(connector, record, cache));
                }

                int ok = results.Count(r => r.VerificationOk && r.ActiveVatPayer == true);
                int inactive = results.Count(r => r.VerificationOk && r.ActiveVatPayer == false);
                int failed = results.Count(r => !r.VerificationOk);
                _logger.LogInformation("[VAT STATUS AUDYT] Okres={Okres:yyyy-MM}; rekordy={Records}; zapytania={Queries}; czynni={Active}; nieczynni={Inactive}; błędy={Failed}",
                    dataRozliczenia,
                    records.Count,
                    cache.Count,
                    ok,
                    inactive,
                    failed);

                if (inactive > 0 || failed > 0)
                {
                    _logger.LogWarning("[VAT STATUS AUDYT UWAGI] Okres={Okres:yyyy-MM}; dokumenty={Documents}",
                        dataRozliczenia,
                        OpiszProblemy(results));
                }

                return Task.FromResult(results);
            }
            catch (Exception ex)
            {
                string message = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "[VAT STATUS AUDYT BŁĄD] Nie udało się wykonać audytu statusu VAT kontrahentów: {Message}", message);
                return Task.FromResult(new List<VatStatusVerificationResult>());
            }
        }

        private VatStatusVerificationResult SprawdzRekord(
            IBialaListaPodatnikowConnector connector,
            VatStatusVerificationRecord record,
            Dictionary<string, VatStatusLookupResult> cache)
        {
            if (string.IsNullOrWhiteSpace(record.CounterpartyNip))
            {
                return ZbudujBlad(record, "Brak NIP kontrahenta - nie można sprawdzić statusu VAT.");
            }

            if (!CzyPolskiNip(record.CounterpartyNip))
            {
                return ZbudujBlad(record, $"NIP kontrahenta '{record.CounterpartyNip}' nie wygląda na polski NIP - pominięto sprawdzenie w Białej Liście VAT.");
            }

            string cacheKey = $"{record.CounterpartyNip}|{record.VerificationDate:yyyy-MM-dd}";
            if (!cache.TryGetValue(cacheKey, out VatStatusLookupResult lookup))
            {
                lookup = SprawdzNip(connector, record.CounterpartyNip, record.VerificationDate);
                cache[cacheKey] = lookup;
            }

            return new VatStatusVerificationResult
            {
                InvoiceNumber = record.InvoiceNumber,
                VendorNip = record.CounterpartyNip,
                VerificationDate = record.VerificationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                VerificationDateSource = record.VerificationDateSource,
                VerificationOk = lookup.VerificationOk,
                ActiveVatPayer = lookup.ActiveVatPayer,
                StatusVat = lookup.StatusVat,
                Message = lookup.Message
            };
        }

        private VatStatusLookupResult SprawdzNip(IBialaListaPodatnikowConnector connector, string nip, DateTime date)
        {
            try
            {
                object wynik = connector.SprawdzPodatnikaNaDzien(nip, date.Date, SposobWeryfikacjiPodatnika.NIP);
                if (wynik == null)
                {
                    return VatStatusLookupResult.Failed("Sfera zwróciła pusty wynik sprawdzenia statusu VAT.");
                }

                bool verificationOk = SferaReflectionHelpers.ReadBoolCandidate(wynik, "PoprawneSprawdzenie") == true;
                bool? activeVatPayer = SferaReflectionHelpers.ReadBoolCandidate(wynik, "CzynnyPodatnik");
                string statusVat = SferaReflectionHelpers.ReadStringCandidate(wynik, "StatusVAT");
                string error = SferaReflectionHelpers.ReadStringCandidate(wynik, "KomunikatBledu");
                string errorCode = SferaReflectionHelpers.ReadStringCandidate(wynik, "KodBledu");
                string checkId = SferaReflectionHelpers.ReadStringCandidate(wynik, "Identyfikator", "IdSprawdzenia");

                string message = null;
                if (!verificationOk)
                {
                    message = !string.IsNullOrWhiteSpace(error)
                        ? $"{ErrorCodePrefix(errorCode)}{error}"
                        : "Sfera nie potwierdziła poprawnego sprawdzenia statusu VAT.";
                }
                else if (activeVatPayer == false)
                {
                    message = $"Kontrahent nie jest czynnym podatnikiem VAT na dzień {date:yyyy-MM-dd}. StatusVAT={statusVat ?? "brak"}.";
                }

                _logger.LogDebug("[VAT STATUS SPRAWDZENIE] NIP={Nip}; data={Date:yyyy-MM-dd}; ok={Ok}; czynny={Active}; status={Status}; checkId={CheckId}; msg={Msg}",
                    nip,
                    date,
                    verificationOk,
                    activeVatPayer,
                    statusVat,
                    checkId,
                    message ?? "brak");

                return new VatStatusLookupResult
                {
                    VerificationOk = verificationOk,
                    ActiveVatPayer = activeVatPayer,
                    StatusVat = statusVat,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                string message = $"Wyjątek podczas sprawdzania statusu VAT: {ex.GetBaseException().Message}";
                _logger.LogWarning(ex, "[VAT STATUS SPRAWDZENIE BŁĄD] NIP={Nip}; data={Date:yyyy-MM-dd}; {Message}", nip, date, message);
                return VatStatusLookupResult.Failed(message);
            }
        }

        private static string ErrorCodePrefix(string errorCode)
        {
            return string.IsNullOrWhiteSpace(errorCode) ? string.Empty : $"[{errorCode}] ";
        }

        private List<VatStatusVerificationRecord> PobierzRekordy(string source, string managerInterfaceName, DateTime dataOd, DateTime dataDo)
        {
            var records = new List<VatStatusVerificationRecord>();

            try
            {
                object manager = PobierzMenedzera(managerInterfaceName);
                if (manager == null)
                {
                    _logger.LogDebug("[VAT STATUS ŹRÓDŁO] Brak menedżera {Manager} dla źródła {Source}.", managerInterfaceName, source);
                    return records;
                }

                object dane = PobierzWlasciwosc(manager, "Dane");
                if (dane == null)
                {
                    _logger.LogDebug("[VAT STATUS ŹRÓDŁO] Menedżer {Manager} nie udostępnia Dane.", managerInterfaceName);
                    return records;
                }

                bool queryScopedToMonth;
                string queryName;
                var rawRecords = PobierzSuroweRekordy(dane, dataOd, dataDo, out queryScopedToMonth, out queryName);
                foreach (object raw in rawRecords)
                {
                    var record = ZbudujRekord(source, raw, dataDo);
                    if (record == null)
                    {
                        continue;
                    }

                    if (!queryScopedToMonth && !CzyDataWOkresie(record.DocumentDate, dataOd, dataDo))
                    {
                        continue;
                    }

                    records.Add(record);
                }

                _logger.LogDebug("[VAT STATUS ŹRÓDŁO] Source={Source}; manager={Manager}; query={Query}; monthScoped={MonthScoped}; records={Records}",
                    source,
                    managerInterfaceName,
                    queryName,
                    queryScopedToMonth,
                    records.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VAT STATUS ŹRÓDŁO BŁĄD] Nie udało się odczytać źródła {Source}/{Manager}: {Message}",
                    source,
                    managerInterfaceName,
                    ex.GetBaseException().Message);
            }

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

        private VatStatusVerificationRecord ZbudujRekord(string source, object entity, DateTime periodEnd)
        {
            if (entity == null)
            {
                return null;
            }

            string invoiceNumber = FirstString(entity, InvoiceNumberPaths);
            string nip = InvoiceDocumentMatcher.NormalizeNip(FirstString(entity, NipPaths));
            DateTime? documentDate = FirstDate(entity, VerificationDatePaths);
            DateTime verificationDate = documentDate?.Date ?? periodEnd.Date;
            string verificationDateSource = documentDate.HasValue ? "documentDate" : "periodEndFallback";
            string id = PobierzWartoscSciezki(entity, "Id")?.ToString()
                ?? PobierzWartoscSciezki(entity, "Nr")?.ToString();

            if (string.IsNullOrWhiteSpace(invoiceNumber) && string.IsNullOrWhiteSpace(nip))
            {
                return null;
            }

            return new VatStatusVerificationRecord
            {
                Source = source,
                EntityType = entity.GetType().Name,
                Id = id,
                InvoiceNumber = invoiceNumber,
                NormalizedInvoiceNumber = InvoiceDocumentMatcher.Normalize(invoiceNumber),
                CounterpartyNip = string.IsNullOrWhiteSpace(nip) ? null : nip,
                DocumentDate = documentDate?.Date,
                VerificationDate = verificationDate.Date,
                VerificationDateSource = verificationDateSource
            };
        }

        private VatStatusVerificationResult ZbudujBlad(VatStatusVerificationRecord record, string message)
        {
            return new VatStatusVerificationResult
            {
                InvoiceNumber = record.InvoiceNumber,
                VendorNip = record.CounterpartyNip,
                VerificationDate = record.VerificationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                VerificationDateSource = record.VerificationDateSource,
                VerificationOk = false,
                ActiveVatPayer = null,
                StatusVat = null,
                Message = message
            };
        }

        private static bool CzyPolskiNip(string nip)
        {
            return !string.IsNullOrWhiteSpace(nip)
                && nip.Length == 10
                && nip.All(char.IsDigit);
        }

        private static bool CzyDataWOkresie(DateTime? date, DateTime dataOd, DateTime dataDo)
        {
            return date.HasValue && date.Value.Date >= dataOd.Date && date.Value.Date <= dataDo.Date;
        }

        private static string KluczRekordu(VatStatusVerificationRecord record)
        {
            return $"{record.CounterpartyNip}|{record.NormalizedInvoiceNumber}|{record.VerificationDate:yyyy-MM-dd}";
        }

        private static string OpiszProblemy(IEnumerable<VatStatusVerificationResult> results)
        {
            return string.Join(" || ", results
                .Where(r => !r.VerificationOk || r.ActiveVatPayer == false)
                .Take(50)
                .Select(r => $"nr={r.InvoiceNumber ?? "brak"} nip={r.VendorNip ?? "brak"} data={r.VerificationDate} ok={r.VerificationOk} czynny={r.ActiveVatPayer?.ToString() ?? "brak"} status={r.StatusVat ?? "brak"} msg={r.Message ?? "brak"}"));
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

        private static Type ZnajdzTypInterfejsu(string nazwa)
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

        private static bool TryInvokeEnumerable(object target, string methodName, object[] args, out object result)
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

        private static object[] DopasujArgumenty(MethodInfo method, object[] args)
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

        private static List<object> CastEnumerable(object value)
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

        private static string FirstString(object entity, IEnumerable<string> paths)
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

        private static DateTime? FirstDate(object entity, IEnumerable<string> paths)
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

        private static object PobierzWartoscSciezki(object obiekt, string sciezka)
        {
            object aktualny = obiekt;
            foreach (string nazwaWlasciwosci in sciezka.Split('.'))
            {
                if (aktualny == null)
                {
                    return null;
                }

                var prop = aktualny.GetType().GetProperty(nazwaWlasciwosci, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (prop == null)
                {
                    return null;
                }

                try { aktualny = prop.GetValue(aktualny); }
                catch { return null; }
            }

            return aktualny;
        }

        private static object PobierzWlasciwosc(object obiekt, string nazwa)
        {
            if (obiekt == null)
            {
                return null;
            }

            try
            {
                return obiekt.GetType().GetProperty(nazwa, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(obiekt);
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ConvertDate(object value)
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

        private class VatStatusLookupResult
        {
            public bool VerificationOk { get; set; }
            public bool? ActiveVatPayer { get; set; }
            public string StatusVat { get; set; }
            public string Message { get; set; }

            public static VatStatusLookupResult Failed(string message)
            {
                return new VatStatusLookupResult
                {
                    VerificationOk = false,
                    ActiveVatPayer = null,
                    StatusVat = null,
                    Message = message
                };
            }
        }

        private class VatStatusVerificationRecord
        {
            public string Source { get; set; }
            public string EntityType { get; set; }
            public string Id { get; set; }
            public string InvoiceNumber { get; set; }
            public string NormalizedInvoiceNumber { get; set; }
            public string CounterpartyNip { get; set; }
            public DateTime? DocumentDate { get; set; }
            public DateTime VerificationDate { get; set; }
            public string VerificationDateSource { get; set; }
        }
    }
}
