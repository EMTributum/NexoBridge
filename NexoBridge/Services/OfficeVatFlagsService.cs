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
    public class OfficeVatFlagsService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<OfficeVatFlagsService> _logger;
        private object _connectionDataResolver;
        private bool _connectionDataResolverChecked;
        private object _databaseNameConverter;
        private bool _databaseNameConverterChecked;
        private int _databaseConnectionMissingBase;
        private int _databaseConnectionMissingId;
        private int _databaseConnectionMissingBytes;
        private int _databaseConnectionMissingResolver;
        private int _databaseConnectionResolverFromEnvironment;
        private int _databaseConnectionEmptyResult;
        private int _databaseConnectionExceptions;
        private string _databaseConnectionFirstException;
        private string _officeDatabaseName;
        private const string ProductDatabaseNamePrefix = "Nexo_";

        public OfficeVatFlagsService(Uchwyt sfera, ILogger<OfficeVatFlagsService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task<OfficeVatFlagsReport> PobierzFlagiAsync(OfficeVatFlagsJob job, Func<int, string, Task> raportujPostep)
        {
            var report = new OfficeVatFlagsReport
            {
                JobId = job.JobId,
                Status = "SUCCESS",
                Message = string.IsNullOrWhiteSpace(job.Nip)
                    ? "Odczytano flagi VAT/VAT-UE klientow Biura."
                    : "Odczytano flagi VAT/VAT-UE klienta Biura.",
                OfficeDatabaseName = job.OfficeDatabaseName,
                Nip = job.Nip,
                NormalizedNip = NormalizujIdPodatkowy(job.Nip)
            };
            _officeDatabaseName = job.OfficeDatabaseName;

            await raportujPostep(30, "Odczyt klientow Biura...");
            var items = PobierzKlientowBiura(report);

            if (!string.IsNullOrWhiteSpace(report.NormalizedNip))
            {
                report.Item = items.FirstOrDefault(x => x.NormalizedNip == report.NormalizedNip);
                if (report.Item == null)
                {
                    report.Status = "NOT_FOUND";
                    report.Message = "Nie znaleziono klienta Biura o podanym NIP.";
                    await raportujPostep(100, "Nie znaleziono klienta Biura o podanym NIP.");
                    return report;
                }

                report.Items.Add(report.Item);
            }
            else
            {
                report.Items.AddRange(items.OrderBy(x => x.Name ?? x.ShortName ?? x.Nip).ThenBy(x => x.Nip));
            }

            UzupelnijMapowanieBaz(report);

            if (report.Warnings.Any() && report.Status == "SUCCESS")
            {
                report.Status = "PARTIAL_SUCCESS";
                report.Message = "Odczytano flagi VAT/VAT-UE klientow Biura z ostrzezeniami.";
            }

            await raportujPostep(100, "Odczyt flag VAT/VAT-UE z Biura zakonczony.");
            return report;
        }

        public async Task<OfficeVatFlagsReport> PobierzNazwyBazDanychAsync(OfficeVatFlagsJob job, Func<int, string, Task> raportujPostep)
        {
            var report = new OfficeVatFlagsReport
            {
                JobId = job.JobId,
                Status = "SUCCESS",
                Message = "Odczytano mapowanie NIP -> nazwa bazy danych klientow Biura.",
                OfficeDatabaseName = job.OfficeDatabaseName,
                Source = "Biuro",
                Precision = "databaseNameMap"
            };
            _officeDatabaseName = job.OfficeDatabaseName;

            await raportujPostep(30, "Odczyt klientow i nazw baz danych z Biura...");
            var items = PobierzKlientowBiura(report)
                .OrderBy(x => x.Name ?? x.ShortName ?? x.Nip)
                .ThenBy(x => x.Nip)
                .ToList();

            report.Items.AddRange(items);
            UzupelnijMapowanieBaz(report);
            DodajDiagnostykeOdczytuBaz(report);

            int withoutDatabaseName = report.DatabaseMappings.Count(x => string.IsNullOrWhiteSpace(x.DatabaseName));
            if (withoutDatabaseName > 0)
            {
                report.Status = "PARTIAL_SUCCESS";
                report.Message = "Odczytano mapowanie NIP -> nazwa bazy danych klientow Biura z ostrzezeniami.";
                report.Warnings.Add($"Nie udalo sie odczytac nazwy bazy danych dla {withoutDatabaseName} klientow Biura.");
            }

            await raportujPostep(100, "Synchronizacja nazw baz danych klientow Biura zakonczona.");
            return report;
        }

        private List<OfficeVatFlagsItem> PobierzKlientowBiura(OfficeVatFlagsReport report)
        {
            var result = new List<OfficeVatFlagsItem>();

            dynamic mgrPodmioty = PobierzMenedzera("IPodmioty");
            if (mgrPodmioty == null)
            {
                report.Status = "FAILED";
                report.Message = "Nie udalo sie pobrac menedzera IPodmioty.";
                report.Warnings.Add(report.Message);
                return result;
            }

            try
            {
                var podmioty = PobierzPodmiotyZDanymiKlientaBiura(mgrPodmioty);
                foreach (var podmiot in podmioty)
                {
                    object klient = SafeObj(podmiot, "KlientBiura");
                    if (klient == null)
                    {
                        continue;
                    }

                    report.TotalOfficeClients++;
                    var item = MapujKlientaBiura(podmiot, klient);
                    if (string.IsNullOrWhiteSpace(item.NormalizedNip))
                    {
                        report.ClientsWithoutNip++;
                        continue;
                    }

                    result.Add(item);
                }
            }
            catch (Exception ex)
            {
                string message = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "Nie udalo sie odczytac klientow Biura.");
                report.Status = "FAILED";
                report.Message = "Blad odczytu klientow Biura: " + message;
                report.Warnings.Add(report.Message);
            }

            return result;
        }

        private IEnumerable<dynamic> PobierzPodmiotyZDanymiKlientaBiura(dynamic mgrPodmioty)
        {
            try
            {
                return ((IEnumerable)mgrPodmioty.Dane.WszystkieDostepne(new[]
                {
                    "Firma",
                    "Grupy",
                    "FlagaWlasna",
                    "KlientBiura",
                    "KlientBiura.BazaDanych",
                    "OpiekunPodstawowy",
                    "OpiekunPodstawowy.Uzytkownik",
                    "OpiekunPodstawowy.Uzytkownik.Osoba"
                })).Cast<dynamic>().ToList();
            }
            catch
            {
                return ((IEnumerable)mgrPodmioty.Dane.Wszystkie()).Cast<dynamic>().ToList();
            }
        }

        private OfficeVatFlagsItem MapujKlientaBiura(object podmiot, object klient)
        {
            object firma = SafeObj(podmiot, "Firma");
            string nip = SafeString(podmiot, "NIP");
            string nipUe = SafeString(podmiot, "NIPUE");
            var groupNames = PobierzNazwyGrup(podmiot);
            string vatUeFlagName = SafeString(SafeObj(podmiot, "FlagaWlasna"), "Nazwa");
            string guardian = PobierzOpiekuna(podmiot);
            object bazaKlienta = SafeObj(klient, "BazaDanych");
            bool? rachmistrzActive = SafeBool(bazaKlienta, "AktywnyRachmistrz");
            bool? rewizorActive = SafeBool(bazaKlienta, "AktywnyRewizor");
            bool? gratyfikantActive = SafeBool(bazaKlienta, "AktywnyGratyfikant");
            var danePolaczenia = PobierzDanePolaczeniaBazy(bazaKlienta);

            return new OfficeVatFlagsItem
            {
                ClientId = SafeInt(klient, "Id"),
                Nip = nip,
                NormalizedNip = NormalizujIdPodatkowy(nip),
                Name = SafeString(firma, "Nazwa"),
                ShortName = SafeString(podmiot, "NazwaSkrocona"),
                Active = SafeBool(klient, "Aktywny"),
                IsVatPayer = groupNames.Any(x => string.Equals(x?.Trim(), "VAT", StringComparison.OrdinalIgnoreCase)),
                IsVatUePayer = string.Equals(vatUeFlagName?.Trim(), "VAT-UE", StringComparison.OrdinalIgnoreCase),
                GroupNames = groupNames,
                VatUeFlagName = vatUeFlagName,
                NipUe = nipUe,
                AlwaysUseNipUe = SafeBool(podmiot, "ZawszeStosujNIPUE"),
                SmeVatPayer = SafeBool(firma, "PodatnikSme"),
                Guardian = guardian,
                AccountingProgram = OkreslProgramKsiegowy(rachmistrzActive, rewizorActive),
                DatabaseName = danePolaczenia.DatabaseName,
                DatabaseServerName = danePolaczenia.ServerName,
                RachmistrzActive = rachmistrzActive,
                RewizorActive = rewizorActive,
                GratyfikantActive = gratyfikantActive,
                AccountingFormCode = SafeInt(klient, "FormaKsiegowosci")
            };
        }

        private void UzupelnijMapowanieBaz(OfficeVatFlagsReport report)
        {
            report.DatabaseMappings.Clear();
            report.DatabaseMappings.AddRange(report.Items
                .Where(x => !string.IsNullOrWhiteSpace(x.NormalizedNip))
                .GroupBy(x => x.NormalizedNip)
                .Select(g => g.First())
                .Select(x => new OfficeDatabaseNameMapItem
                {
                    ClientId = x.ClientId,
                    Nip = x.Nip,
                    NormalizedNip = x.NormalizedNip,
                    Name = x.Name,
                    ShortName = x.ShortName,
                    Active = x.Active,
                    DatabaseName = x.DatabaseName,
                    DatabaseServerName = x.DatabaseServerName,
                    AccountingProgram = x.AccountingProgram,
                    RachmistrzActive = x.RachmistrzActive,
                    RewizorActive = x.RewizorActive,
                    GratyfikantActive = x.GratyfikantActive
                }));
        }

        private void DodajDiagnostykeOdczytuBaz(OfficeVatFlagsReport report)
        {
            int missingDatabaseName = report.DatabaseMappings.Count(x => string.IsNullOrWhiteSpace(x.DatabaseName));
            if (missingDatabaseName == 0)
            {
                return;
            }

            string diagnostic = $"Diagnostyka odczytu nazw baz: brakNazwyBazy={missingDatabaseName}, brakObiektuBazy={_databaseConnectionMissingBase}, brakId={_databaseConnectionMissingId}, brakDanePolaczenia={_databaseConnectionMissingBytes}, brakResolvera={_databaseConnectionMissingResolver}, resolverZeSrodowiska={_databaseConnectionResolverFromEnvironment}, pustyWynikResolvera={_databaseConnectionEmptyResult}, wyjatki={_databaseConnectionExceptions}, pierwszyWyjatek={_databaseConnectionFirstException ?? "brak"}.";
            if (!report.Warnings.Contains(diagnostic))
            {
                report.Warnings.Add(diagnostic);
            }

            _logger.LogWarning("[OFFICE DATABASE NAMES DIAG] {Diagnostic}", diagnostic);
        }

        private DatabaseConnectionInfo PobierzDanePolaczeniaBazy(object bazaKlienta)
        {
            var result = new DatabaseConnectionInfo();
            if (bazaKlienta == null)
            {
                _databaseConnectionMissingBase++;
                return result;
            }

            try
            {
                Guid? databaseId = SafeGuid(bazaKlienta, "Id");
                Guid? officeDatabaseId = SafeGuid(bazaKlienta, "IdDlaBiura");
                byte[] dataBytes = SafeBytes(bazaKlienta, "DanePolaczenia");
                object converter = PobierzKonwerterNazwyBazyDanych();

                if (!databaseId.HasValue || dataBytes == null || dataBytes.Length == 0 || converter == null)
                {
                    if (!databaseId.HasValue)
                    {
                        _databaseConnectionMissingId++;
                    }

                    if (dataBytes == null || dataBytes.Length == 0)
                    {
                        _databaseConnectionMissingBytes++;
                    }

                    if (converter == null)
                    {
                        _databaseConnectionMissingResolver++;
                    }

                    return result;
                }

                result.DatabaseName = OdczytajNazweBazyPrzezKonwerter(converter, databaseId.Value, dataBytes);
                if (string.IsNullOrWhiteSpace(result.DatabaseName) && officeDatabaseId.HasValue && officeDatabaseId.Value != databaseId.Value)
                {
                    result.DatabaseName = OdczytajNazweBazyPrzezKonwerter(converter, officeDatabaseId.Value, dataBytes);
                }

                if (string.IsNullOrWhiteSpace(result.DatabaseName))
                {
                    _databaseConnectionEmptyResult++;
                }
            }
            catch (Exception ex)
            {
                _databaseConnectionExceptions++;
                _databaseConnectionFirstException ??= ex.GetBaseException().Message;
                _logger.LogDebug(ex, "Nie udalo sie odczytac danych polaczenia bazy klienta Biura: {Message}", ex.GetBaseException().Message);
            }

            return result;
        }

        private object PobierzKonwerterNazwyBazyDanych()
        {
            if (_databaseNameConverterChecked)
            {
                return _databaseNameConverter;
            }

            _databaseNameConverterChecked = true;
            object resolver = PobierzResolverDanychPolaczenia();
            if (resolver == null)
            {
                return null;
            }

            try
            {
                var assembly = ZaladujAssembly("InsERT.Moria.API.UI");
                var converterType = assembly?.GetType("InsERT.Moria.KlienciBiura.UI.DanePolaczeniaToNazwaBazyDanychConverter");
                _databaseNameConverter = converterType == null
                    ? null
                    : Activator.CreateInstance(converterType, resolver, ProductDatabaseNamePrefix);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nie udalo sie utworzyc konwertera nazwy bazy klienta Biura: {Message}", ex.GetBaseException().Message);
                _databaseNameConverter = null;
            }

            return _databaseNameConverter;
        }

        private string OdczytajNazweBazyPrzezKonwerter(object converter, Guid databaseId, byte[] dataBytes)
        {
            try
            {
                object value = converter.GetType()
                    .GetMethod("Convert")
                    ?.Invoke(converter, new object[] { new object[] { databaseId, dataBytes }, typeof(string), ProductDatabaseNamePrefix, System.Globalization.CultureInfo.InvariantCulture });

                return ZapewnijPelnaNazweBazy(value?.ToString());
            }
            catch (Exception ex)
            {
                _databaseConnectionExceptions++;
                _databaseConnectionFirstException ??= ex.GetBaseException().Message;
                _logger.LogDebug(ex, "Nie udalo sie skonwertowac danych polaczenia klienta Biura na nazwe bazy: {Message}", ex.GetBaseException().Message);
                return null;
            }
        }

        private string ZapewnijPelnaNazweBazy(string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                return null;
            }

            return databaseName.StartsWith(ProductDatabaseNamePrefix, StringComparison.OrdinalIgnoreCase)
                ? databaseName
                : ProductDatabaseNamePrefix + databaseName;
        }
        private object PobierzResolverDanychPolaczenia()
        {
            if (_connectionDataResolverChecked)
            {
                return _connectionDataResolver;
            }

            _connectionDataResolverChecked = true;
            _connectionDataResolver =
                PobierzOpcjonalnegoMenedzera("IConnectionDataResolver") ??
                PobierzUslugeZKontenera("IConnectionDataResolver") ??
                UtworzResolverDanychPolaczeniaZFabryki() ??
                UtworzResolverDanychPolaczeniaZParametrowSrodowiska();

            return _connectionDataResolver;
        }

        private object UtworzResolverDanychPolaczeniaZFabryki()
        {
            try
            {
                object dbConnectionFactory = PobierzUslugeZKontenera("IDbConnectionFactory");
                if (dbConnectionFactory == null)
                {
                    return null;
                }

                var assembly = ZaladujAssembly("InsERT.Moria.KlienciBiura");
                var scramblerType = assembly?.GetType("InsERT.Moria.KlienciBiura.ConnectionDataScrambler");
                return scramblerType == null ? null : Activator.CreateInstance(scramblerType, dbConnectionFactory);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nie udalo sie utworzyc ConnectionDataScrambler dla danych polaczenia klientow Biura: {Message}", ex.GetBaseException().Message);
                return null;
            }
        }

        private object UtworzResolverDanychPolaczeniaZParametrowSrodowiska()
        {
            try
            {
                string server = Environment.GetEnvironmentVariable("DB_SERVER");
                string dbUser = Environment.GetEnvironmentVariable("DB_USER");
                string dbPass = Environment.GetEnvironmentVariable("DB_PASS");

                if (string.IsNullOrWhiteSpace(server) ||
                    string.IsNullOrWhiteSpace(_officeDatabaseName) ||
                    string.IsNullOrWhiteSpace(dbUser))
                {
                    return null;
                }

                var dbAccessAssembly = ZaladujAssembly("InsERT.Mox.DatabaseAccess");
                var sqlLoginInfoType = dbAccessAssembly?.GetType("InsERT.Mox.DatabaseAccess.SqlLoginInfo");
                var sqlConnectionFactoryType = dbAccessAssembly?.GetType("InsERT.Mox.DatabaseAccess.SqlConnectionFactory");
                if (sqlLoginInfoType == null || sqlConnectionFactoryType == null)
                {
                    return null;
                }

                object sqlLoginInfo = Activator.CreateInstance(sqlLoginInfoType, server, _officeDatabaseName, dbUser, dbPass);
                object dbConnectionFactory = Activator.CreateInstance(sqlConnectionFactoryType, sqlLoginInfo);

                var klientBiuraAssembly = ZaladujAssembly("InsERT.Moria.KlienciBiura");
                var scramblerType = klientBiuraAssembly?.GetType("InsERT.Moria.KlienciBiura.ConnectionDataScrambler");
                object resolver = scramblerType == null ? null : Activator.CreateInstance(scramblerType, dbConnectionFactory);
                if (resolver != null)
                {
                    _databaseConnectionResolverFromEnvironment++;
                }

                return resolver;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nie udalo sie utworzyc ConnectionDataScrambler z parametrow srodowiska: {Message}", ex.GetBaseException().Message);
                return null;
            }
        }

        private object PobierzUslugeZKontenera(string nazwaInterfejsu)
        {
            try
            {
                Type typSzukany = ZnajdzTypInterfejsu(nazwaInterfejsu);
                object kontener = PobierzKontenerSfery();
                if (typSzukany == null || kontener == null)
                {
                    return null;
                }

                var getObject = kontener.GetType()
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "GetObject" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));

                return getObject?.Invoke(kontener, new object[] { typSzukany });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nie udalo sie pobrac uslugi {Service} z kontenera Sfery: {Message}", nazwaInterfejsu, ex.GetBaseException().Message);
                return null;
            }
        }

        private object PobierzKontenerSfery()
        {
            try
            {
                for (Type type = _sfera.GetType(); type != null; type = type.BaseType)
                {
                    var container = type
                        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Select(f => f.GetValue(_sfera))
                        .FirstOrDefault(v => v != null && v.GetType().GetInterfaces().Any(i => i.FullName == "InsERT.Mox.Runtime.IInjectionContainer"));

                    if (container != null)
                    {
                        return container;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private string OkreslProgramKsiegowy(bool? rachmistrzActive, bool? rewizorActive)
        {
            bool rachmistrz = rachmistrzActive == true;
            bool rewizor = rewizorActive == true;

            if (rachmistrz && rewizor)
            {
                return "Rachmistrz+Rewizor";
            }

            if (rewizor)
            {
                return "Rewizor";
            }

            if (rachmistrz)
            {
                return "Rachmistrz";
            }

            return null;
        }
        private List<string> PobierzNazwyGrup(object podmiot)
        {
            var result = new List<string>();
            object grupy = SafeObj(podmiot, "Grupy");
            if (grupy is not IEnumerable enumerable || grupy is string)
            {
                return result;
            }

            foreach (var grupa in enumerable)
            {
                string nazwa = SafeString(grupa, "Nazwa")?.Trim();
                if (!string.IsNullOrWhiteSpace(nazwa))
                {
                    result.Add(nazwa);
                }
            }

            return result;
        }

        private string PobierzOpiekuna(object podmiot)
        {
            try
            {
                object opiekunPodstawowy = SafeObj(podmiot, "OpiekunPodstawowy");
                if (opiekunPodstawowy == null)
                {
                    return null;
                }

                object uzytkownik = SafeObj(opiekunPodstawowy, "Uzytkownik");
                if (uzytkownik == null)
                {
                    return null;
                }

                object osoba = SafeObj(uzytkownik, "Osoba");
                if (osoba == null)
                {
                    return null;
                }

                string imie = SafeString(osoba, "Imie")?.Trim();
                string nazwisko = SafeString(osoba, "Nazwisko")?.Trim();

                string guardian = string.Join(" ", new[]
                {
            imie,
            nazwisko
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

                return string.IsNullOrWhiteSpace(guardian) ? null : guardian;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Nie uda�o si� pobra� opiekuna dla podmiotu ID {Id}: {Message}",
                    SafeInt(podmiot, "Id"),
                    ex.GetBaseException().Message);

                return null;
            }
        }

        private dynamic PobierzMenedzera(string nazwaInterfejsu)
        {
            var typSzukany = ZnajdzTypInterfejsu(nazwaInterfejsu);
            if (typSzukany == null)
            {
                return null;
            }

            var metoda = _sfera.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "PodajObiektTypu" && m.IsGenericMethod && m.GetParameters().Length == 0);

            return metoda?.MakeGenericMethod(typSzukany).Invoke(_sfera, null);
        }

        private object PobierzOpcjonalnegoMenedzera(string nazwaInterfejsu)
        {
            try
            {
                return PobierzMenedzera(nazwaInterfejsu);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nie udalo sie pobrac opcjonalnego menedzera {Manager}: {Message}", nazwaInterfejsu, ex.GetBaseException().Message);
                return null;
            }
        }

        private Type ZnajdzTypInterfejsu(string nazwa)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = ZnajdzTypInterfejsu(assembly, nazwa);
                if (found != null)
                {
                    return found;
                }
            }

            foreach (string assemblyName in new[]
            {
                "InsERT.Moria.API.Private",
                "InsERT.Moria.API",
                "InsERT.Moria.KlienciBiura",
                "InsERT.Mox.DatabaseAccess",
                "InsERT.Mox.Core"
            })
            {
                var assembly = ZaladujAssembly(assemblyName);
                var found = ZnajdzTypInterfejsu(assembly, nazwa);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private Type ZnajdzTypInterfejsu(Assembly assembly, string nazwa)
        {
            if (assembly == null ||
                assembly.FullName.StartsWith("System.") ||
                assembly.FullName.StartsWith("Microsoft."))
            {
                return null;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                return null;
            }

            return types.FirstOrDefault(t => t != null && t.IsInterface && t.Name == nazwa);
        }

        private Assembly ZaladujAssembly(string assemblyName)
        {
            try
            {
                return Assembly.Load(assemblyName);
            }
            catch
            {
                string baseDirectory = AppContext.BaseDirectory;
                string localPath = System.IO.Path.Combine(baseDirectory, assemblyName + ".dll");
                if (System.IO.File.Exists(localPath))
                {
                    try
                    {
                        return Assembly.LoadFrom(localPath);
                    }
                    catch { }
                }

                string nexoPath = Environment.GetEnvironmentVariable("NEXO_BIN_PATH");
                if (!string.IsNullOrWhiteSpace(nexoPath))
                {
                    string nexoDllPath = System.IO.Path.Combine(nexoPath, assemblyName + ".dll");
                    if (System.IO.File.Exists(nexoDllPath))
                    {
                        try
                        {
                            return Assembly.LoadFrom(nexoDllPath);
                        }
                        catch { }
                    }
                }
            }

            return null;
        }

        private object SafeObj(object obj, string propertyName)
        {
            if (obj == null)
            {
                return null;
            }

            try
            {
                return obj.GetType().GetProperty(propertyName)?.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        private string SafeString(object obj, string propertyName)
        {
            var value = SafeObj(obj, propertyName);
            return value == null ? null : value.ToString();
        }

        private int? SafeInt(object obj, string propertyName)
        {
            var value = SafeObj(obj, propertyName);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return null;
            }
        }

        private bool? SafeBool(object obj, string propertyName)
        {
            var value = SafeObj(obj, propertyName);
            if (value == null)
            {
                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (bool.TryParse(value.ToString(), out var parsedBool))
            {
                return parsedBool;
            }

            if (int.TryParse(value.ToString(), out var parsedInt))
            {
                return parsedInt != 0;
            }

            return null;
        }

        private Guid? SafeGuid(object obj, string propertyName)
        {
            var value = SafeObj(obj, propertyName);
            if (value == null)
            {
                return null;
            }

            if (value is Guid guidValue)
            {
                return guidValue;
            }

            if (Guid.TryParse(value.ToString(), out var parsedGuid))
            {
                return parsedGuid;
            }

            return null;
        }

        private byte[] SafeBytes(object obj, string propertyName)
        {
            return SafeObj(obj, propertyName) as byte[];
        }

        private sealed class DatabaseConnectionInfo
        {
            public string DatabaseName { get; set; }
            public string ServerName { get; set; }
        }

        private string NormalizujIdPodatkowy(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        }
    }
}
