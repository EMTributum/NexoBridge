# Plan Integracji RCP z NexoBridge

## Cel

Przenieść działanie obecnego projektu `NexoRCP` do `NexoBridge` jako dodatkowy, zautomatyzowany proces działający w tle. Proces ma okresowo pobierać dane RCP z zewnętrznego źródła, sprawdzać czy godziny za dany miesiąc są już gotowe, a następnie zapisywać je w Gratyfikancie przez Sferę jako historyczne godziny przepracowane widoczne w kalendarzu ECP.

## Stan obecny

### Co już mamy w `NexoBridge`

- usługę Windows opartą o `BackgroundService`
- gotowy mechanizm kolejek oparty o `Channel<T>`
- warstwę uruchamiania Sfery w `SferaEngine`
- komplet bibliotek runtime nexo, w tym biblioteki kadrowe
- gotowe wzorce:
  - worker nasłuchujący kolejki
  - worker wykonujący osobny proces dla Biura
  - serwis domenowy wykonujący logikę biznesową
  - logowanie przez `Serilog`

### Co już mamy w `NexoRCP`

- sprawdzoną logikę zapisu godzin przez `IMenadzerHarmonogramuECP`
- połączenie do `ProductId.Gratyfikant`
- wyszukiwanie pracownika po PESEL
- odnalezienie aktywnej umowy na wskazany dzień
- przygotowanie relacji potrzebnych do pracy z harmonogramem
- zapis godzin historycznych widocznych w kalendarzu ECP

## Docelowy przepływ

1. `NexoBridge` uruchamia cykliczny worker sprawdzający, czy są nowe dane RCP do importu.
2. Worker odpytuje zewnętrzny endpoint HTTP dla wskazanych baz / klientów.
3. Endpoint zwraca jedną z odpowiedzi:
   - dane jeszcze niegotowe
   - dane gotowe do importu
4. Jeżeli dane są gotowe, worker buduje zadanie importu RCP i zapisuje je do osobnej kolejki.
5. Dedykowany worker importu RCP otwiera Sferę z `ProductId.Gratyfikant`.
6. Serwis importu RCP mapuje `employeeId -> PESEL`.
7. Dla każdego pracownika i dnia wykonuje zapis godzin przez logikę przeniesioną z `NexoRCP`.
8. Wynik importu jest logowany i zapisywany jako raport końcowy.

## Zalecana architektura

### Nowe modele

Pliki do dodania w `NexoBridge/Models`:

- `RcpTimesheetPayload.cs`
- `RcpEmployeeTimesheet.cs`
- `RcpShiftPayload.cs`
- `RcpImportJob.cs`
- `RcpEmployeeMapping.cs`
- `RcpImportReport.cs`
- `RcpEmployeeImportResult.cs`
- `RcpShiftImportResult.cs`

### Proponowane modele wejściowe

```csharp
public sealed class RcpTimesheetPayload
{
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public List<RcpEmployeeTimesheet> EmployeesTimesheets { get; set; } = new();
}

public sealed class RcpEmployeeTimesheet
{
    public string EmployeeId { get; set; } = string.Empty;
    public List<RcpShiftPayload> Shifts { get; set; } = new();
}

public sealed class RcpShiftPayload
{
    public DateOnly Date { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
}
```

### Model zadania do kolejki

```csharp
public sealed class RcpImportJob
{
    public string JobId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public List<RcpEmployeeMapping> EmployeeMappings { get; set; } = new();
    public RcpTimesheetPayload Payload { get; set; } = new();
    public string SourceSnapshotHash { get; set; } = string.Empty;
}
```

### Mapowanie pracownika

```csharp
public sealed class RcpEmployeeMapping
{
    public string EmployeeId { get; set; } = string.Empty;
    public string Pesel { get; set; } = string.Empty;
    public string? WorkerName { get; set; }
}
```

## Nowe usługi

Pliki do dodania w `NexoBridge/Services`:

- `RcpJobQueue.cs`
- `RcpSourceClient.cs`
- `RcpImportService.cs`
- `RcpImportResultStore.cs`
- `RcpImportStateStore.cs`

### `RcpSourceClient`

Odpowiedzialność:

- wykonywanie zapytania HTTP do zewnętrznej strony
- deserializacja odpowiedzi
- rozróżnienie statusów `not_ready` / `ready`
- podstawowa walidacja danych wejściowych

Proponowany interfejs:

```csharp
public interface IRcpSourceClient
{
    Task<RcpSourceResponse> FetchAsync(
        string databaseName,
        int year,
        int month,
        CancellationToken cancellationToken);
}
```

### `RcpImportService`

Odpowiedzialność:

- przejęcie logiki biznesowej z `NexoRCP`
- połączenie danych wejściowych z mapą `employeeId -> PESEL`
- wykonanie importu dla każdego pracownika i każdej zmiany
- budowa raportu końcowego

Logika z `NexoRCP`, którą należy przenieść prawie 1:1:

- `UpdateWorkedHours`
- `ApplyHoursThroughHarmonogram`
- znalezienie pracownika po PESEL
- znalezienie aktywnej umowy na dany dzień
- doładowanie relacji pracownika i umowy
- zapis przez `IMenadzerHarmonogramuECP`

W praktyce warto tę logikę rozdzielić na metody:

- `ImportMonthAsync`
- `ImportEmployeeAsync`
- `ImportShiftAsync`
- `FindEmployeeByPesel`
- `FindActiveContractOnDay`
- `ApplyWorkedHoursThroughHarmonogram`

### `RcpJobQueue`

Taki sam wzorzec jak istniejące kolejki:

- jeden reader
- writer z API lub workera pollingowego
- zadanie typu `RcpImportJob`

## Nowe workery

Pliki do dodania w `NexoBridge/Workers`:

- `RcpImportBackgroundWorker.cs`
- `RcpPollingBackgroundWorker.cs`

### `RcpImportBackgroundWorker`

Odpowiedzialność:

- pobranie zadania z kolejki
- uruchomienie `SferaEngine` z `ProductId.Gratyfikant`
- wywołanie `RcpImportService`
- obsługa błędów i logowania

### `RcpPollingBackgroundWorker`

Odpowiedzialność:

- cykliczne odpytanie źródła danych RCP
- ustalenie, dla jakich baz trzeba uruchomić pobranie
- pominięcie baz bez aktywnego Gratyfikanta
- pilnowanie, by nie importować tego samego okresu wielokrotnie
- zbudowanie i wrzucenie `RcpImportJob` do kolejki

## Zalecany kontrakt HTTP źródła RCP

Najbezpieczniejszy jest jawny status odpowiedzi zamiast polegania na pustym `employeesTimesheets`.

### Wariant `not_ready`

```json
{
  "status": "not_ready",
  "periodYear": 2026,
  "periodMonth": 7,
  "message": "Godziny za wskazany okres nie są jeszcze gotowe."
}
```

### Wariant `ready`

```json
{
  "status": "ready",
  "periodYear": 2026,
  "periodMonth": 7,
  "employeesTimesheets": [
    {
      "employeeId": "105",
      "shifts": [
        {
          "date": "2026-07-02",
          "startTime": "09:00",
          "endTime": "20:00"
        }
      ]
    }
  ]
}
```

### Dlaczego tak

- prostsza diagnostyka
- łatwiejsze logowanie
- brak zgadywania, czy pusta lista oznacza brak godzin, czy brak gotowości danych

## Skąd brać listę baz do sprawdzania

Są dwie rozsądne opcje.

### Opcja A: konfiguracja statyczna

W `.env` albo osobnej konfiguracji trzymamy listę baz, które mają uczestniczyć w imporcie.

Plusy:

- najszybsze wdrożenie
- mało zależności

Minusy:

- wymaga ręcznego utrzymywania listy

### Opcja B: na podstawie danych Biura

Wykorzystujemy istniejący mechanizm odczytu baz klientów i filtrujemy tylko te, które mają aktywnego Gratyfikanta.

Plusy:

- mniej ręcznej konfiguracji
- większa spójność z danymi Biura

Minusy:

- trochę większa złożoność startowa

### Rekomendacja

Na pierwszą wersję:

- lista baz w konfiguracji
- osobno mapa `employeeId -> PESEL`

Po potwierdzeniu działania:

- automatyczne filtrowanie po klientach Biura z aktywnym Gratyfikantem

## Mapowanie `employeeId -> PESEL`

Ponieważ payload wejściowy identyfikuje pracownika przez `employeeId`, a import do Gratyfikanta wygodnie robimy po PESEL, potrzebujemy stabilnej mapy.

### Rekomendowana forma

Osobny plik konfiguracyjny na bazę, np.:

```json
{
  "databaseName": "Nexo_CIERACHOWSKA DOROTA-WARSZTATOWNIA",
  "employees": [
    {
      "employeeId": "105",
      "pesel": "02242102513",
      "workerName": "Przykładowy Jan"
    }
  ]
}
```

`workerName` nie jest wymagany do wyszukania, ale pomaga w logach i diagnostyce.

## Idempotencja i zabezpieczenia

To jest najważniejszy element poza samym importem.

### Musimy zapobiec:

- ponownemu importowi tego samego miesiąca
- wielokrotnemu nadpisaniu tych samych danych przez ten sam snapshot
- importowi częściowo gotowych danych

### Minimalna wersja

`RcpImportStateStore` zapisuje:

- `databaseName`
- `periodYear`
- `periodMonth`
- hash payloadu źródłowego
- datę ostatniego sukcesu
- status ostatniej próby

Jeżeli przy kolejnym pollingu przyjdzie dokładnie ten sam hash dla okresu już oznaczonego jako `SUCCESS`, worker pomija import.

### Pytanie biznesowe do ustalenia

Czy dla tego samego miesiąca dopuszczamy reimport, jeśli źródło zwróci inny payload niż poprzednio.

Rekomendacja:

- tak, ale tylko jeśli payload ma inny hash
- każda taka sytuacja powinna być bardzo wyraźnie zalogowana jako `REIMPORT`

## Polityka nadpisywania godzin

To trzeba ustalić przed implementacją produkcyjną.

### Wariant 1

Nadpisujemy zawsze.

Plusy:

- najprostsze zachowanie

Minusy:

- może nadpisać ręczne poprawki księgowej

### Wariant 2

Nadpisujemy tylko wpisy pochodzące z automatu.

Plusy:

- bezpieczniejsze

Minusy:

- trzeba oznaczać wpisy, np. opisem technicznym

### Wariant 3

Nie nadpisujemy istniejących danych, tylko raportujemy konflikt.

Plusy:

- największe bezpieczeństwo

Minusy:

- więcej wyjątków do ręcznej obsługi

### Rekomendacja

Na start:

- nadpisuj tylko wpisy oznaczone jako automatyczne
- ustawiaj techniczny opis, np. `Import RCP NexoBridge 2026-07`

To uprości późniejsze rozróżnienie wpisów ręcznych od automatycznych.

## Harmonogram uruchamiania

Nie rekomenduję modelu „jedna próba raz w miesiącu”.

### Lepsza strategia

Od 1 do 10 dnia kolejnego miesiąca:

- uruchamiaj polling raz dziennie
- dla poprzedniego miesiąca
- kończ dalsze próby po pierwszym udanym imporcie

### Przykład

10 sierpnia 2026:

- sprawdzamy dane za lipiec 2026

Jeżeli źródło odpowie `not_ready`:

- kolejna próba następnego dnia

Jeżeli odpowie `ready`:

- import
- zapis stanu `SUCCESS`
- brak kolejnych prób dla tego okresu

## Logowanie i raportowanie

### W logach chcemy widzieć:

- bazę docelową
- okres
- liczbę pracowników
- liczbę zmian
- liczbę zmian zapisanych
- liczbę konfliktów
- liczbę błędów

### Raport końcowy powinien zawierać:

- status zadania
- bazę
- okres
- listę pracowników
- listę dni z wynikiem
- komunikaty błędów per dzień / pracownik

## Proponowane pliki do dodania

### `Models`

- `RcpTimesheetPayload.cs`
- `RcpImportJob.cs`
- `RcpImportReport.cs`
- `RcpEmployeeImportResult.cs`
- `RcpShiftImportResult.cs`
- `RcpEmployeeMapping.cs`

### `Services`

- `RcpJobQueue.cs`
- `RcpSourceClient.cs`
- `RcpImportService.cs`
- `RcpImportStateStore.cs`
- `RcpImportResultStore.cs`

### `Workers`

- `RcpPollingBackgroundWorker.cs`
- `RcpImportBackgroundWorker.cs`

### `API`

Opcjonalnie:

- `RcpImportEndpoints.cs`

To pozwoli ręcznie wymusić import lub podejrzeć stan ostatniego importu.

## Zmiany w istniejących plikach

### `Program.cs`

Do zrobienia:

- rejestracja nowych singletonów
- rejestracja `HttpClient` dla `RcpSourceClient`
- dodanie hosted services dla workerów RCP

### `SferaEngine.cs`

Do zrobienia:

- nic dużego, bo już wspiera `ProductId product`
- dla importu RCP po prostu używać `ProductId.Gratyfikant`

## Kolejność wdrożenia

### Etap 1

Przenieść logikę z `NexoRCP` do nowego `RcpImportService`, ale jeszcze bez pollingu HTTP.

Cel:

- uruchomić import ręcznie na twardym payloadzie testowym

### Etap 2

Dodać `RcpJobQueue` i `RcpImportBackgroundWorker`.

Cel:

- mieć identyczny wzorzec jak w obecnych procesach `NexoBridge`

### Etap 3

Dodać `RcpSourceClient` i ręczny endpoint, który wrzuca payload do kolejki.

Cel:

- przetestować integrację ze źródłem bez pełnej automatyzacji miesięcznej

### Etap 4

Dodać `RcpPollingBackgroundWorker`.

Cel:

- pełna automatyzacja pobierania danych raz dziennie w oknie importowym

### Etap 5

Dodać `RcpImportStateStore` i obsługę reimportów.

Cel:

- odporność na duplikaty i ponowne dostarczenie tych samych danych

## Minimalna wersja MVP

Jeżeli chcemy ruszyć szybko, pierwsza wersja może wyglądać tak:

- ręczny endpoint w `NexoBridge`
- payload JSON wklejany lub wysyłany z zewnątrz
- osobna kolejka RCP
- osobny worker RCP
- `RcpImportService` z logiką z `NexoRCP`
- brak automatycznego pollingu
- brak automatycznego reimportu
- mapa `employeeId -> PESEL` z pliku konfiguracyjnego

To pozwoli szybko sprawdzić cały tor end-to-end bez wchodzenia jeszcze w harmonogram cykliczny.

## Rekomendacja końcowa

Najlepszy kierunek wdrożenia:

1. przenieść logikę `NexoRCP` do `NexoBridge` jako osobny moduł RCP
2. nie mieszać tego z `NexoImportService`, bo to inny proces biznesowy
3. użyć osobnej kolejki i osobnego workera
4. uruchamiać Sferę dla `ProductId.Gratyfikant`
5. dodać polling dopiero po uruchomieniu stabilnej wersji ręcznej

Taki podział będzie najczytelniejszy, najbezpieczniejszy i najmniej ryzykowny dla obecnego importu faktur.
