using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using InsERT.Moria.Dokumenty.Logistyka;
using InsERT.Moria.Kasa;
using InsERT.Moria.Klienci;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using InsERT.Moria.Slowniki;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using PodmiotyDane = InsERT.Moria.Klienci.IPodmiotyDane;
using PodmiotyManager = InsERT.Mox.ObiektyBiznesowe.IObiektyBiznesowe<InsERT.Moria.Klienci.IPodmiot, InsERT.Moria.ModelDanych.Podmiot, InsERT.Moria.Klienci.IPodmiotyDane>;
using static NexoBridge.Services.SferaReflectionHelpers;

namespace NexoBridge.Services
{
    /// <summary>
    /// Tworzenie faktury sprzedaży w Subiekcie/nexo przez Sferę, na podstawie pozycji dostarczonych przez
    /// wywołującego (pozycje cykliczne z billing-snapshot połączone z usługami jednorazowymi z bazy
    /// NexoBillingKonsoli - to łączenie dzieje się poza NexoBridge). Port CreateInvoiceDraft i pokrewnych
    /// metod z prototypu NexoBillingKonsola/Program.cs.
    /// </summary>
    public class InvoiceCreationService
    {
        private readonly Uchwyt _sfera;
        private readonly ILogger<InvoiceCreationService> _logger;

        public InvoiceCreationService(Uchwyt sfera, ILogger<InvoiceCreationService> logger)
        {
            _sfera = sfera;
            _logger = logger;
        }

        public async Task<InvoiceCreationReport> UtworzFaktureAsync(InvoiceCreationJob job, Func<int, string, Task> raportujPostep)
        {
            var report = new InvoiceCreationReport
            {
                JobId = job.JobId,
                Status = "SUCCESS",
                Message = "Faktura utworzona.",
                DatabaseName = job.DatabaseName,
                Nip = job.Nip
            };

            try
            {
                if (job.Lines == null || job.Lines.Count == 0)
                {
                    report.Status = "FAILED";
                    report.Message = "Zlecenie nie zawiera żadnych pozycji faktury.";
                    await raportujPostep(100, report.Message);
                    return report;
                }

                if (!string.Equals(job.PaymentMethod, "Card", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(job.PaymentMethod, "Transfer", StringComparison.OrdinalIgnoreCase))
                {
                    report.Status = "FAILED";
                    report.Message = $"Nieprawidłowa lub brakująca metoda płatności: '{job.PaymentMethod}'. Oczekiwano 'Card' albo 'Transfer'.";
                    await raportujPostep(100, report.Message);
                    return report;
                }

                await raportujPostep(10, "Odczyt podmiotów...");
                PodmiotyManager podmiotyManager = GetPodmiotyManager(_sfera, DateTime.Today);
                PodmiotyDane podmiotyDane = GetManagerDataOrContainer<PodmiotyDane>(_sfera, podmiotyManager, "IPodmioty.Dane");
                List<Podmiot> allClients = LoadClients(podmiotyDane);

                await raportujPostep(25, "Wyszukiwanie klienta po NIP...");
                Podmiot client = FindClientByNip(allClients, job.Nip);
                if (client == null)
                {
                    report.Status = "NOT_FOUND";
                    report.Message = $"Nie znaleziono aktywnego klienta biura z cechą „Do fakturowania” o NIP {job.Nip}.";
                    await raportujPostep(100, report.Message);
                    return report;
                }

                await raportujPostep(45, "Tworzenie dokumentu sprzedaży...");
                DateTime issueDate = DateTime.Today;
                DateTime serviceMonthStart = new(job.ServiceYear, job.ServiceMonth, 1);
                DateTime saleDate = GetSaleDate(serviceMonthStart, issueDate);

                IDokumentySprzedazy documents = GetRequiredService<IDokumentySprzedazy>(_sfera, issueDate);
                IDokumentSprzedazy invoice = documents.UtworzFaktureSprzedazy();

                ConfigureInvoiceDates(invoice, issueDate, saleDate);
                invoice.PodmiotyDokumentu.UstawNabywceWedlugId(client.Id);
                invoice.PodmiotyDokumentu.UstawPlatnikaWedlugId(client.Id);
                ConfigureInvoiceForKsef(invoice);

                await raportujPostep(65, "Dodawanie pozycji faktury...");
                Dictionary<int, StawkaVat> vatRatesByPercent = job.Lines.Any(line => line.VatRate.HasValue)
                    ? LoadVatRatesByPercent()
                    : new Dictionary<int, StawkaVat>();
                foreach (InvoiceLineRequest line in job.Lines)
                {
                    AddInvoiceLine(invoice, line, vatRatesByPercent, report.Warnings);
                }

                PaymentConfiguration payment = ResolvePaymentConfigurationForMethod(job.PaymentMethod);
                ConfigureInvoicePayment(invoice, payment);

                await raportujPostep(85, "Zapis dokumentu...");
                SaveBusinessObject(invoice);
                report.InvoiceSaved = true;

                // Pełny numer (np. "FS 12/10/2026") to Sygnatura.PelnaSygnatura na DokumentDS - zweryfikowane refleksją
                // na ModelDanych; wcześniejsze "Dane.NumerPelny"/"Dane.Numer" nie istnieją i zwracały null.
                report.InvoiceNumber = ReadStringCandidate(invoice, "Dane.NumerWewnetrzny.PelnaSygnatura", "Dane.NumerPelny", "Dane.Numer", "NumerPelny", "Numer");
                report.InvoiceId = ReadIntCandidate(invoice, "Dane.Id", "Id");
                // Kwota, jaką Subiekt faktycznie policzył na zapisanym dokumencie - wywołujący porównuje ją z kwotą
                // pobraną z karty (bezpiecznik na każdą rozbieżność: stawka VAT, zaokrąglenia, brak brutto).
                report.InvoiceGross = ReadDecimalCandidate(invoice, "Dane.Wartosc.BruttoPoRabacie", "Wartosc.BruttoPoRabacie");
                report.Message = $"Utworzono fakturę {report.InvoiceNumber ?? report.InvoiceId?.ToString() ?? "(brak numeru)"} - zapisana lokalnie w nexo, bez wysyłki do KSeF.";

                await raportujPostep(100, report.Message);
                return report;
            }
            catch (Exception ex)
            {
                string message = ex.GetBaseException().Message;
                _logger.LogError(ex, "Nie udało się utworzyć faktury dla NIP={Nip}, zlecenie {JobId}.", job.Nip, job.JobId);
                report.Status = "FAILED";
                report.Message = "Błąd tworzenia faktury: " + message;
                report.Warnings.Add(report.Message);
                await raportujPostep(100, $"BŁĄD: {message}");
                return report;
            }
        }

        /// <summary>
        /// Data dostawy/wykonania usługi = data wystawienia, także przy rozliczaniu poprzedniego miesiąca (decyzja
        /// biura, 2026-10-05: "data wystawienia i dostawy = dniu wystawienia"). Wcześniej dla poprzedniego miesiąca
        /// była to ostatnia data tego miesiąca. Miesiąc usługi z przyszłości nadal jest odrzucany.
        /// </summary>
        private static DateTime GetSaleDate(DateTime serviceMonthStart, DateTime issueDate)
        {
            DateTime issueMonthStart = new(issueDate.Year, issueDate.Month, 1);
            if (serviceMonthStart > issueMonthStart)
            {
                throw new InvalidOperationException($"Miesiąc usługi {serviceMonthStart:yyyy-MM} jest w przyszłości względem daty wystawienia {issueDate:yyyy-MM-dd}.");
            }

            return issueDate;
        }

        private static void ConfigureInvoiceDates(IDokumentSprzedazy invoice, DateTime issueDate, DateTime saleDate)
        {
            // DokumentDS nie ma pola DataWystawienia/DataDokumentu (zweryfikowane refleksją na ModelDanych) - data
            // wystawienia faktury to DataWydaniaWystawienia; stare nazwy zostają jako zapas na inną wersję Sfery.
            TrySetFirstPropertyPath(invoice, issueDate, "Dane.DataWydaniaWystawienia", "Dane.DataWystawienia", "Dane.DataDokumentu", "DataWystawienia", "DataDokumentu");
            TrySetFirstPropertyPath(invoice, saleDate, "Dane.DataSprzedazy", "DataSprzedazy");
        }

        /// <summary>
        /// Ustawia wyłącznie FORMĘ faktury (KSeF), żeby dało się ją potem wysłać z Subiekta. Celowo NIE
        /// wołamy invoice.ObslugaKSeF.WygenerujEFakture()/PrzekazDoWysylki() - faktura z billingu ma trafić
        /// do KSeF dopiero po ręcznym sprawdzeniu w Subiekcie. Uwaga: Subiekt może sam wysłać dokument, jeśli
        /// w Konfiguracji -> Parametry KSeF włączono generowanie e-Faktur "automatycznie przy zapisie" albo
        /// automatyczną (cykliczną) wysyłkę - to jest ustawienie nexo, nie tego kodu.
        /// </summary>
        private static void ConfigureInvoiceForKsef(IDokumentSprzedazy invoice)
        {
            if (!TrySetFirstPropertyPath(invoice, FormaFaktury.KSEF, "Dane.FormaFaktury", "FormaFaktury"))
            {
                TrySetFirstPropertyPath(invoice, (byte)FormaFaktury.KSEF, "Dane.FormaFaktury", "FormaFaktury");
            }

            EnsureKsefInvoiceKind(invoice);
        }

        private static void EnsureKsefInvoiceKind(IDokumentSprzedazy invoice)
        {
            if (!TryResolvePropertyPath(invoice, "Dane.RodzajFakturyKsef", out object owner, out PropertyInfo property)
                || owner == null
                || property == null
                || !property.CanWrite)
            {
                return;
            }

            object currentValue = SafeGetPropertyValue(owner, property);
            if (currentValue != null && !IsDefaultValue(currentValue))
            {
                return;
            }

            Type targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object newValue = GetDefaultEnumLikeValue(targetType);
            if (newValue == null)
            {
                return;
            }

            try
            {
                property.SetValue(owner, newValue);
            }
            catch
            {
            }
        }

        private static object GetDefaultEnumLikeValue(Type type)
        {
            if (type.IsEnum)
            {
                Array values = Enum.GetValues(type);
                if (values.Length == 0)
                {
                    return null;
                }

                foreach (object value in values)
                {
                    string name = Enum.GetName(type, value) ?? string.Empty;
                    if (name.Contains("PODSTAW", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("BAZOW", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("ZWYK", StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }
                }

                return values.GetValue(0);
            }

            if (type == typeof(byte))
            {
                return (byte)0;
            }

            if (type == typeof(short))
            {
                return (short)0;
            }

            if (type == typeof(int))
            {
                return 0;
            }

            return null;
        }

        /// <summary>
        /// Polskie stawki VAT ze słownika nexo, kluczem procent (23 -> symbol "23", Stawka 0.23). Celowo pomija
        /// "zw"/"nieop."/"npo" - mają Stawka=0 tak jak "0", więc po samej wartości byłyby nieodróżnialne;
        /// procent 0 oznacza wyłącznie stawkę "0" (Stawka VAT 0%). Słownik zweryfikowany na bazie biura
        /// (NexoBillingKonsola --dump-vat-rates): symbole unikalne, brak stawek innych państw.
        /// </summary>
        private Dictionary<int, StawkaVat> LoadVatRatesByPercent()
        {
            IStawkiVat manager = GetRequiredService<IStawkiVat>(_sfera, DateTime.Today);
            IStawkiVatDane dane = GetManagerDataOrContainer<IStawkiVatDane>(_sfera, manager, "IStawkiVat.Dane");

            var result = new Dictionary<int, StawkaVat>();
            foreach (StawkaVat rate in dane.Wszystkie(Array.Empty<string>()).ToList())
            {
                if (rate.Zwolniona || rate.NiePodlegaOpodatkowaniu || rate.IsInRecycleBin)
                {
                    continue;
                }

                decimal percent = rate.Stawka * 100m;
                if (percent != decimal.Truncate(percent)
                    || !string.Equals(rate.Symbol?.Trim(), ((int)percent).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    continue;
                }

                // Dwie stawki o tym samym procencie i symbolu - nie zgadujemy, pozycja dostanie ostrzeżenie.
                if (!result.TryAdd((int)percent, rate))
                {
                    result[(int)percent] = null;
                }
            }

            return result;
        }

        private static void AddInvoiceLine(IDokumentSprzedazy invoice, InvoiceLineRequest line, Dictionary<int, StawkaVat> vatRatesByPercent, List<string> warnings)
        {
            if (!line.NetAmount.HasValue && !line.GrossAmount.HasValue)
            {
                throw new InvalidOperationException($"Pozycja `{line.Description}` nie ma żadnej kwoty netto ani brutto.");
            }

            // Ilość i jednostka (np. 3 "szt") - cena na pozycji to wtedy cena ZA JEDNOSTKĘ, a wartość (cena x ilość)
            // liczy Subiekt. Bez ilości/jednostki: 1 x wartość pozycji i jednostka domyślna Subiekta, jak dotąd.
            decimal quantity = line.Quantity is > 0 ? line.Quantity.Value : 1m;
            decimal? unitNet = line.UnitNetAmount ?? (line.NetAmount.HasValue ? line.NetAmount.Value / quantity : null);
            decimal? unitGross = line.GrossAmount.HasValue ? line.GrossAmount.Value / quantity : null;

            object position = CreateOneOffServicePosition(invoice.Pozycje, line.Description, quantity, line.Unit);

            TrySetFirstPropertyPath(position, line.Description, "Opis");
            TrySetFirstPropertyPath(position, true, "CenaRecznieEdytowana");

            if (line.VatRate.HasValue && unitNet.HasValue)
            {
                // Najpierw nawigacja (Sfera od razu przelicza cenę), a gdyby encja słownika była z innego kontekstu
                // niż dokument i przypisanie się nie udało - klucz obcy. Skutek i tak weryfikuje porównanie
                // InvoiceGross z kwotą pobraną z karty po stronie wywołującego.
                if (vatRatesByPercent.TryGetValue(line.VatRate.Value, out StawkaVat rate)
                    && rate != null
                    && (TrySetFirstPropertyPath(position, rate, "StawkaVat") || TrySetFirstPropertyPath(position, rate.Id, "StawkaVatId")))
                {
                    // Stawka ustawiona jawnie - podajemy TYLKO cenę netto za jednostkę, brutto liczy Subiekt z tej
                    // stawki. Ustawienie obu cen nadpisywałoby jedną drugą przy domyślnej stawce Subiekta.
                    TrySetFirstPropertyPath(position, unitNet.Value, "Cena.NettoPrzedRabatem", "Cena.NettoPoRabacie");
                    return;
                }

                warnings.Add(
                    $"Pozycja `{line.Description}`: nie udało się ustawić stawki VAT {line.VatRate.Value}% ze słownika nexo - " +
                    "użyto domyślnej stawki Subiekta, sprawdź fakturę.");
            }

            if (unitNet.HasValue)
            {
                TrySetFirstPropertyPath(position, unitNet.Value, "Cena.NettoPrzedRabatem", "Cena.NettoPoRabacie");
            }

            if (unitGross.HasValue)
            {
                TrySetFirstPropertyPath(position, unitGross.Value, "Cena.BruttoPrzedRabatem", "Cena.BruttoPoRabacie");
            }
        }

        /// <summary>
        /// IPozycjeDokumentu.DodajUslugeJednorazowa(nazwa, ilosc[, symbolJednostkiMiary]) - przeciążenia zweryfikowane
        /// refleksją na InsERT.Moria.API. Z jednostką tylko, gdy wywołujący ją podał (symbol ze słownika nexo:
        /// szt/usl/godz/min - pilnuje tego panel); bez niej jednostka domyślna Subiekta, jak dotąd.
        /// </summary>
        private static object CreateOneOffServicePosition(IPozycjeDokumentu positions, string description, decimal quantity, string unitSymbol)
        {
            object created = string.IsNullOrWhiteSpace(unitSymbol)
                ? positions.DodajUslugeJednorazowa(description, quantity)
                : positions.DodajUslugeJednorazowa(description, quantity, unitSymbol.Trim());
            if (created == null)
            {
                throw new InvalidOperationException($"Sfera nie zwróciła nowej pozycji dla `{description}`.");
            }

            return created;
        }

        private static void ConfigureInvoicePayment(IDokumentSprzedazy invoice, PaymentConfiguration payment)
        {
            ClearInvoicePayments(invoice);

            if (payment.PaymentForm != null && invoice.Platnosci.CzyMoznaDodacPlatnosc(payment.PaymentForm))
            {
                if (payment.IsDeferred || (payment.TermDays ?? 0) > 0)
                {
                    invoice.Platnosci.DodajPlatnoscOdroczona(payment.PaymentForm);
                }
                else
                {
                    invoice.Platnosci.DodajPlatnoscNatychmiastowa(payment.PaymentForm);
                }

                return;
            }

            if (payment.TermDays is > 0)
            {
                invoice.Platnosci.DodajPlatnoscOdroczona(payment.TermDays.Value);
                return;
            }

            invoice.Platnosci.DodajDomyslnaPlatnoscNatychmiastowaNaKwoteDokumentu();
        }

        private static void ClearInvoicePayments(IDokumentSprzedazy invoice)
        {
            List<PlatnoscDokumentu> existingPayments = ReadObjectCollection(invoice, "Dane.PlatnosciDokumentow")
                .OfType<PlatnoscDokumentu>()
                .ToList();

            foreach (PlatnoscDokumentu payment in existingPayments)
            {
                invoice.Platnosci.Usun(payment);
            }

            invoice.Platnosci.UsunPlatnosciNieobslugiwane();
        }

        /// <summary>
        /// Rozwiązuje formę płatności na podstawie metody dostarczonej JAWNIE przez wywołującego
        /// ("Card"/"Transfer"), a nie odczytanej z domyślnej formy płatności konkretnego klienta.
        /// Szuka w globalnym słowniku form płatności (IFormyPlatnosci/IFormyPlatnosciDane - ten sam
        /// wzorzec menedżer+Dane co IPodmioty/IPodmiotyDane) formy o nazwie zawierającej "Karta" dla
        /// płatności kartą, albo "Odroczony"/"Przelew" dla przelewu.
        /// </summary>
        private PaymentConfiguration ResolvePaymentConfigurationForMethod(string paymentMethod)
        {
            IFormyPlatnosci manager = GetRequiredService<IFormyPlatnosci>(_sfera, DateTime.Today);
            IFormyPlatnosciDane dane = GetManagerDataOrContainer<IFormyPlatnosciDane>(_sfera, manager, "IFormyPlatnosci.Dane");
            List<FormaPlatnosci> allForms = LoadAllPaymentForms(dane);

            bool isCard = string.Equals(paymentMethod, "Card", StringComparison.OrdinalIgnoreCase);
            FormaPlatnosci form = isCard
                ? FindPaymentFormByNameContains(allForms, "KARTA", preferredExactName: "Karta płatnicza")
                : FindPaymentFormByNameContains(allForms, "ODROCZONY") ?? FindPaymentFormByNameContains(allForms, "PRZELEW");

            if (form == null)
            {
                throw new InvalidOperationException(
                    $"Nie znaleziono w słowniku form płatności nexo formy odpowiadającej metodzie '{paymentMethod}'.");
            }

            int? term = ReadIntCandidate(form, "TerminPlatnosci");
            bool? delayedFlag = ReadBoolCandidate(form, "TypPlatnosci.Odroczony");
            bool isDeferred = delayedFlag == true || term is > 0;

            return new PaymentConfiguration(
                PaymentForm: form,
                IsDeferred: isDeferred,
                TermDays: term,
                Share: null,
                Active: ReadBoolCandidate(form, "Aktywna"));
        }

        /// <summary>
        /// IFormyPlatnosciDane dziedziczy wyłącznie IDane&lt;FormaPlatnosci&gt;.Wszystkie(string[] razemZ)
        /// (zweryfikowane refleksją na DLL-ach Sfery) - nie ma bezparametrowych WszystkieDostepne()/Wszystkie(),
        /// których szukała wcześniejsza wersja przez refleksję na typie konkretnym. To kończyło się błędem
        /// tworzenia KAŻDEJ faktury (także już po pobraniu płatności kartą).
        /// </summary>
        private static List<FormaPlatnosci> LoadAllPaymentForms(IFormyPlatnosciDane dane)
        {
            try
            {
                return dane.Wszystkie(new[] { "TypPlatnosci" }).ToList();
            }
            catch
            {
                return dane.Wszystkie(Array.Empty<string>()).ToList();
            }
        }

        /// <summary>
        /// Najpierw forma o nazwie dokładnie równej `preferredExactName` (np. "Karta płatnicza" - ta sama, którą
        /// klienci mają ustawioną jako domyślną), potem dowolna zawierająca `nameFragment`. W obu krokach
        /// aktywne formy mają pierwszeństwo przed nieaktywnymi.
        /// </summary>
        private static FormaPlatnosci FindPaymentFormByNameContains(List<FormaPlatnosci> allForms, string nameFragment, string preferredExactName = null)
        {
            List<FormaPlatnosci> activeFirst = allForms.OrderByDescending(form => form.Aktywna).ToList();

            if (!string.IsNullOrWhiteSpace(preferredExactName))
            {
                string normalizedExact = NormalizeText(preferredExactName);
                FormaPlatnosci exact = activeFirst.FirstOrDefault(form =>
                    NormalizeText(ReadStringCandidate(form, "Nazwa") ?? string.Empty) == normalizedExact);
                if (exact != null)
                {
                    return exact;
                }
            }

            string normalizedFragment = NormalizeText(nameFragment);
            return activeFirst.FirstOrDefault(form =>
                NormalizeText(ReadStringCandidate(form, "Nazwa") ?? string.Empty).Contains(normalizedFragment));
        }

        private static void SaveBusinessObject(object businessObject)
        {
            MethodInfo saveMethod = businessObject.GetType().GetMethod(
                "Zapisz",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (saveMethod == null)
            {
                throw new MissingMethodException($"Obiekt {businessObject.GetType().FullName} nie wystawia metody Zapisz().");
            }

            object result = saveMethod.Invoke(businessObject, null);
            if (result is bool saved && !saved)
            {
                throw new InvalidOperationException($"Sfera odrzuciła zapis dokumentu. Szczegóły: {DescribeBusinessObjectIssues(businessObject)}");
            }
        }

        private static string DescribeBusinessObjectIssues(object businessObject)
        {
            foreach (string propertyPath in new[] { "Bledy", "Problemy", "Ostrzezenia", "Informacje" })
            {
                List<string> entries = ReadObjectCollection(businessObject, propertyPath)
                    .Select(entry => entry.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Take(5)
                    .ToList();

                if (entries.Count > 0)
                {
                    return string.Join(" | ", entries);
                }
            }

            return "brak dodatkowych informacji";
        }

        private sealed record PaymentConfiguration(
            FormaPlatnosci PaymentForm,
            bool IsDeferred,
            int? TermDays,
            decimal? Share,
            bool? Active);
    }
}
