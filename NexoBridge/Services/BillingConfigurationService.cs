using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InsERT.Moria.Klienci;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using PodmiotyDane = InsERT.Moria.Klienci.IPodmiotyDane;
using PodmiotyManager = InsERT.Mox.ObiektyBiznesowe.IObiektyBiznesowe<InsERT.Moria.Klienci.IPodmiot, InsERT.Moria.ModelDanych.Podmiot, InsERT.Moria.Klienci.IPodmiotyDane>;
using static NexoBridge.Services.SferaReflectionHelpers;

namespace NexoBridge.Services
{
    /// <summary>
    /// Odczyt konfiguracji billingowej klienta (domyślne stawki + typ płatności) z Subiekta/nexo przez Sferę.
    /// Port logiki z prototypu NexoBillingKonsola/Program.cs (BuildClientBillingSnapshot i pokrewne), uogólniony
    /// tak, by zwracać dowolną liczbę pozycji cyklicznych zamiast sztywno księgowość+kadry.
    /// </summary>
    public class BillingConfigurationService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<BillingConfigurationService> _logger;

        public BillingConfigurationService(Uchwyt sfera, ILogger<BillingConfigurationService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task<BillingSnapshotReport> PobierzKonfiguracjeBillingowaAsync(BillingSnapshotJob job, Func<int, string, Task> raportujPostep)
        {
            var report = new BillingSnapshotReport
            {
                JobId = job.JobId,
                Status = "SUCCESS",
                Message = "Odczytano konfigurację billingową klienta.",
                DatabaseName = job.DatabaseName,
                Nip = job.Nip
            };

            try
            {
                await raportujPostep(20, "Odczyt podmiotów...");
                PodmiotyManager podmiotyManager = GetPodmiotyManager(_sfera, DateTime.Today);
                PodmiotyDane podmiotyDane = GetManagerDataOrContainer<PodmiotyDane>(_sfera, podmiotyManager, "IPodmioty.Dane");
                List<Podmiot> allClients = LoadClients(podmiotyDane);

                await raportujPostep(60, "Wyszukiwanie klienta po NIP...");
                Podmiot client = FindClientByNip(allClients, job.Nip);
                if (client == null)
                {
                    report.Status = "NOT_FOUND";
                    report.Message = $"Nie znaleziono aktywnego klienta z cechą „Do fakturowania” o NIP {job.Nip}.";
                    await raportujPostep(100, report.Message);
                    return report;
                }

                await raportujPostep(80, "Odczyt stawek i formy płatności...");
                report.Item = BuildSnapshotItem(client);

                await raportujPostep(100, "Odczyt konfiguracji billingowej zakończony.");
                return report;
            }
            catch (Exception ex)
            {
                string message = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "Nie udało się odczytać konfiguracji billingowej klienta NIP={Nip}.", job.Nip);
                report.Status = "FAILED";
                report.Message = "Błąd odczytu konfiguracji billingowej: " + message;
                report.Warnings.Add(report.Message);
                await raportujPostep(100, $"BŁĄD: {message}");
                return report;
            }
        }

        /// <summary>
        /// Skanuje WSZYSTKICH Podmiotów Biura (wzorem OfficeVatFlagsService) i zwraca tylko tych
        /// kwalifikujących się do billingu (aktywny + cecha "Do fakturowania") - jedyne źródło prawdy
        /// o tym, kogo w ogóle rozliczamy. Wywołujący nie musi utrzymywać własnej listy klientów.
        /// </summary>
        public async Task<BillingClientsReport> PobierzListeKlientowAsync(BillingClientsJob job, Func<int, string, Task> raportujPostep)
        {
            var report = new BillingClientsReport
            {
                JobId = job.JobId,
                Status = "SUCCESS",
                Message = "Odczytano listę klientów do rozliczenia.",
                DatabaseName = job.DatabaseName
            };

            try
            {
                await raportujPostep(20, "Odczyt podmiotów...");
                PodmiotyManager podmiotyManager = GetPodmiotyManager(_sfera, DateTime.Today);
                PodmiotyDane podmiotyDane = GetManagerDataOrContainer<PodmiotyDane>(_sfera, podmiotyManager, "IPodmioty.Dane");
                List<Podmiot> allClients = LoadClients(podmiotyDane);

                await raportujPostep(70, "Filtrowanie aktywnych klientów z cechą „Do fakturowania”...");
                List<Podmiot> eligibleClients = FindEligibleClients(allClients);

                report.Items = eligibleClients
                    .Select(client => new BillingClientListItem
                    {
                        Nip = ReadStringCandidate(client, "NIP", "Nip"),
                        Name = GetDisplayName(client),
                        Active = ReadBoolCandidate(client, "Aktywny"),
                        DoFakturowania = HasFeature(client, "Do fakturowania")
                    })
                    .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                await raportujPostep(100, $"Odczytano {report.Items.Count} klientów do rozliczenia.");
                return report;
            }
            catch (Exception ex)
            {
                string message = ex.GetBaseException().Message;
                _logger.LogWarning(ex, "Nie udało się odczytać listy klientów do rozliczenia.");
                report.Status = "FAILED";
                report.Message = "Błąd odczytu listy klientów: " + message;
                report.Warnings.Add(report.Message);
                await raportujPostep(100, $"BŁĄD: {message}");
                return report;
            }
        }

        private ClientBillingSnapshotItem BuildSnapshotItem(Podmiot client)
        {
            BestPaymentEntry bestPayment = ResolveBestPaymentEntry(client);
            (bool isDeferred, int? termDays, string summary) = ResolvePaymentSummary(client, bestPayment);
            (string paymentMethod, string paymentMethodSource) = ResolvePaymentMethod(bestPayment);

            MonthlyFeeLine baseFee = FindPrimaryMonthlyServiceLine(client, MonthlyServiceKind.Accounting);
            MonthlyFeeLine payrollFee = FindPrimaryMonthlyServiceLine(client, MonthlyServiceKind.Payroll);

            return new ClientBillingSnapshotItem
            {
                ClientId = ReadIntCandidate(client, "Id"),
                Nip = ReadStringCandidate(client, "NIP", "Nip"),
                Name = GetDisplayName(client),
                Active = ReadBoolCandidate(client, "Aktywny"),
                DoFakturowania = HasFeature(client, "Do fakturowania"),
                Payment = new PaymentConfigurationDto
                {
                    PaymentMethod = paymentMethod,
                    PaymentMethodSource = paymentMethodSource,
                    IsDeferred = isDeferred,
                    TermDays = termDays,
                    Summary = summary
                },
                BaseFeeName = baseFee?.Name,
                BaseFeeNet = baseFee?.Net,
                BaseFeeGross = baseFee?.Gross,
                PayrollFeeName = payrollFee?.Name,
                PayrollFeeNet = payrollFee?.Net,
                PayrollFeeGross = payrollFee?.Gross
            };
        }

        private static string GetDisplayName(Podmiot client)
        {
            string name = ReadStringCandidate(client, "Nazwa", "PelnaNazwa", "NazwaSkrocona");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            string firstName = ReadStringCandidate(client, "Osoba.Imie");
            string lastName = ReadStringCandidate(client, "Osoba.Nazwisko");
            string fullName = string.Join(" ", new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
        }

        private static BestPaymentEntry ResolveBestPaymentEntry(Podmiot client)
        {
            return ReadObjectCollection(client, "DomyslneFormyPlatnosci")
                .Select(entry => new BestPaymentEntry(
                    Term: ReadIntCandidate(entry, "FormaPlatnosci.TerminPlatnosci"),
                    Deferred: ReadBoolCandidate(entry, "FormaPlatnosci.TypPlatnosci.Odroczony"),
                    Bankowy: ReadBoolCandidate(entry, "FormaPlatnosci.TypPlatnosci.Bankowy"),
                    Gotowkowy: ReadBoolCandidate(entry, "FormaPlatnosci.TypPlatnosci.Gotowkowy"),
                    FormName: ReadStringCandidate(entry, "FormaPlatnosci.Nazwa", "FormaPlatnosci.SkrotDlaPlatnosci.Nazwa"),
                    Share: ReadDecimalCandidate(entry, "Procent"),
                    Active: ReadBoolCandidate(entry, "FormaPlatnosci.Aktywna"),
                    Summary: DescribeDefaultPayment(entry)))
                .Where(entry => entry.Summary != null)
                .OrderByDescending(entry => entry.Share ?? 0m)
                .ThenByDescending(entry => entry.Active != false)
                .FirstOrDefault();
        }

        /// <summary>
        /// Klasyfikacja karta/przelew - Faza 0 planu, zweryfikowana na realnych danych produkcyjnych
        /// (--dump-payment-fields w NexoBillingKonsola). Hipoteza "Odroczony = karta" okazała się błędna -
        /// ta sama flaga Odroczony=True występuje zarówno u formy "Przelew", jak i "Odroczony 7 dni".
        /// Realny, jednoznaczny sygnał to nazwa domyślnej formy płatności: firma ma osobną, wprost nazwaną
        /// formę płatności "Karta płatnicza" (odrębna kombinacja flag Kasowy+Cesyjny, ale to nazwa jest
        /// pewnym sygnałem, nie te flagi). Brak dopasowania - domyślnie przelew.
        /// </summary>
        private static (string PaymentMethod, string Source) ResolvePaymentMethod(BestPaymentEntry best)
        {
            if (best == null)
            {
                return ("Transfer", "Default");
            }

            if (!string.IsNullOrWhiteSpace(best.FormName) && NormalizeText(best.FormName).Contains("KARTA"))
            {
                return ("Card", "FormaPlatnosciNazwa");
            }

            return ("Transfer", "Default");
        }

        private static (bool IsDeferred, int? TermDays, string Summary) ResolvePaymentSummary(Podmiot client, BestPaymentEntry best)
        {
            if (best != null)
            {
                bool isDeferred = best.Deferred == true || best.Term is > 0;
                return (isDeferred, best.Term, best.Summary);
            }

            int? salesPaymentTerm = ReadIntCandidate(client, "TerminPlatnosciSprzedaz");
            return (salesPaymentTerm is > 0, salesPaymentTerm, salesPaymentTerm.HasValue ? $"{salesPaymentTerm.Value} dni" : null);
        }

        private sealed record BestPaymentEntry(
            int? Term,
            bool? Deferred,
            bool? Bankowy,
            bool? Gotowkowy,
            string FormName,
            decimal? Share,
            bool? Active,
            string Summary);

        private static string DescribeDefaultPayment(object paymentEntry)
        {
            string name = ReadStringCandidate(
                paymentEntry,
                "FormaPlatnosci.Nazwa",
                "FormaPlatnosci.SkrotDlaPlatnosci.Nazwa");
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            List<string> parts = new();

            int? term = ReadIntCandidate(paymentEntry, "FormaPlatnosci.TerminPlatnosci");
            if (term.HasValue)
            {
                parts.Add($"termin={term.Value} dni");
            }

            decimal? percent = ReadDecimalCandidate(paymentEntry, "Procent");
            if (percent.HasValue && percent.Value > 0)
            {
                parts.Add($"udział={percent.Value:0.##}%");
            }

            bool? active = ReadBoolCandidate(paymentEntry, "FormaPlatnosci.Aktywna");
            if (active == false)
            {
                parts.Add("nieaktywna");
            }

            return parts.Count == 0
                ? name
                : $"{name} ({string.Join(", ", parts)})";
        }

        /// <summary>
        /// Dokładnie dwie nazwane kwoty (bazowe rozliczenie księgowe + opcjonalnie kadrowe), zgodnie z
        /// oryginalnym prototypem NexoBillingKonsola/Program.cs (FindPrimaryMonthlyServiceLine i pokrewne) -
        /// cofnięcie wcześniejszej generalizacji do dowolnej listy pozycji cyklicznych.
        /// </summary>
        private static MonthlyFeeLine FindPrimaryMonthlyServiceLine(Podmiot client, MonthlyServiceKind kind)
        {
            if (!TryReadPropertyPath(client, "KlientBiura", out object biuroClient) || biuroClient == null)
            {
                return null;
            }

            List<MonthlyFeeLine> matches = new();

            MonthlyFeeLine fixedFee = GetMatchingFixedFeeLine(biuroClient, kind);
            if (fixedFee != null)
            {
                matches.Add(fixedFee);
            }
            else if (kind == MonthlyServiceKind.Accounting)
            {
                MonthlyFeeLine accountingFallback = GetAccountingFixedFeeFallbackLine(biuroClient);
                if (accountingFallback != null)
                {
                    matches.Add(accountingFallback);
                }
            }

            if (TryReadPropertyPath(biuroClient, "CennikUslug", out object pricing) && pricing != null)
            {
                matches.AddRange(GetMatchingServiceFeeLines(pricing, kind));

                MonthlyFeeLine pricingFallback = GetPricingCatalogFallbackLine(pricing, kind);
                if (pricingFallback != null)
                {
                    matches.Add(pricingFallback);
                }
            }

            return matches.FirstOrDefault(line => line.HasAmount);
        }

        private static MonthlyFeeLine GetMatchingFixedFeeLine(object biuroClient, MonthlyServiceKind kind)
        {
            string fixedFeeName = ReadStringCandidate(biuroClient, "NazwaStawkiStalej", "CennikUslug.NazwaStawkiStalej");
            if (!MatchesMonthlyServiceKind(fixedFeeName, kind))
            {
                return null;
            }

            decimal? net = ReadDecimalCandidate(biuroClient, "StawkaStalaNetto", "CennikUslug.StawkaStalaNetto");
            decimal? gross = ReadDecimalCandidate(biuroClient, "StawkaStalaBrutto", "CennikUslug.StawkaStalaBrutto");
            if (!net.HasValue && !gross.HasValue)
            {
                return null;
            }

            return new MonthlyFeeLine(
                string.IsNullOrWhiteSpace(fixedFeeName) ? GetDefaultServiceName(kind) : fixedFeeName,
                net,
                gross);
        }

        private static MonthlyFeeLine GetAccountingFixedFeeFallbackLine(object biuroClient)
        {
            decimal? net = ReadDecimalCandidate(biuroClient, "StawkaStalaNetto", "CennikUslug.StawkaStalaNetto");
            decimal? gross = ReadDecimalCandidate(biuroClient, "StawkaStalaBrutto", "CennikUslug.StawkaStalaBrutto");
            if (!net.HasValue && !gross.HasValue)
            {
                return null;
            }

            bool? fromCatalog = ReadBoolCandidate(biuroClient, "StawkaStalaWgCennika");
            if (fromCatalog == true)
            {
                return null;
            }

            string fixedFeeName = ReadStringCandidate(biuroClient, "NazwaStawkiStalej", "CennikUslug.NazwaStawkiStalej");
            return new MonthlyFeeLine(
                string.IsNullOrWhiteSpace(fixedFeeName) ? GetDefaultServiceName(MonthlyServiceKind.Accounting) : fixedFeeName,
                net,
                gross);
        }

        private static MonthlyFeeLine GetPricingCatalogFallbackLine(object pricing, MonthlyServiceKind kind)
        {
            string pricingName = ReadStringCandidate(pricing, "Nazwa");
            if (!MatchesMonthlyServiceKind(pricingName, kind))
            {
                return null;
            }

            decimal? net = ReadDecimalCandidate(pricing, "StawkaStalaNetto");
            decimal? gross = ReadDecimalCandidate(pricing, "StawkaStalaBrutto");
            if (!net.HasValue && !gross.HasValue)
            {
                return null;
            }

            return new MonthlyFeeLine(
                string.IsNullOrWhiteSpace(pricingName) ? GetDefaultServiceName(kind) : pricingName,
                net,
                gross);
        }

        private static IEnumerable<MonthlyFeeLine> GetMatchingServiceFeeLines(object pricing, MonthlyServiceKind kind)
        {
            foreach (object position in ReadObjectCollection(pricing, "PozycjeCennikaUslug"))
            {
                string label = GetPositionLabel(position);
                string searchText = string.Join(" ", new[]
                {
                    ReadStringCandidate(position, "NazwaDoWydruku"),
                    ReadStringCandidate(position, "ObiektPozycjiCennikaUslug.UslugaKsiegowa.Nazwa"),
                    ReadStringCandidate(position, "ObiektPozycjiCennikaUslug.UslugaKsiegowa.Symbol"),
                    ReadStringCandidate(position, "ObiektPozycjiCennikaUslug.Nazwa")
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

                if (!MatchesMonthlyServiceKind(searchText, kind))
                {
                    continue;
                }

                (decimal? net, decimal? gross) = GetPrimaryPositionFeeAmounts(position);
                if (!net.HasValue && !gross.HasValue)
                {
                    continue;
                }

                yield return new MonthlyFeeLine(
                    string.IsNullOrWhiteSpace(label) ? GetDefaultServiceName(kind) : label,
                    net,
                    gross);
            }
        }

        private static string GetPositionLabel(object position)
        {
            return ReadStringCandidate(
                position,
                "NazwaDoWydruku",
                "ObiektPozycjiCennikaUslug.UslugaKsiegowa.Nazwa",
                "ObiektPozycjiCennikaUslug.UslugaKsiegowa.Symbol",
                "ObiektPozycjiCennikaUslug.Nazwa");
        }

        private static (decimal? Net, decimal? Gross) GetPrimaryPositionFeeAmounts(object position)
        {
            foreach (object priceValue in ReadObjectCollection(position, "WartosciPozycjiCennikaUslug"))
            {
                decimal? net = ReadDecimalCandidate(priceValue, "CenaZbiorczaWPrzedzialeNetto", "CenaZbiorczaNetto", "CenaJednostkowaNetto");
                decimal? gross = ReadDecimalCandidate(priceValue, "CenaZbiorczaWPrzedzialeBrutto", "CenaZbiorczaBrutto", "CenaJednostkowaBrutto");
                if (!net.HasValue && !gross.HasValue)
                {
                    continue;
                }

                return (net, gross);
            }

            return (
                ReadDecimalCandidate(position, "CenaNetto"),
                ReadDecimalCandidate(position, "CenaBrutto"));
        }

        private static bool MatchesMonthlyServiceKind(string value, MonthlyServiceKind kind)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = NormalizeText(value);
            string[] tokens = kind == MonthlyServiceKind.Accounting
                ? new[] { "KSIEG", "KSIEGOW", "KSIEGOWOSC", "KPIR", "KR", "RYCZALT", "EWIDENCJA", "ZPIK", "PELNA KSIEGOWOSC" }
                : new[] { "KADR", "PLAC", "PLACA", "WYNAGRODZEN", "PRACOWNIK", "UMOWA", "HR" };

            string[] words = normalized.Split(
                new[] { ' ', '-', '_', '/', '\\', ',', ';', '.', ':', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries);

            return tokens.Any(token =>
                token.Length <= 3
                    ? words.Contains(token, StringComparer.Ordinal)
                    : normalized.Contains(token, StringComparison.Ordinal));
        }

        private static string GetDefaultServiceName(MonthlyServiceKind kind)
        {
            return kind == MonthlyServiceKind.Accounting ? "Obsługa księgowa" : "Obsługa kadrowa";
        }

        private sealed record MonthlyFeeLine(string Name, decimal? Net, decimal? Gross)
        {
            public bool HasAmount => Net.HasValue || Gross.HasValue;
        }

        private enum MonthlyServiceKind
        {
            Accounting,
            Payroll
        }
    }
}
