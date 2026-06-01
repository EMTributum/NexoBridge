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

            if (report.Warnings.Any() && report.Status == "SUCCESS")
            {
                report.Status = "PARTIAL_SUCCESS";
                report.Message = "Odczytano flagi VAT/VAT-UE klientow Biura z ostrzezeniami.";
            }

            await raportujPostep(100, "Odczyt flag VAT/VAT-UE z Biura zakonczony.");
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
                    "KlientBiura"
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
                SmeVatPayer = SafeBool(firma, "PodatnikSme")
            };
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
                if (assembly.FullName.StartsWith("System.") || assembly.FullName.StartsWith("Microsoft."))
                {
                    continue;
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
                    continue;
                }

                var found = types.FirstOrDefault(t => t != null && t.IsInterface && t.Name == nazwa);
                if (found != null)
                {
                    return found;
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
