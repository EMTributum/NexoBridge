using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using DotNetEnv;
using InsERT.Moria.OperacjeZewnetrzne;
using InsERT.Mox.Product;
using NexoBridge.API;
using NexoBridge.Infrastructure;
using NexoBridge.Models;
using NexoBridge.Services;
using NexoBridge.Workers;
using NexoBridge.Hubs;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace NexoBridge
{
    public class Program
    {
        private const long DefaultMaxRequestBodySizeMb = 30;
        private const string MaxRequestBodySizeEnvName = "NEXO_BRIDGE_MAX_REQUEST_BODY_MB";
        private const string LogLevelEnvName = "NEXO_LOG_LEVEL";
        private const string BuildMarker = "VAT_STATUS_AUDIT_2026_08_13_1225";

        /// <summary>Znaczniki otaczające JSON zwracany przez "--payroll-client-worker". Sfera nigdy
        /// nie była projektowana do pracy bez konsoli/UI - w praktyce coś wewnątrz jej własnego,
        /// zamkniętego kodu potrafi dopisać do stdout dodatkowy tekst (obserwowane na produkcji:
        /// JSON zwracany przez proces potomny nie parsował się, bo poprzedzał go jakiś obcy fragment).
        /// Zamiast zakładać "cały stdout to czysty JSON", rodzic wycina TYLKO fragment między tymi
        /// znacznikami - odporne na cokolwiek innego, co SDK dorzuci do stdout.</summary>
        internal const string PayrollWorkerResultStartMarker = "###PAYROLL_WORKER_RESULT_START###";
        internal const string PayrollWorkerResultEndMarker = "###PAYROLL_WORKER_RESULT_END###";

        /// <summary>Te same znaczniki, ten sam powód (patrz komentarz nad Payroll*Marker powyżej),
        /// współdzielone przez oba tryby procesu potomnego backfillu (enumeracja i zapis komentarzy) -
        /// nigdy nie działają w tym samym procesie naraz, więc jedna para wystarcza.</summary>
        internal const string BackfillWorkerResultStartMarker = "###BACKFILL_WORKER_RESULT_START###";
        internal const string BackfillWorkerResultEndMarker = "###BACKFILL_WORKER_RESULT_END###";

        public static void Main(string[] args)
        {
            // Tryb procesu potomnego dla POJEDYNCZEGO klienta w Payroll Counts - patrz
            // RunPayrollClientWorker i uzasadnienie w PayrollCountsService.PoliczDlaKlientaWProcesie.
            // Musi być SAMOTNĄ, pierwszą gałęzią w Main - żadnego Seriloga na konsolę (zaśmieciłby
            // stdout, na którym oczekujemy WYŁĄCZNIE jednej linii JSON), żadnego Kestrela/SignalR.
            //
            // UWAGA: LoadEnvironment()/RegisterNexoRuntimeResolvers() MUSZĄ się wykonać TUTAJ, w Main,
            // PRZED wywołaniem RunPayrollClientWorker() - a nie jako pierwsze linie WEWNĄTRZ tamtej
            // metody. RunPayrollClientWorker odwołuje się do typów z assembly InsERT.Moria.* (SferaEngine,
            // IFabrykaLicznikowObiektow...), więc .NET JIT-uje CAŁE jej ciało (czyli już próbuje
            // rozwiązać te referencje) w momencie WEJŚCIA do metody - zanim wykona się choćby pierwsza
            // linia jej kodu. Gdyby rejestracja resolverów była pierwszą linią WEWNĄTRZ tej metody,
            // byłoby za późno: JIT rzuca FileNotFoundException, bo próbuje znaleźć np.
            // "InsERT.Moria.API" zanim resolver, który wie jak je znaleźć w folderze NexoDLLs, w ogóle
            // zdążył się zarejestrować. Potwierdzone empirycznie przy pierwszym teście tego trybu.
            if (args.Length > 0 && string.Equals(args[0], "--payroll-client-worker", StringComparison.Ordinal))
            {
                LoadEnvironment();
                RegisterNexoRuntimeResolvers();
                Environment.Exit(RunPayrollClientWorker());
                return;
            }

            // Tryb procesu potomnego dla POJEDYNCZEGO klienta w Raw Payroll Counts - patrz
            // RunRawPayrollClientWorker i uzasadnienie w RawPayrollCountsService.PoliczDlaKlientaWProcesie.
            // Ten sam powód, dla którego to MUSI być osobna, wczesna gałąź w Main (patrz komentarz nad
            // gałęzią --payroll-client-worker powyżej - JIT rozwiązuje referencje InsERT.Moria.* przy
            // wejściu do metody, więc LoadEnvironment/RegisterNexoRuntimeResolvers muszą wykonać się PRZED
            // wywołaniem RunRawPayrollClientWorker(), nie jako pierwsze linie w jej wnętrzu).
            if (args.Length > 0 && string.Equals(args[0], "--raw-payroll-client-worker", StringComparison.Ordinal))
            {
                LoadEnvironment();
                RegisterNexoRuntimeResolvers();
                Environment.Exit(RunRawPayrollClientWorker());
                return;
            }

            // Tryb procesu potomnego dla POJEDYNCZEGO klienta w odczycie ZUS-u właściciela - patrz
            // ZusOwnerContributionService.PoliczDlaKlientaWProcesie. Ten sam powód wczesnej, samotnej
            // gałęzi w Main co --payroll-client-worker/--raw-payroll-client-worker powyżej.
            if (args.Length > 0 && string.Equals(args[0], "--zus-owner-client-worker", StringComparison.Ordinal))
            {
                LoadEnvironment();
                RegisterNexoRuntimeResolvers();
                Environment.Exit(RunZusOwnerClientWorker());
                return;
            }

            // Tryb procesu potomnego dla POJEDYNCZEGO klienta w backfillu linków do podglądu faktur -
            // patrz BackfillService (EnumerateOneClientInChildProcessAsync) i to samo uzasadnienie co
            // przy --payroll-client-worker powyżej (JIT + świeży, nieskażony stan statyczny Sfery).
            if (args.Length > 0 && string.Equals(args[0], "--backfill-enumerate-client-worker", StringComparison.Ordinal))
            {
                LoadEnvironment();
                RegisterNexoRuntimeResolvers();
                Environment.Exit(RunBackfillEnumerateClientWorker());
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "--backfill-write-comments-worker", StringComparison.Ordinal))
            {
                LoadEnvironment();
                RegisterNexoRuntimeResolvers();
                Environment.Exit(RunBackfillWriteCommentsWorker());
                return;
            }

            LoadEnvironment();
            RegisterNexoRuntimeResolvers();

            // =====================================================================
            // 1. INICJALIZACJA SERILOGA NA SAMYM POCZĄTKU
            // =====================================================================
            LogEventLevel minimumLogLevel = ReadMinimumLogLevel();
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            string mainLogPath = Path.Combine(logDirectory, "nexobridge-.log");
            string attachmentDebugLogPath = Path.Combine(logDirectory, "nexobridge-attachments-debug-.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Wycisza spam z ASP.NET
                .Enrich.FromLogContext()
                // Format dla konsoli (kolorowy, czytelny)
                .WriteTo.Console(
                    restrictedToMinimumLevel: minimumLogLevel,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                // Format dla pliku tekstowego (nowy plik codziennie)
                .WriteTo.File(
                    path: mainLogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    restrictedToMinimumLevel: minimumLogLevel,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .WriteTo.Logger(logger => logger
                    .Filter.ByIncludingOnly(IsAttachmentServiceLog)
                    .WriteTo.File(
                        path: attachmentDebugLogPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        restrictedToMinimumLevel: LogEventLevel.Debug,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                    ))
                .CreateLogger();

            try
            {
                Log.Information("Uruchamianie mikroserwisu NexoBridge... Marker kompilacji: {BuildMarker}", BuildMarker);
                Log.Information(
                    "Poziom głównego logowania NexoBridge: {LogLevel}. Log główny: {MainLogPath}. Pełna diagnostyka załączników: {AttachmentDebugLogPath}.",
                    minimumLogLevel,
                    mainLogPath,
                    attachmentDebugLogPath);
                Log.ForContext("SourceContext", typeof(AttachmentService).FullName)
                    .Debug(
                        "[ZAŁĄCZNIKI DIAG START] Logger diagnostyczny załączników gotowy. Plik={AttachmentDebugLogPath}; BaseDir={BaseDirectory}; NexoRuntime={NexoRuntimeDirectory}.",
                        attachmentDebugLogPath,
                        AppContext.BaseDirectory,
                        Path.Combine(AppContext.BaseDirectory, "NexoDLLs"));

                long maxRequestBodySizeMb = ReadMaxRequestBodySizeMb();
                long maxRequestBodySizeBytes = maxRequestBodySizeMb * 1024L * 1024L;

                var builder = WebApplication.CreateBuilder(args);

                // =====================================================================
                // 2. PODPIĘCIE SERILOGA DO HOSTA APLIKACJI
                // =====================================================================
                builder.Host.UseSerilog();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Limits.MaxRequestBodySize = maxRequestBodySizeBytes;
                });
                builder.Services.Configure<FormOptions>(options =>
                {
                    options.MultipartBodyLengthLimit = maxRequestBodySizeBytes;
                });

                // 3. Rejestrujemy aplikację jako oficjalną Usługę Windows
                builder.Services.AddWindowsService(options =>
                {
                    options.ServiceName = "NexoBridgeService";
                });

                // 4. Twardy CORS - wpuszczamy TYLKO Twoją aplikację z VM 12
                string allowedOrigin = Environment.GetEnvironmentVariable("ALLOWED_ORIGIN");
                builder.Services.AddCors(options =>
                {
                    options.AddPolicy("StrictPolicy", policy => {
                        policy.WithOrigins(allowedOrigin)
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials(); // Wymagane dla SignalR
                    });
                });

                // 4b. Klucz API do huba SignalR i endpointów REST - dziś jedynymi klientami są
                // nasze własne usługi (backend KlasyfikatorFaktur + nexo_bridge_listener), nie
                // przeglądarka, więc prosty współdzielony sekret w nagłówku wystarczy. Dopóki
                // zmienna nie jest ustawiona, autoryzacja NIE jest wymuszana (żeby wdrożenie tej
                // zmiany nie zablokowało istniejących wywołań, dopóki obie strony nie mają klucza).
                string bridgeApiKey = Environment.GetEnvironmentVariable("NEXO_BRIDGE_API_KEY");
                if (string.IsNullOrWhiteSpace(bridgeApiKey))
                {
                    Log.Warning(
                        "NEXO_BRIDGE_API_KEY nie jest ustawiony - hub SignalR i endpointy REST NexoBridge " +
                        "NIE wymagają dziś autoryzacji. Ustaw tę zmienną (tę samą wartość co w " +
                        "KlasyfikatorFaktur/.env), żeby to zamknąć.");
                }

                Log.Information(
                    "Aktywny limit request body w NexoBridge: {MaxRequestBodySizeMb} MB ({MaxRequestBodySizeBytes} B).",
                    maxRequestBodySizeMb,
                    maxRequestBodySizeBytes
                );

                builder.Services.AddSignalR();
                builder.Services.AddSingleton<JobQueue>();
                builder.Services.AddSingleton<OfficeVatFlagsJobQueue>();
                builder.Services.AddSingleton<OfficeVatFlagsResultStore>();
                builder.Services.AddSingleton<NexoBridgeLogReader>();
                builder.Services.AddSingleton<RcpEmployeeMappingStore>();
                builder.Services.AddSingleton<RcpImportJobQueue>();
                builder.Services.AddSingleton<RcpImportResultStore>();
                builder.Services.AddSingleton<RcpImportStateStore>();
                builder.Services.AddSingleton<PoczekalniaBaselineStore>();
                builder.Services.AddSingleton<RcpRuntimeSettings>();
                builder.Services.AddSingleton<BillingJobQueue>();
                builder.Services.AddSingleton<BillingResultStore>();
                builder.Services.AddSingleton<InvoiceCreationJobQueue>();
                builder.Services.AddSingleton<InvoiceCreationResultStore>();
                builder.Services.AddSingleton<BillingClientsJobQueue>();
                builder.Services.AddSingleton<BillingClientsResultStore>();
                builder.Services.AddSingleton<PayrollCountsJobQueue>();
                builder.Services.AddSingleton<PayrollCountsResultStore>();
                builder.Services.AddSingleton<RawPayrollCountsJobQueue>();
                builder.Services.AddSingleton<RawPayrollCountsResultStore>();
                builder.Services.AddSingleton<ZusOwnerContributionJobQueue>();
                builder.Services.AddSingleton<ZusOwnerContributionResultStore>();
                builder.Services.AddSingleton<DuplicateScanJobQueue>();
                builder.Services.AddSingleton<DuplicateScanResultStore>();
                builder.Services.AddSingleton<BackfillEnumerateJobQueue>();
                builder.Services.AddSingleton<BackfillEnumerateResultStore>();
                builder.Services.AddSingleton<BackfillWriteCommentsJobQueue>();
                builder.Services.AddSingleton<BackfillWriteCommentsResultStore>();
                builder.Services.AddHttpClient<NexoBridgeErrorReporter>();
                builder.Services.AddHttpClient<RcpSourceClient>();
                builder.Services.AddHostedService<NexoBackgroundWorker>();
                builder.Services.AddHostedService<OfficeVatFlagsBackgroundWorker>();
                builder.Services.AddHostedService<RcpImportBackgroundWorker>();
                builder.Services.AddHostedService<RcpPollingBackgroundWorker>();
                builder.Services.AddHostedService<BillingSnapshotBackgroundWorker>();
                builder.Services.AddHostedService<InvoiceCreationBackgroundWorker>();
                builder.Services.AddHostedService<BillingClientsBackgroundWorker>();
                builder.Services.AddHostedService<PayrollCountsBackgroundWorker>();
                builder.Services.AddHostedService<RawPayrollCountsBackgroundWorker>();
                builder.Services.AddHostedService<ZusOwnerContributionBackgroundWorker>();
                builder.Services.AddHostedService<DuplicateScanBackgroundWorker>();
                builder.Services.AddHostedService<BackfillEnumerateBackgroundWorker>();
                builder.Services.AddHostedService<BackfillWriteCommentsBackgroundWorker>();

                var app = builder.Build();

                // Używamy nowej, rygorystycznej polityki
                app.UseCors("StrictPolicy");

                // Autoryzacja kluczem API - patrz komentarz przy odczycie NEXO_BRIDGE_API_KEY wyżej.
                // /ping zostaje otwarty, żeby monitoring/healthcheck nie potrzebował klucza.
                app.Use(async (context, next) =>
                {
                    if (string.IsNullOrWhiteSpace(bridgeApiKey) || context.Request.Path.StartsWithSegments("/ping"))
                    {
                        await next();
                        return;
                    }

                    string providedKey = context.Request.Headers["X-Nexo-Bridge-Api-Key"].FirstOrDefault();
                    if (!string.Equals(providedKey, bridgeApiKey, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsync("Brak lub nieprawidłowy klucz API (X-Nexo-Bridge-Api-Key).");
                        return;
                    }

                    await next();
                });

                // Rejestrujemy trasę dla naszego Huba
                app.MapHub<ProgressHub>("/progressHub");

                app.MapHealthEndpoints();
                app.MapImportEndpoints();
                app.MapOfficeVatFlagsEndpoints();
                app.MapRcpEmployeeMappingEndpoints();
                app.MapRcpImportEndpoints();
                app.MapLogEndpoints();
                app.MapBillingEndpoints();
                app.MapPayrollCountsEndpoints();
                app.MapRawPayrollCountsEndpoints();
                app.MapZusOwnerContributionEndpoints();
                app.MapDuplicateScanEndpoints();
                app.MapBackfillEndpoints();

                Log.Information("NexoBridge nasłuchuje na porcie 5000...");
                app.Run("http://0.0.0.0:5000");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Aplikacja zakończyła działanie z powodu krytycznego błędu");
            }
            finally
            {
                // Zapewnia zrzucenie ostatnich logów z pamięci do pliku przed zamknięciem
                Log.CloseAndFlush();
            }
        }

        /// <summary>
        /// Liczy pozycje kadrowo-płacowe DOKŁADNIE JEDNEGO klienta, w kompletnie IZOLOWANYM procesie
        /// systemowym - patrz pełne uzasadnienie w PayrollCountsService.PoliczDlaKlientaWProcesie.
        /// Wejście: JSON (PayrollClientWorkerRequest) na stdin. Wyjście: JSON (PayrollClientWorkerResponse)
        /// na stdout, otoczony PayrollWorkerResultStartMarker/EndMarker - rodzic wycina TYLKO ten
        /// fragment (patrz uzasadnienie przy definicji znaczników), bo Sfera potrafi dopisać do stdout
        /// coś od siebie. Diagnostyka poza kontraktem idzie na stderr.
        /// </summary>
        private static int RunPayrollClientWorker()
        {
            var response = new PayrollClientWorkerResponse { Status = "SUCCESS" };

            try
            {
                string inputJson = Console.In.ReadToEnd();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                PayrollClientWorkerRequest request = JsonSerializer.Deserialize<PayrollClientWorkerRequest>(inputJson, jsonOptions);

                try
                {
                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(request.Username, request.Password, request.DatabaseName, ProductId.Gratyfikant);

                        IFabrykaLicznikowObiektow counterFactory =
                            SferaReflectionHelpers.GetRequiredService<IFabrykaLicznikowObiektow>(silnik.Sfera, DateTime.Today);

                        foreach (PayrollClientWorkerLineSpec lineSpec in request.Lines)
                        {
                            try
                            {
                                var pendingSpec = new PendingPayrollLineSpec(
                                    lineSpec.Label,
                                    lineSpec.CounterGuid,
                                    lineSpec.Tiers
                                        .Select(t => new PayrollTierSpec(t.From, t.To, t.UnitNet, t.UnitGross, t.CollectiveNet, t.CollectiveGross))
                                        .ToList());

                                PayrollFeeLineDto line = PayrollLineSpecExtractor.ComputeLine(
                                    pendingSpec, counterFactory, request.PeriodStart, request.PeriodEnd);
                                if (line != null)
                                {
                                    response.Lines.Add(line);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[payroll-client-worker] Błąd pozycji '{lineSpec.Label}': {ex.GetBaseException().Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    response.Status = "FAILED";
                    response.Error = ex.GetBaseException().Message;
                }

                string outputJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                Console.Out.Write(PayrollWorkerResultStartMarker);
                Console.Out.Write(outputJson);
                Console.Out.Write(PayrollWorkerResultEndMarker);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[payroll-client-worker] Krytyczny błąd: {ex}");
                return 1;
            }
        }

        /// <summary>Analogiczne do RunPayrollClientWorker, ale liczy SUROWE rachunki do umów
        /// pracowniczych i wypłaty bezpośrednio z bazy klienta (RawPayrollExtractor), BEZ pośrednictwa
        /// licznika obiektów cennika - patrz RawPayrollCountsService i RawPayrollExtractor.</summary>
        private static int RunRawPayrollClientWorker()
        {
            var response = new RawPayrollClientWorkerResponse { Status = "SUCCESS" };

            try
            {
                string inputJson = Console.In.ReadToEnd();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                RawPayrollClientWorkerRequest request = JsonSerializer.Deserialize<RawPayrollClientWorkerRequest>(inputJson, jsonOptions);

                try
                {
                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(request.Username, request.Password, request.DatabaseName, ProductId.Gratyfikant);

                        response.RachunekCount = RawPayrollExtractor.CountRachunkiDoUmowPracowniczych(silnik.Sfera, request.PeriodStart, request.PeriodEnd);
                        response.WyplataCount = RawPayrollExtractor.CountWyplaty(silnik.Sfera, request.PeriodStart, request.PeriodEnd);
                    }
                }
                catch (Exception ex)
                {
                    response.Status = "FAILED";
                    response.Error = ex.GetBaseException().Message;
                }

                string outputJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                Console.Out.Write(PayrollWorkerResultStartMarker);
                Console.Out.Write(outputJson);
                Console.Out.Write(PayrollWorkerResultEndMarker);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[raw-payroll-client-worker] Krytyczny błąd: {ex}");
                return 1;
            }
        }

        /// <summary>Analogiczne do RunRawPayrollClientWorker, ale odczytuje (BEZ przeliczania) już
        /// policzone w Nexo naliczenia ZUS właściciela bezpośrednio z bazy klienta
        /// (ZusOwnerContributionExtractor) - patrz ZusOwnerContributionService i
        /// ZusOwnerContributionExtractor co do uzasadnienia, dlaczego to WYŁĄCZNIE odczyt.</summary>
        private static int RunZusOwnerClientWorker()
        {
            var response = new ZusOwnerContributionWorkerResponse { Status = "SUCCESS" };

            try
            {
                string inputJson = Console.In.ReadToEnd();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                ZusOwnerContributionWorkerRequest request = JsonSerializer.Deserialize<ZusOwnerContributionWorkerRequest>(inputJson, jsonOptions);

                try
                {
                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(request.Username, request.Password, request.DatabaseName, ProductId.Gratyfikant);

                        response.Entries = ZusOwnerContributionExtractor.GetOwnerContributionsForPeriod(
                            silnik.Sfera, request.PeriodYear, request.PeriodMonth);
                    }
                }
                catch (Exception ex)
                {
                    response.Status = "FAILED";
                    response.Error = ex.GetBaseException().Message;
                }

                string outputJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                Console.Out.Write(PayrollWorkerResultStartMarker);
                Console.Out.Write(outputJson);
                Console.Out.Write(PayrollWorkerResultEndMarker);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[zus-owner-client-worker] Krytyczny błąd: {ex}");
                return 1;
            }
        }

        /// <summary>
        /// Enumeruje/wyciąga załączniki DOKŁADNIE JEDNEGO klienta backfillu, w kompletnie IZOLOWANYM
        /// procesie systemowym - patrz pełne uzasadnienie w PayrollCountsService (ten sam mechanizm,
        /// ten sam powód: powtarzane logowania Sfery w jednym procesie psują jej stan statyczny nawet
        /// przy pełnej serializacji). Wejście: JSON (BackfillEnumerateClientWorkerRequest) na stdin.
        /// Wyjście: JSON (BackfillEnumerateClientWorkerResponse) na stdout, otoczony
        /// BackfillWorkerResultStartMarker/EndMarker. Diagnostyka poza kontraktem idzie na stderr.
        /// </summary>
        private static int RunBackfillEnumerateClientWorker()
        {
            var response = new BackfillEnumerateClientWorkerResponse { Status = "SUCCESS" };

            try
            {
                string inputJson = Console.In.ReadToEnd();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                BackfillEnumerateClientWorkerRequest request = JsonSerializer.Deserialize<BackfillEnumerateClientWorkerRequest>(inputJson, jsonOptions);

                try
                {
                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(request.Username, request.Password, request.DatabaseName);

                        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
                        var service = new BackfillService(loggerFactory);
                        var client = new BackfillClientRef
                        {
                            Nip = request.ClientNip,
                            ClientName = request.ClientName,
                            DatabaseName = request.DatabaseName
                        };

                        response.Rows = service.EnumerateOneClient(silnik.Sfera, client, request.Year, out int skippedAlreadyLinked);
                        response.SkippedAlreadyLinked = skippedAlreadyLinked;
                    }
                }
                catch (Exception ex)
                {
                    response.Status = "FAILED";
                    response.Error = ex.GetBaseException().Message;
                }

                string outputJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                Console.Out.Write(BackfillWorkerResultStartMarker);
                Console.Out.Write(outputJson);
                Console.Out.Write(BackfillWorkerResultEndMarker);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[backfill-enumerate-client-worker] Krytyczny błąd: {ex}");
                return 1;
            }
        }

        /// <summary>Analogiczne do RunBackfillEnumerateClientWorker, ale dopisuje komentarze z linkiem
        /// dla WSZYSTKICH wierszy JEDNEJ bazy (DatabaseName) w jednym, izolowanym procesie.</summary>
        private static int RunBackfillWriteCommentsWorker()
        {
            var response = new BackfillWriteCommentsClientWorkerResponse { Status = "SUCCESS" };

            try
            {
                string inputJson = Console.In.ReadToEnd();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                BackfillWriteCommentsClientWorkerRequest request = JsonSerializer.Deserialize<BackfillWriteCommentsClientWorkerRequest>(inputJson, jsonOptions);

                try
                {
                    using (var silnik = new SferaEngine())
                    {
                        silnik.Uruchom(request.Username, request.Password, request.DatabaseName);

                        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
                        var service = new BackfillService(loggerFactory);
                        BackfillService.WriteCommentsResult result = service.WriteCommentsForDatabase(silnik.Sfera, request.Rows);

                        response.Written = result.Written;
                        response.SkippedAlreadyLinked = result.SkippedAlreadyLinked;
                        response.Failed = result.Failed;
                        response.Errors = result.Errors;
                    }
                }
                catch (Exception ex)
                {
                    response.Status = "FAILED";
                    response.Error = ex.GetBaseException().Message;
                }

                string outputJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                Console.Out.Write(BackfillWorkerResultStartMarker);
                Console.Out.Write(outputJson);
                Console.Out.Write(BackfillWorkerResultEndMarker);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[backfill-write-comments-worker] Krytyczny błąd: {ex}");
                return 1;
            }
        }

        private static long ReadMaxRequestBodySizeMb()
        {
            string rawValue = Environment.GetEnvironmentVariable(MaxRequestBodySizeEnvName);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return DefaultMaxRequestBodySizeMb;
            }

            if (long.TryParse(rawValue, out long parsedValue) && parsedValue > 0)
            {
                return parsedValue;
            }

            Log.Warning(
                "Nieprawidłowa wartość zmiennej {EnvName}='{EnvValue}'. Używam domyślnego limitu {DefaultMaxRequestBodySizeMb} MB.",
                MaxRequestBodySizeEnvName,
                rawValue,
                DefaultMaxRequestBodySizeMb
            );
            return DefaultMaxRequestBodySizeMb;
        }

        private static void LoadEnvironment()
        {
            string envPath = Path.Combine(AppContext.BaseDirectory, ".env");
            if (File.Exists(envPath))
            {
                Env.Load(envPath);
                return;
            }

            Env.Load();
        }

        private static LogEventLevel ReadMinimumLogLevel()
        {
            string rawValue = Environment.GetEnvironmentVariable(LogLevelEnvName);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return LogEventLevel.Information;
            }

            return Enum.TryParse(rawValue.Trim(), ignoreCase: true, out LogEventLevel parsedLevel)
                ? parsedLevel
                : LogEventLevel.Information;
        }

        private static void RegisterNexoRuntimeResolvers()
        {
            string nexoRuntimeDirectory = Path.Combine(AppContext.BaseDirectory, "NexoDLLs");
            if (!Directory.Exists(nexoRuntimeDirectory))
            {
                return;
            }

            AddNexoRuntimeDirectoriesToPath(nexoRuntimeDirectory);

            AssemblyLoadContext.Default.Resolving += ResolveNexoManagedAssembly;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNexoNativeLibrary;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveNexoManagedAssemblyLegacy;
        }

        private static void AddNexoRuntimeDirectoriesToPath(string nexoRuntimeDirectory)
        {
            try
            {
                List<string> nexoDirectories = Directory
                    .EnumerateDirectories(nexoRuntimeDirectory, "*", SearchOption.AllDirectories)
                    .Prepend(nexoRuntimeDirectory)
                    .ToList();

                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                HashSet<string> existingPathEntries = currentPath
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                List<string> missingDirectories = nexoDirectories
                    .Where(directory => !existingPathEntries.Contains(directory))
                    .ToList();

                if (missingDirectories.Count > 0)
                {
                    string newPathPrefix = string.Join(Path.PathSeparator, missingDirectories);
                    Environment.SetEnvironmentVariable("PATH", $"{newPathPrefix}{Path.PathSeparator}{currentPath}");
                }
            }
            catch
            {
                // Resolver dalej sprobuje ladowac biblioteki po sciezce bezposredniej.
            }
        }

        private static Assembly ResolveNexoManagedAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            string candidatePath = FindNexoRuntimeFile($"{assemblyName.Name}.dll");
            return string.IsNullOrWhiteSpace(candidatePath)
                ? null
                : context.LoadFromAssemblyPath(candidatePath);
        }

        private static Assembly ResolveNexoManagedAssemblyLegacy(object sender, ResolveEventArgs args)
        {
            string assemblyName = new AssemblyName(args.Name).Name;
            string candidatePath = FindNexoRuntimeFile($"{assemblyName}.dll");
            return string.IsNullOrWhiteSpace(candidatePath)
                ? null
                : Assembly.LoadFrom(candidatePath);
        }

        private static IntPtr ResolveNexoNativeLibrary(Assembly assembly, string libraryName)
        {
            string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? libraryName
                : $"{libraryName}.dll";

            string candidatePath = FindNexoRuntimeFile(fileName);
            return string.IsNullOrWhiteSpace(candidatePath)
                ? IntPtr.Zero
                : NativeLibrary.Load(candidatePath);
        }

        private static string FindNexoRuntimeFile(string fileName)
        {
            string nexoRuntimeDirectory = Path.Combine(AppContext.BaseDirectory, "NexoDLLs");
            string directPath = Path.Combine(nexoRuntimeDirectory, fileName);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            try
            {
                return Directory
                    .EnumerateFiles(nexoRuntimeDirectory, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsAttachmentServiceLog(LogEvent logEvent)
        {
            if (logEvent == null || !logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue sourceContext))
            {
                return false;
            }

            string value = sourceContext.ToString().Trim('"');
            return string.Equals(value, typeof(AttachmentService).FullName, StringComparison.Ordinal);
        }
    }
}
