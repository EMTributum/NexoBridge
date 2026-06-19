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

            await raportujPostep(30, "Odczyt klientow i nazw baz danych z Biura...");
            var items = PobierzKlientowBiura(report)
                .OrderBy(x => x.Name ?? x.ShortName ?? x.Nip)
                .ThenBy(x => x.Nip)
                .ToList();

            report.Items.AddRange(items);
            UzupelnijMapowanieBaz(report);

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
                })).Cast<dynamic>();
            }
            catch
            {
                return ((IEnumerable)mgrPodmioty.Dane.Wszystkie()).Cast<dynamic>();
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

        private DatabaseConnectionInfo PobierzDanePolaczeniaBazy(object bazaKlienta)
        {
            var result = new DatabaseConnectionInfo();
            if (bazaKlienta == null)
            {
                return result;
            }

            try
            {
                Guid? databaseId = SafeGuid(bazaKlienta, "Id");
                byte[] dataBytes = SafeBytes(bazaKlienta, "DanePolaczenia");
                object resolver = PobierzResolverDanychPolaczenia();

                if (!databaseId.HasValue || dataBytes == null || dataBytes.Length == 0 || resolver == null)
                {
                    return result;
                }

                object loginInfo = resolver.GetType().GetMethod("Read")?.Invoke(resolver, new object[] { databaseId.Value, dataBytes });
                result.DatabaseName = SafeString(loginInfo, "DatabaseName");
                result.ServerName = SafeString(loginInfo, "ServerName");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nie udalo sie odczytac danych polaczenia bazy klienta Biura: {Message}", ex.GetBaseException().Message);
            }

            return result;
        }

        private object PobierzResolverDanychPolaczenia()
        {
            if (_connectionDataResolverChecked)
            {
                return _connectionDataResolver;
            }

            _connectionDataResolverChecked = true;
            _connectionDataResolver = PobierzMenedzera("IConnectionDataResolver");
            return _connectionDataResolver;
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

            foreach (string assemblyName in new[] { "InsERT.Moria.API.Private", "InsERT.Moria.API", "InsERT.Moria.KlienciBiura" })
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
