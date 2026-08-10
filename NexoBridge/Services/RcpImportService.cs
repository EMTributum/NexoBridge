using InsERT.Moria.Kadry.Duze;
using InsERT.Moria.Klienci;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using InsERT.Mox.DataAccess.EntityFramework;
using InsERT.Mox.Product;
using Microsoft.Extensions.Logging;
using NexoBridge.Models;
using PodmiotyDane = InsERT.Moria.Klienci.IPodmiotyDane;
using PodmiotyManager = InsERT.Mox.ObiektyBiznesowe.IObiektyBiznesowe<InsERT.Moria.Klienci.IPodmiot, InsERT.Moria.ModelDanych.Podmiot, InsERT.Moria.Klienci.IPodmiotyDane>;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ZapisyDane = InsERT.Moria.Kadry.Duze.IZapisyWECPDane;
using ZapisyManager = InsERT.Mox.ObiektyBiznesowe.IObiektyBiznesowe<InsERT.Moria.Kadry.Duze.IZapisWECP, InsERT.Moria.ModelDanych.ZapisWECP, InsERT.Moria.Kadry.Duze.IZapisyWECPDane>;

namespace NexoBridge.Services
{
    public sealed class RcpImportService
    {
        private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");

        private readonly RcpEmployeeMappingStore _mappingStore;
        private readonly ILogger<RcpImportService> _logger;

        public RcpImportService(
            RcpEmployeeMappingStore mappingStore,
            ILogger<RcpImportService> logger)
        {
            _mappingStore = mappingStore;
            _logger = logger;
        }

        public async Task<RcpImportReport> ImportAsync(
            Uchwyt sfera,
            RcpImportJob job,
            Func<int, string, Task> reportProgress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sfera);
            ArgumentNullException.ThrowIfNull(job);

            RcpTimesheetPayload payload = job.Payload ?? throw new InvalidOperationException("Brak payloadu RCP.");
            payload.EmployeesTimesheets ??= new List<RcpEmployeeTimesheet>();

            var report = new RcpImportReport
            {
                JobId = job.JobId,
                DatabaseName = job.DatabaseName,
                PeriodYear = job.PeriodYear,
                PeriodMonth = job.PeriodMonth,
                SourceMode = job.SourceMode,
                SourceUrl = job.SourceUrl,
                PayloadHash = job.PayloadHash,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EmployeesReceivedCount = payload.EmployeesTimesheets.Count,
                ShiftsReceivedCount = payload.EmployeesTimesheets.Sum(employee => employee?.Shifts?.Count ?? 0),
                Status = "PENDING",
                Message = "Import RCP zostal rozpoczęty."
            };

            await ReportProgressAsync(reportProgress, 5, "Pobieram mapę pracowników RCP...");

            RcpEmployeeDatabaseMapping databaseMapping = await _mappingStore.GetDatabaseAsync(job.DatabaseName, cancellationToken)
                ?? new RcpEmployeeDatabaseMapping { DatabaseName = job.DatabaseName };

            Dictionary<string, RcpEmployeeMapItem> mappings = (databaseMapping.Employees ?? new List<RcpEmployeeMapItem>())
                .Where(employee => employee != null && !string.IsNullOrWhiteSpace(employee.EmployeeId))
                .ToDictionary(employee => employee.EmployeeId, StringComparer.OrdinalIgnoreCase);

            if (report.ShiftsReceivedCount == 0)
            {
                report.Status = "SUCCESS";
                report.Message = "Payload RCP nie zawiera zmian do zaimportowania.";
                report.FinishedAtUtc = DateTimeOffset.UtcNow;
                await ReportProgressAsync(reportProgress, 100, report.Message);
                return report;
            }

            int totalShifts = Math.Max(1, report.ShiftsReceivedCount);
            int processedShifts = 0;

            foreach (RcpEmployeeTimesheet employee in payload.EmployeesTimesheets
                .Where(employee => employee != null)
                .OrderBy(employee => employee.EmployeeId, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var employeeResult = new RcpEmployeeImportResult
                {
                    EmployeeId = employee.EmployeeId?.Trim(),
                    ShiftsReceivedCount = employee.Shifts?.Count ?? 0
                };

                report.Employees.Add(employeeResult);

                if (string.IsNullOrWhiteSpace(employeeResult.EmployeeId))
                {
                    employeeResult.Status = "SKIPPED";
                    employeeResult.Message = "Pominieto rekord bez employeeId.";
                    employeeResult.Warnings.Add(employeeResult.Message);
                    report.EmployeesSkippedCount++;
                    report.Warnings.Add(employeeResult.Message);
                    continue;
                }

                if (!mappings.TryGetValue(employeeResult.EmployeeId, out RcpEmployeeMapItem mapping))
                {
                    employeeResult.Status = "SKIPPED";
                    employeeResult.Message = $"Brak mapowania employeeId={employeeResult.EmployeeId}. Uzupełnij PESEL w employee-mappings.json.";
                    employeeResult.Warnings.Add(employeeResult.Message);
                    report.EmployeesSkippedCount++;
                    report.Warnings.Add(employeeResult.Message);
                    _logger.LogWarning(
                        "Pominieto import RCP dla employeeId={EmployeeId} w bazie {DatabaseName} - brak mapowania.",
                        employeeResult.EmployeeId,
                        job.DatabaseName);
                    continue;
                }

                employeeResult.Pesel = mapping.Pesel;
                employeeResult.WorkerName = mapping.WorkerName;

                if (string.IsNullOrWhiteSpace(mapping.Pesel))
                {
                    employeeResult.Status = "SKIPPED";
                    employeeResult.Message = $"Mapowanie employeeId={employeeResult.EmployeeId} nie ma PESEL. Uzupełnij mapę i uruchom import ponownie.";
                    employeeResult.Warnings.Add(employeeResult.Message);
                    report.EmployeesSkippedCount++;
                    report.Warnings.Add(employeeResult.Message);
                    _logger.LogWarning(
                        "Pominieto import RCP dla employeeId={EmployeeId} w bazie {DatabaseName} - brak PESEL w mapie.",
                        employeeResult.EmployeeId,
                        job.DatabaseName);
                    continue;
                }

                foreach (RcpShiftPayload shift in (employee.Shifts ?? new List<RcpShiftPayload>())
                    .Where(shift => shift != null)
                    .OrderBy(shift => shift.Date, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(shift => shift.StartTime, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var shiftResult = new RcpShiftImportResult
                    {
                        Date = shift.Date?.Trim(),
                        StartTime = shift.StartTime?.Trim(),
                        EndTime = shift.EndTime?.Trim(),
                        Status = "PENDING"
                    };
                    employeeResult.Shifts.Add(shiftResult);

                    try
                    {
                        DateTime shiftDate = ParseShiftDate(shiftResult.Date);
                        TimeSpan startTime = ParseShiftTime(shiftResult.StartTime, "startTime");
                        TimeSpan endTime = ParseShiftTime(shiftResult.EndTime, "endTime");

                        ValidateShift(job, shiftDate, startTime, endTime);

                        await ReportProgressAsync(
                            reportProgress,
                            CalculateProgress(++processedShifts, totalShifts),
                            $"Importuję {employeeResult.WorkerName ?? employeeResult.EmployeeId} za {FormatDate(shiftDate)}...");

                        ApplyShift(
                            sfera,
                            mapping.Pesel,
                            mapping.WorkerName,
                            shiftDate,
                            startTime,
                            endTime);

                        shiftResult.Status = "SUCCESS";
                        shiftResult.Message = $"Zapisano {FormatTime(startTime)}-{FormatTime(endTime)}.";
                        employeeResult.ShiftsImportedCount++;
                        report.ShiftsImportedCount++;
                    }
                    catch (Exception ex)
                    {
                        string message = ex.GetBaseException().Message;
                        shiftResult.Status = "FAILED";
                        shiftResult.Message = message;
                        employeeResult.Warnings.Add(message);
                        report.Warnings.Add(message);
                        report.ShiftsFailedCount++;
                        _logger.LogError(
                            ex,
                            "Nie udalo sie zaimportowac zmiany RCP dla employeeId={EmployeeId}, PESEL={Pesel}, data={Date}.",
                            employeeResult.EmployeeId,
                            mapping.Pesel,
                            shiftResult.Date);
                    }
                }

                if (employeeResult.ShiftsImportedCount == employeeResult.Shifts.Count && employeeResult.Shifts.Count > 0)
                {
                    employeeResult.Status = "SUCCESS";
                    employeeResult.Message = "Wszystkie zmiany pracownika zostały zapisane.";
                    report.EmployeesImportedCount++;
                }
                else if (employeeResult.ShiftsImportedCount > 0)
                {
                    employeeResult.Status = "PARTIAL_SUCCESS";
                    employeeResult.Message = "Część zmian pracownika została zapisana.";
                    report.EmployeesImportedCount++;
                }
                else
                {
                    employeeResult.Status = "FAILED";
                    employeeResult.Message = employeeResult.Shifts.Count == 0
                        ? "Brak zmian pracownika do zapisania."
                        : "Nie udało się zapisać żadnej zmiany pracownika.";
                }
            }

            report.FinishedAtUtc = DateTimeOffset.UtcNow;
            report.Status = ResolveFinalStatus(report);
            report.Message = BuildSummaryMessage(report);
            await ReportProgressAsync(reportProgress, 100, report.Message);
            return report;
        }

        private void ApplyShift(
            Uchwyt sfera,
            string pesel,
            string expectedName,
            DateTime targetDate,
            TimeSpan startTime,
            TimeSpan endTime)
        {
            PodmiotyManager podmiotyManager = GetPodmiotyManager(sfera, targetDate);
            PodmiotyDane podmiotyDane = GetManagerDataOrContainer<PodmiotyDane>(sfera, podmiotyManager, "IPodmioty.Dane");
            Podmiot pracownikPodmiot = FindEmployeeByPesel(podmiotyDane, pesel, expectedName);

            ZapisyManager zapisyManager = GetZapisyManager(sfera, targetDate);
            ZapisyDane zapisyDane = GetManagerDataOrContainer<ZapisyDane>(sfera, zapisyManager, "IZapisyWECP.Dane");
            List<ZapisWECP> zapisyPrzedZmiana = GetEntriesForDay(zapisyDane, pesel, targetDate);
            Pracownik pracownik = GetEmployeeModel(podmiotyDane, pracownikPodmiot, zapisyPrzedZmiana);

            ApplyHoursThroughHarmonogram(sfera, pracownik, zapisyDane, pesel, targetDate, startTime, endTime);
        }

        private Podmiot FindEmployeeByPesel(PodmiotyDane podmiotyDane, string pesel, string expectedName)
        {
            List<Podmiot> candidates = podmiotyDane.WszyscyPracownicy()
                .Where(p => p.Osoba != null && p.Osoba.PESEL == pesel)
                .ToList();

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException($"Nie znaleziono pracownika o PESEL {pesel}.");
            }

            if (candidates.Count > 1)
            {
                string found = string.Join("; ", candidates.Select(FormatEmployee));
                throw new InvalidOperationException($"PESEL {pesel} zwrócił wiele rekordów: {found}.");
            }

            Podmiot employee = candidates[0];
            string actualName = FormatEmployee(employee);
            if (!string.IsNullOrWhiteSpace(expectedName) &&
                !string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Mapowanie PESEL={Pesel} wskazuje na pracownika '{ActualName}', a w mapie zapisano '{ExpectedName}'. Kontynuuję po PESEL.",
                    pesel,
                    actualName,
                    expectedName);
            }

            return employee;
        }

        private void ApplyHoursThroughHarmonogram(
            Uchwyt sfera,
            Pracownik pracownik,
            ZapisyDane zapisyDane,
            string pesel,
            DateTime targetDate,
            TimeSpan startTime,
            TimeSpan endTime)
        {
            IMenadzerHarmonogramuECP menadzer = GetHarmonogramManager(sfera, targetDate);
            object umowa = FindActiveContractOnDay(sfera, pracownik, targetDate);
            umowa = PrepareContractForHarmonogram(sfera, umowa, targetDate);

            menadzer.DataOd = targetDate;
            menadzer.DataDo = targetDate;
            menadzer.Zakres = HarmonogramECPZakresDanych.Wykonanie;
            menadzer.AktualizujModyfikowaneWSumarycznejECP = true;

            ClearCollection(menadzer.UmowyPracownicze);
            AddToCollection(menadzer.UmowyPracownicze, umowa);

            menadzer.Inicjalizuj();
            TryFillFromContract(menadzer, umowa);

            object dzien = FindHarmonogramDay(menadzer.DniHarmonogramu, targetDate);
            TimeSpan czasPracy = endTime - startTime;

            SetTimeProperty(dzien, "Przepracowane", czasPracy);
            SetTimeProperty(dzien, "PrzepracowaneNocne", TimeSpan.Zero);
            SetTimeProperty(dzien, "GodzinaRozpoczeciaPracy", startTime);
            SetTimeProperty(dzien, "GodzinaZakonczeniaPracy", endTime);

            InvokeSave(menadzer);

            List<ZapisWECP> potwierdzenie = GetEntriesForDay(zapisyDane, pesel, targetDate);
            if (potwierdzenie.Count == 0 || potwierdzenie[0].Godziny == null)
            {
                throw new InvalidOperationException("Po zapisie przez IMenadzerHarmonogramuECP nie udało się odczytać wpisu ECP z godzinami.");
            }
        }

        private PodmiotyManager GetPodmiotyManager(Uchwyt sfera, DateTime dataOperacji)
        {
            try
            {
                return (PodmiotyManager)UchwytRozszerzenia.Podmioty(sfera);
            }
            catch
            {
                return GetRequiredService<PodmiotyManager>(sfera, dataOperacji);
            }
        }

        private ZapisyManager GetZapisyManager(Uchwyt sfera, DateTime dataOperacji)
        {
            try
            {
                return (ZapisyManager)UchwytRozszerzenia.ZapisyWECP(sfera);
            }
            catch
            {
                return GetRequiredService<ZapisyManager>(sfera, dataOperacji);
            }
        }

        private IMenadzerHarmonogramuECP GetHarmonogramManager(Uchwyt sfera, DateTime dataOperacji)
        {
            try
            {
                return UchwytRozszerzenia.MenadzerHarmonogramuECP(sfera);
            }
            catch
            {
                return GetRequiredService<IMenadzerHarmonogramuECP>(sfera, dataOperacji);
            }
        }

        private TData GetManagerDataOrContainer<TData>(Uchwyt sfera, object manager, string label)
            where TData : class
        {
            PropertyInfo daneProperty = manager.GetType().GetProperty("Dane", BindingFlags.Instance | BindingFlags.Public);
            if (daneProperty != null)
            {
                try
                {
                    if (daneProperty.GetValue(manager) is TData data)
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

        private T GetRequiredService<T>(Uchwyt sfera, DateTime dataOperacji)
            where T : class
        {
            T service = TryGetServiceFromSfera<T>(sfera, dataOperacji)
                ?? TryGetServiceFromContainer<T>(sfera);

            return service ?? throw new InvalidOperationException($"Nie udało się pobrać {typeof(T).FullName} ze Sfery.");
        }

        private T TryGetOptionalService<T>(Uchwyt sfera, DateTime dataOperacji)
            where T : class
        {
            return TryGetServiceFromSfera<T>(sfera, dataOperacji)
                ?? TryGetServiceFromContainer<T>(sfera);
        }

        private T TryGetServiceFromSfera<T>(Uchwyt sfera, DateTime dataOperacji)
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

                TrySetSystemDateContext(typed, dataOperacji);
                return typed;
            }
            catch
            {
                return null;
            }
        }

        private T TryGetServiceFromContainer<T>(Uchwyt sfera)
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

        private void TrySetSystemDateContext(object service, DateTime date)
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

        private InsERT.Mox.Runtime.IInjectionContainer GetSferaContainer(Uchwyt sfera)
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

        private Pracownik GetEmployeeModel(PodmiotyDane podmiotyDane, Podmiot pracownikPodmiot, IReadOnlyCollection<ZapisWECP> zapisyDnia)
        {
            if (pracownikPodmiot.Osoba?.Pracownik != null)
            {
                return pracownikPodmiot.Osoba.Pracownik;
            }

            Pracownik fromEntries = zapisyDnia
                .Select(zapis => zapis.Pracownik)
                .FirstOrDefault(pracownik => pracownik != null);
            if (fromEntries != null)
            {
                return fromEntries;
            }

            string pesel = pracownikPodmiot.Osoba?.PESEL
                ?? throw new InvalidOperationException("Znaleziony podmiot pracownika nie ma PESEL.");

            Pracownik fromExtendedQuery = podmiotyDane.WszystkieDostepne(new[] { "Osoba", "Osoba.Pracownik" })
                .Where(p => p.Osoba != null && p.Osoba.PESEL == pesel)
                .Select(p => p.Osoba.Pracownik)
                .FirstOrDefault(pracownik => pracownik != null);

            return fromExtendedQuery ?? throw new InvalidOperationException("Nie udało się odczytać encji Pracownik dla znalezionego podmiotu.");
        }

        private List<ZapisWECP> GetEntriesForDay(ZapisyDane zapisyDane, string pesel, DateTime data)
        {
            return zapisyDane.WszystkieDostepne(new[] { "Pracownik", "Pracownik.Osoba", "Godziny", "Umowy" })
                .Where(zapis =>
                    zapis.Typ == (byte)TypZapisuWECP.Godziny &&
                    zapis.Pracownik != null &&
                    zapis.Pracownik.Osoba != null &&
                    zapis.Pracownik.Osoba.PESEL == pesel &&
                    zapis.Okres != null &&
                    zapis.Okres.DataPoczatkowa.Date == data.Date &&
                    zapis.Okres.DataKoncowa.Date == data.Date)
                .ToList();
        }

        private object FindActiveContractOnDay(Uchwyt sfera, Pracownik pracownik, DateTime data)
        {
            LoadEmployeeRelationships(sfera, pracownik, data);

            List<object> contracts = new List<object>();
            if (pracownik.UmowyPracownicze is IEnumerable collection)
            {
                foreach (object contract in collection)
                {
                    if (contract != null)
                    {
                        contracts.Add(contract);
                    }
                }
            }

            if (contracts.Count == 0)
            {
                throw new InvalidOperationException($"Pracownik Id={pracownik.Id} nie ma żadnych umów pracowniczych.");
            }

            List<object> activeContracts = contracts
                .Where(contract => IsContractActiveOnDate(contract, data))
                .ToList();

            if (activeContracts.Count == 1)
            {
                return activeContracts[0];
            }

            if (activeContracts.Count > 1)
            {
                string found = string.Join("; ", activeContracts.Select(FormatContract));
                throw new InvalidOperationException($"Znaleziono wiele aktywnych umów pracownika na {FormatDate(data)}: {found}.");
            }

            if (contracts.Count == 1)
            {
                _logger.LogWarning(
                    "Nie znaleziono formalnie aktywnej umowy na {Date}. Używam jedynej dostępnej: {Contract}.",
                    FormatDate(data),
                    FormatContract(contracts[0]));
                return contracts[0];
            }

            string available = string.Join("; ", contracts.Select(FormatContract));
            throw new InvalidOperationException($"Nie znaleziono aktywnej umowy pracownika na {FormatDate(data)}. Dostępne umowy: {available}.");
        }

        private static bool IsContractActiveOnDate(object contract, DateTime data)
        {
            object okres = GetPropertyValue(contract, "OkresObowiazywania");
            DateTime? from = GetDateProperty(okres, "DataPoczatkowa");
            DateTime? to = GetDateProperty(okres, "DataKoncowa")?.Date;

            return from.HasValue &&
                   from.Value.Date <= data.Date &&
                   (!to.HasValue || to.Value >= data.Date);
        }

        private object PrepareContractForHarmonogram(Uchwyt sfera, object contract, DateTime dataOperacji)
        {
            LoadContractRelationships(sfera, contract, dataOperacji);
            if (contract is not UmowaPracowniczaGr typedContract)
            {
                return contract;
            }

            UmowaPracowniczaGr mainContract = typedContract.UmowaGlowna ?? typedContract.UmowaDlaKtorejAneks;
            if (mainContract == null || mainContract.Id == typedContract.Id)
            {
                return typedContract;
            }

            _logger.LogInformation(
                "Wybrana relacja wygląda na aneks, przełączam na umowę główną {Contract}.",
                FormatContract(mainContract));

            LoadContractRelationships(sfera, mainContract, dataOperacji);
            return mainContract;
        }

        private void LoadEmployeeRelationships(Uchwyt sfera, Pracownik pracownik, DateTime dataOperacji)
        {
            IPracownikRelationshipLoader loader = TryGetOptionalService<IPracownikRelationshipLoader>(sfera, dataOperacji);
            if (loader == null)
            {
                return;
            }

            TryLoadRelation("Pracownik.Osoba", () => loader.IsOsobaLoaded(pracownik), () => loader.LoadOsoba(pracownik));
            TryLoadRelation("Pracownik.UmowyPracownicze", () => loader.AreUmowyPracowniczeLoaded(pracownik), () => loader.LoadUmowyPracownicze(pracownik));
        }

        private void LoadContractRelationships(Uchwyt sfera, object contract, DateTime dataOperacji)
        {
            if (contract is not UmowaPracowniczaGr typedContract)
            {
                return;
            }

            IUmowaPracowniczaGrRelationshipLoader loader = TryGetOptionalService<IUmowaPracowniczaGrRelationshipLoader>(sfera, dataOperacji);
            if (loader == null)
            {
                return;
            }

            TryLoadRelation("UmowaPracowniczaGr.Kalendarze", () => loader.AreKalendarzeLoaded(typedContract), () => loader.LoadKalendarze(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.ZapisyWECP", () => loader.AreZapisyWECPLoaded(typedContract), () => loader.LoadZapisyWECP(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.SumaryczneECP", () => loader.AreSumaryczneECPLoaded(typedContract), () => loader.LoadSumaryczneECP(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.Rejestr", () => loader.IsRejestrLoaded(typedContract), () => loader.LoadRejestr(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.RodzajUmowyPracowniczej", () => loader.IsRodzajUmowyPracowniczejLoaded(typedContract), () => loader.LoadRodzajUmowyPracowniczej(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.TypUmowyPracowniczej", () => loader.IsTypUmowyPracowniczejLoaded(typedContract), () => loader.LoadTypUmowyPracowniczej(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.UmowaGlowna", () => loader.IsUmowaGlownaLoaded(typedContract), () => loader.LoadUmowaGlowna(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.UmowaDlaKtorejAneks", () => loader.IsUmowaDlaKtorejAneksLoaded(typedContract), () => loader.LoadUmowaDlaKtorejAneks(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.Aneks", () => loader.IsAneksLoaded(typedContract), () => loader.LoadAneks(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.NajnowszyAneks", () => loader.IsNajnowszyAneksLoaded(typedContract), () => loader.LoadNajnowszyAneks(typedContract));
            TryLoadRelation("UmowaPracowniczaGr.Aneksy", () => loader.AreAneksyLoaded(typedContract), () => loader.LoadAneksy(typedContract));

            LoadContractCalendars(sfera, typedContract, dataOperacji);
        }

        private void LoadContractCalendars(Uchwyt sfera, UmowaPracowniczaGr umowa, DateTime dataOperacji)
        {
            IKalendarzWUmowiePracowniczejRelationshipLoader contractCalendarLoader =
                TryGetOptionalService<IKalendarzWUmowiePracowniczejRelationshipLoader>(sfera, dataOperacji);
            IKalendarzRelationshipLoader calendarLoader =
                TryGetOptionalService<IKalendarzRelationshipLoader>(sfera, dataOperacji);
            ICyklKalendarzaRelationshipLoader cycleLoader =
                TryGetOptionalService<ICyklKalendarzaRelationshipLoader>(sfera, dataOperacji);

            foreach (KalendarzWUmowiePracowniczej kalendarzWUmowie in umowa.Kalendarze ?? Array.Empty<KalendarzWUmowiePracowniczej>())
            {
                if (contractCalendarLoader != null)
                {
                    TryLoadRelation("KalendarzWUmowie.Kalendarz", () => contractCalendarLoader.IsKalendarzLoaded(kalendarzWUmowie), () => contractCalendarLoader.LoadKalendarz(kalendarzWUmowie));
                    TryLoadRelation("KalendarzWUmowie.WymiarZatrudnienia", () => contractCalendarLoader.IsWymiarZatrudnieniaLoaded(kalendarzWUmowie), () => contractCalendarLoader.LoadWymiarZatrudnienia(kalendarzWUmowie));
                }

                if (kalendarzWUmowie.Kalendarz == null || calendarLoader == null)
                {
                    continue;
                }

                TryLoadRelation("Kalendarz.SystemRozliczaniaCzasuPracy", () => calendarLoader.IsSystemRozliczaniaCzasuPracyLoaded(kalendarzWUmowie.Kalendarz), () => calendarLoader.LoadSystemRozliczaniaCzasuPracy(kalendarzWUmowie.Kalendarz));
                TryLoadRelation("Kalendarz.Cykle", () => calendarLoader.AreCykleLoaded(kalendarzWUmowie.Kalendarz), () => calendarLoader.LoadCykle(kalendarzWUmowie.Kalendarz));
                TryLoadRelation("Kalendarz.Normy", () => calendarLoader.AreNormyLoaded(kalendarzWUmowie.Kalendarz), () => calendarLoader.LoadNormy(kalendarzWUmowie.Kalendarz));
                TryLoadRelation("Kalendarz.Wyjatki", () => calendarLoader.AreWyjatkiLoaded(kalendarzWUmowie.Kalendarz), () => calendarLoader.LoadWyjatki(kalendarzWUmowie.Kalendarz));
                TryLoadRelation("Kalendarz.WyjatkiUstawowe", () => calendarLoader.AreWyjatkiUstawoweLoaded(kalendarzWUmowie.Kalendarz), () => calendarLoader.LoadWyjatkiUstawowe(kalendarzWUmowie.Kalendarz));

                LoadCalendarCycles(kalendarzWUmowie.Kalendarz, cycleLoader);
            }
        }

        private void LoadCalendarCycles(Kalendarz kalendarz, ICyklKalendarzaRelationshipLoader cycleLoader)
        {
            if (cycleLoader == null)
            {
                return;
            }

            foreach (CyklKalendarza cykl in kalendarz.Cykle ?? Array.Empty<CyklKalendarza>())
            {
                TryLoadRelation("CyklKalendarza.Dni", () => cycleLoader.AreDniLoaded(cykl), () => cycleLoader.LoadDni(cykl));
            }
        }

        private void TryFillFromContract(IMenadzerHarmonogramuECP menadzer, object umowa)
        {
            try
            {
                MethodInfo method = menadzer.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(candidate =>
                    {
                        if (!string.Equals(candidate.Name, "WypelnijNaPodstawie", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        ParameterInfo[] parameters = candidate.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(umowa);
                    });

                method?.Invoke(menadzer, new[] { umowa });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "WypelnijNaPodstawie(umowa) nie powiodło się: {Message}",
                    ex.GetBaseException().Message);
            }
        }

        private static void InvokeSave(IMenadzerHarmonogramuECP menadzer)
        {
            MethodInfo saveMethod = menadzer.GetType().GetMethod("Zapisz", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("IMenadzerHarmonogramuECP nie udostępnia metody Zapisz().");

            object result = saveMethod.Invoke(menadzer, null);
            if (result is bool saved && !saved)
            {
                throw new InvalidOperationException("IMenadzerHarmonogramuECP.Zapisz() zwróciło false.");
            }
        }

        private void TryLoadRelation(string relationName, Func<bool> isLoaded, Action load)
        {
            try
            {
                if (isLoaded())
                {
                    return;
                }

                load();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Nie udało się doładować relacji {RelationName}: {Message}",
                    relationName,
                    ex.GetBaseException().Message);
            }
        }

        private static void ClearCollection(object collection)
        {
            if (collection == null)
            {
                return;
            }

            MethodInfo clearMethod = collection.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
            clearMethod?.Invoke(collection, null);
        }

        private static void AddToCollection(object collection, object item)
        {
            if (collection == null)
            {
                throw new InvalidOperationException("Kolekcja docelowa jest null.");
            }

            MethodInfo addMethod = collection.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"Kolekcja typu {collection.GetType().FullName} nie udostępnia metody Add().");

            addMethod.Invoke(collection, new[] { item });
        }

        private static object FindHarmonogramDay(object days, DateTime date)
        {
            if (days is not IEnumerable collection)
            {
                throw new InvalidOperationException("IMenadzerHarmonogramuECP.DniHarmonogramu nie jest kolekcją IEnumerable.");
            }

            foreach (object day in collection)
            {
                if (day != null && GetPropertyValue(day, "Data") is DateTime dayDate && dayDate.Date == date.Date)
                {
                    return day;
                }
            }

            throw new InvalidOperationException($"IMenadzerHarmonogramuECP nie zwrócił dnia dla {FormatDate(date)}.");
        }

        private static void SetTimeProperty(object target, string propertyName, TimeSpan value)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Nie znaleziono właściwości {propertyName} na typie {target.GetType().FullName}.");

            if (!property.CanWrite)
            {
                throw new InvalidOperationException($"Właściwość {propertyName} na typie {target.GetType().FullName} nie jest zapisywalna.");
            }

            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (propertyType == typeof(TimeSpan))
            {
                property.SetValue(target, value);
                return;
            }

            if (propertyType == typeof(long))
            {
                property.SetValue(target, value.Ticks);
                return;
            }

            object boxed = Activator.CreateInstance(propertyType)
                ?? throw new InvalidOperationException($"Nie udało się utworzyć instancji typu {propertyType.FullName} dla właściwości {propertyName}.");

            PropertyInfo ticksProperty = propertyType.GetProperty("Ticks", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ticksProperty?.CanWrite != true)
            {
                throw new InvalidOperationException($"Nie potrafię ustawić czasu dla właściwości {propertyName} typu {propertyType.FullName}.");
            }

            ticksProperty.SetValue(boxed, value.Ticks);
            property.SetValue(target, boxed);
        }

        private static object GetPropertyValue(object target, string propertyName)
        {
            if (target == null)
            {
                return null;
            }

            return target.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(target);
        }

        private static DateTime? GetDateProperty(object target, string propertyName)
        {
            object value = GetPropertyValue(target, propertyName);
            if (value is DateTime date)
            {
                return date;
            }

            if (value is string text && DateTime.TryParse(text, out DateTime parsed))
            {
                return parsed;
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

        private static DateTime ParseShiftDate(string value)
        {
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exactDate))
            {
                return exactDate.Date;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fallbackDate))
            {
                return fallbackDate.Date;
            }

            throw new InvalidOperationException($"Nieprawidłowa data zmiany: '{value}'. Oczekiwano formatu yyyy-MM-dd.");
        }

        private static TimeSpan ParseShiftTime(string value, string label)
        {
            if (TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan exactTime))
            {
                return exactTime;
            }

            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan fallbackTime))
            {
                return fallbackTime;
            }

            throw new InvalidOperationException($"Nieprawidłowa wartość {label}: '{value}'. Oczekiwano formatu HH:mm.");
        }

        private static void ValidateShift(RcpImportJob job, DateTime shiftDate, TimeSpan startTime, TimeSpan endTime)
        {
            if (shiftDate.Year != job.PeriodYear || shiftDate.Month != job.PeriodMonth)
            {
                throw new InvalidOperationException(
                    $"Zmiana z dnia {FormatDate(shiftDate)} nie należy do okresu {job.PeriodYear}-{job.PeriodMonth:00}.");
            }

            if (endTime <= startTime)
            {
                throw new InvalidOperationException(
                    $"Godzina zakończenia {FormatTime(endTime)} musi być późniejsza niż godzina rozpoczęcia {FormatTime(startTime)}.");
            }
        }

        private static int CalculateProgress(int processedShifts, int totalShifts)
        {
            double ratio = Math.Clamp((double)processedShifts / totalShifts, 0d, 1d);
            return 10 + (int)Math.Round(ratio * 85d, MidpointRounding.AwayFromZero);
        }

        private static string ResolveFinalStatus(RcpImportReport report)
        {
            if (report.ShiftsFailedCount == 0 && report.EmployeesSkippedCount == 0)
            {
                return "SUCCESS";
            }

            if (report.ShiftsImportedCount > 0 || report.EmployeesSkippedCount > 0)
            {
                return "PARTIAL_SUCCESS";
            }

            return "FAILED";
        }

        private static string BuildSummaryMessage(RcpImportReport report)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Import RCP zakończony statusem {report.Status}. Zapisano {report.ShiftsImportedCount}/{report.ShiftsReceivedCount} zmian dla {report.EmployeesImportedCount}/{report.EmployeesReceivedCount} pracowników.");
        }

        private static async Task ReportProgressAsync(Func<int, string, Task> reportProgress, int percent, string message)
        {
            if (reportProgress != null)
            {
                await reportProgress(percent, message);
            }
        }

        private static string FormatEmployee(Podmiot pracownik)
        {
            if (pracownik?.Osoba == null)
            {
                return "brak";
            }

            return FormatEmployee(pracownik.Osoba.Imie, pracownik.Osoba.Nazwisko);
        }

        private static string FormatEmployee(string firstName, string lastName)
        {
            string fullName = string.Join(" ", new[] { firstName?.Trim(), lastName?.Trim() }.Where(part => !string.IsNullOrWhiteSpace(part)));
            return string.IsNullOrWhiteSpace(fullName) ? "brak" : fullName;
        }

        private static string FormatContract(object contract)
        {
            string number = GetPropertyValue(contract, "Numer") as string ?? "brak numeru";
            object okres = GetPropertyValue(contract, "OkresObowiazywania");
            DateTime? from = GetDateProperty(okres, "DataPoczatkowa");
            DateTime? to = GetDateProperty(okres, "DataKoncowa");

            string period = from == null
                ? "brak okresu"
                : $"{FormatDate(from.Value)}-{(to.HasValue ? FormatDate(to.Value) : "bez końca")}";

            return $"{number} [{period}]";
        }

        private static string FormatDate(DateTime date)
        {
            return date.ToString("d.MM.yyyy", PolishCulture);
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }
    }
}
