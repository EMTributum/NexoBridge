using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using InsERT.Moria.Dokumenty.Logistyka;
using InsERT.Moria.Kasa;
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
                    report.Message = $"Nie znaleziono aktywnego klienta z cechą „Do fakturowania” o NIP {job.Nip}.";
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
                foreach (InvoiceLineRequest line in job.Lines)
                {
                    AddInvoiceLine(invoice, line);
                }

                PaymentConfiguration payment = ResolvePaymentConfigurationForMethod(job.PaymentMethod);
                ConfigureInvoicePayment(invoice, payment);

                await raportujPostep(85, "Zapis dokumentu...");
                SaveBusinessObject(invoice);

                report.InvoiceNumber = ReadStringCandidate(invoice, "Dane.NumerPelny", "Dane.Numer", "NumerPelny", "Numer");
                report.InvoiceId = ReadIntCandidate(invoice, "Dane.Id", "Id");
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

        private static DateTime GetSaleDate(DateTime serviceMonthStart, DateTime issueDate)
        {
            DateTime issueMonthStart = new(issueDate.Year, issueDate.Month, 1);
            if (serviceMonthStart > issueMonthStart)
            {
                throw new InvalidOperationException($"Miesiąc usługi {serviceMonthStart:yyyy-MM} jest w przyszłości względem daty wystawienia {issueDate:yyyy-MM-dd}.");
            }

            if (serviceMonthStart == issueMonthStart)
            {
                return issueDate;
            }

            return serviceMonthStart.AddMonths(1).AddDays(-1);
        }

        private static void ConfigureInvoiceDates(IDokumentSprzedazy invoice, DateTime issueDate, DateTime saleDate)
        {
            TrySetFirstPropertyPath(invoice, issueDate, "Dane.DataWystawienia", "Dane.DataDokumentu", "DataWystawienia", "DataDokumentu");
            TrySetFirstPropertyPath(invoice, saleDate, "Dane.DataSprzedazy", "DataSprzedazy");
        }

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

        private static void AddInvoiceLine(IDokumentSprzedazy invoice, InvoiceLineRequest line)
        {
            if (!line.NetAmount.HasValue && !line.GrossAmount.HasValue)
            {
                throw new InvalidOperationException($"Pozycja `{line.Description}` nie ma żadnej kwoty netto ani brutto.");
            }

            object position = CreateOneOffServicePosition(invoice.Pozycje, line.Description);

            TrySetFirstPropertyPath(position, line.Description, "Opis");
            TrySetFirstPropertyPath(position, true, "CenaRecznieEdytowana");

            if (line.NetAmount.HasValue)
            {
                TrySetFirstPropertyPath(position, line.NetAmount.Value, "Cena.NettoPrzedRabatem", "Cena.NettoPoRabacie");
            }

            if (line.GrossAmount.HasValue)
            {
                TrySetFirstPropertyPath(position, line.GrossAmount.Value, "Cena.BruttoPrzedRabatem", "Cena.BruttoPoRabacie");
            }
        }

        private static object CreateOneOffServicePosition(IPozycjeDokumentu positions, string description)
        {
            MethodInfo addMethod = positions.GetType().GetMethod(
                "DodajUslugeJednorazowa",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(string), typeof(decimal) },
                modifiers: null);

            if (addMethod == null)
            {
                throw new MissingMethodException("Nie udało się odnaleźć metody DodajUslugeJednorazowa(string, decimal).");
            }

            object created = addMethod.Invoke(positions, new object[] { description, 1m });
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
                ? FindPaymentFormByNameContains(allForms, "KARTA")
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

        private static List<FormaPlatnosci> LoadAllPaymentForms(IFormyPlatnosciDane dane)
        {
            string[] candidateMethodNames = { "WszystkieDostepne", "Wszystkie" };
            foreach (string methodName in candidateMethodNames)
            {
                MethodInfo method = dane.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

                if (method == null)
                {
                    continue;
                }

                try
                {
                    object result = method.Invoke(dane, null);
                    if (result is IEnumerable<FormaPlatnosci> typedResult)
                    {
                        return typedResult.ToList();
                    }

                    if (result is System.Collections.IEnumerable rawResult)
                    {
                        return rawResult.Cast<FormaPlatnosci>().ToList();
                    }
                }
                catch
                {
                }
            }

            throw new MissingMethodException(
                "Nie udało się odnaleźć metody do pobrania wszystkich form płatności na IFormyPlatnosciDane (próbowano: "
                + string.Join(", ", candidateMethodNames) + ").");
        }

        private static FormaPlatnosci FindPaymentFormByNameContains(List<FormaPlatnosci> allForms, string nameFragment)
        {
            string normalizedFragment = NormalizeText(nameFragment);
            return allForms.FirstOrDefault(form =>
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
