# Dokumentacja Projektu: NexoBridge (Sfera Nexo 59+)
**Cel:** Automatyczny import faktur z plików EPP do InsERT nexo (Rachmistrz/Rewizor) dla biura rachunkowego, a następnie ich seryjna dekretacja w oparciu o zdefiniowane w programie Warunki Wyboru.

---

## 1. Kluczowe Odkrycia w SDK (Wersja 59+)
W najnowszych wersjach SDK mechanizm importu EPP uległ zmianie. Nie korzystamy już z ogólnego "Agenta", lecz z dedykowanych interfejsów dla Biur Rachunkowych.

### Główne Interfejsy i Klasy (Import EPP):
* `IOdbiorDanychKlientaBiuraRachunkowego`: Główny menedżer odpowiedzialny za inicjację procesu odbioru dokumentów od klienta.
* `ISerializatorEPP`: Służy do dekodowania fizycznego pliku `.epp` na tablicę obiektów `object[]` rozumianych przez Sferę.
* `IPolowicznaOperacjaKomunikacjiZMedium`: Interfejs reprezentujący pierwszy etap importu (Słowniki).

### Główne Interfejsy i Klasy (Dekretacja):
* `IDokumentyDoKsiegowania`: Menedżer dostępu do tzw. "Poczekalni" (dokumenty wczytane, ale jeszcze niezaksięgowane, oznaczane jako `StatusKsiegowy == 2`).
* `IOperacjeImportuKsiegowego`: Silnik dekretacji (wbudowany "Sędzia"). Rozpoznaje, który schemat dekretacji nałożyć na dany dokument.
* `IOkresyObrachunkowe`: Wymagany do przekazania Sędziemu aktualnego roku obrachunkowego, z którego ma pobrać schematy.

---

## 2. Logika Procesu (Workflow)
Proces jest podzielony na odizolowane etapy i korzysta z natywnego silnika oceny schematów Sfery:

1.  **Deserializacja EPP:** Wczytanie pliku EPP przez `ISerializatorEPP`.
2.  **Etap 1 (Słowniki):** Wywołanie `Zapisz()`. Sfera synchronizuje kontrahentów, towary, waluty itp. Zwraca obiekt kolejnego etapu.
3.  **Etap 2 (Dokumenty):** Wywołanie `Zapisz()`. Faktury lądują w module "Dokumenty do dekretacji" (Status: Oczekujące).
4.  **Ewaluacja (Wbudowany Sędzia):** Metoda `WyszukajSchematyDlaDokumentow` weryfikuje oczekujące dokumenty z "Warunkami Wyboru" schematów dla bieżącego okresu.
5.  **Rozpakowanie Werdyktu:** Zabezpieczone wydobycie par `[Dokument, Schemat]` przy użyciu Refleksji z obiektu `dynamic`.
6.  **Dekretacja Seryjna:** Wykonanie cichej operacji importu, która fizycznie tworzy zapisy w KPiR / Księgach Handlowych oraz rejestrach VAT.

---

## 3. Rozwiązania Techniczne (Fixy i Pułapki)
Podczas tworzenia mostu natrafiliśmy na szereg barier architektonicznych i językowych, które omijamy następująco:

* **Zarządzanie Hasłami (.env):** Zamiast twardego kodowania haseł SQL i Nexo, wykorzystujemy paczkę `DotNetEnv`. Hasła są bezpieczne i pomijane przez `.gitignore`.
* **Obfuskacja Werdyktu Nexo (Refleksja):** Sędzia zwraca "teczkę" typu `DokumentyZeWskazanymiSchematami`. Nazwy wewnętrznych właściwości potrafią się zmieniać. Zamiast szukać ich po nazwie, używamy Refleksji (szukamy po typie `DokumentDoKsiegowania` i `SchematImportu`), co czyni kod odpornym na aktualizacje InsERTu.
* **Klątwa typu Dynamic w C#:** Ponieważ werdykt sędziego jest odczytywany dynamicznie, wynikowa lista przejmuje ten typ. Metody rozszerzające LINQ (np. `.Any()`) **nie działają** na typach `dynamic` w czasie wykonywania. Należy używać natywnych właściwości np. `.Count == 0`.
* **AssemblyResolve & WPF Context:** Dynamiczne ładowanie bibliotek DLL prosto z folderu `nexo\Bin` oraz inicjalizacja `new System.Windows.Application()`, by uniknąć błędów UI podczas pracy w tle.

---

## 4. Architektura Docelowa (Clean Architecture)
Kod został zrefaktoryzowany zgodnie z zasadą Single Responsibility Principle (SRP) i przygotowany pod bycie mikroserwisem (ASP.NET Core Web API):

* **Infrastruktura (`SilnikSfery.cs`):** Odpowiada WYŁĄCZNIE za poprawne powołanie AppDomain, wczytanie `.env` i zwrócenie aktywnego obiektu `Uchwyt`. Posiada `IDisposable` do zwalniania licencji.
* **Logika Biznesowa (`SerwisKsiegowy.cs`):** Zawiera metody `PrzetworzPlikEpp` oraz `UruchomAutomatycznaDekretacje`. Rozbija paczki od Sędziego, odrzuca błędy (logowanie brakujących schematów) i przepycha poprawne faktury.
* **Kontroler / Entry Point (`Program.cs`):** Czysty plik sterujący, łatwy do zamiany na kontrolery REST (przyjmujące żądania od aplikacji App klasyfikującej faktury). 

---

## 5. Fundament Logiki Dekretacji (Code Snippet)
Kluczowy fragment bezpiecznego "odpakowywania" werdyktu od Sędziego Nexo przy użyciu Refleksji:

```csharp
// Pobranie z wbudowanego analizatora Nexo
dynamic werdykt = menedzerDynamiczny.WyszukajSchematyDlaDokumentow(oczekujace, obecnyOkres);
var szufladka = werdykt.GetType().GetProperty("DokumentyZeSchematami")?.GetValue(werdykt) as System.Collections.IEnumerable;

foreach (var item in szufladka)
{
    var typItemu = item.GetType();
    
    // Szukanie bezpiecznie po TYPIE, a nie nazwie właściwości
    var dok = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.Name.Contains("DokumentDoKsiegowania"))?.GetValue(item);
    
    InsERT.Moria.ModelDanych.SchematImportu schemat = null;
    var schematyProp = typItemu.GetProperties().FirstOrDefault(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Any(g => g.Name.Contains("SchematImportu")));
    
    if (schematyProp != null && schematyProp.GetValue(item) is System.Collections.IEnumerable lista)
    {
        foreach (var s in lista) { schemat = (InsERT.Moria.ModelDanych.SchematImportu)s; break; } // Bierzemy pierwszy dopasowany
    }

    if (dok != null && schemat != null)
    {
        var para = (DokumentDoKsiegowania)dok;
        string numer = para.DokumentDoKsiegowaniaGlowny.NumerDokumentu ?? "Brak numeru";
        Console.WriteLine($"[OK] Dopasowano: {numer} -> Schemat: {schemat.Nazwa}");
    }
}
```