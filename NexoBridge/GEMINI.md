# Dokumentacja Projektu: NexoBridge (Sfera Nexo 59+)
**Cel:** Mikroserwis (Web API) do seryjnego importu i dekretacji faktur ze Scanye przy użyciu poświadczeń użytkownika końcowego.

---

## 1. Kluczowe Odkrycia w SDK (Wersja 59+)
* **Dwuetapowy Import:** Synchronizacja słowników (`IOdbiorDanychKlientaBiuraRachunkowego`) musi poprzedzać zapis dokumentów.
* **Wbudowany Sędzia:** Wykorzystujemy `WyszukajSchematyDlaDokumentow` do automatycznego dobierania schematów dekretacji.
* **Refleksja & Dynamic:** Ze względu na zmienność struktur Sfery, werdykt Sędziego odpakowujemy po typach obiektów, a nie nazwach właściwości.

---

## 2. Architektura Produkcyjna (VM 10 - Nexo Node)
System zaprojektowany jako asynchroniczny procesor zadań, aby optymalnie zarządzać licencjami Sfery.



### Komponenty:
1.  **Web API (Minimal API):** Przyjmuje paczki faktur i dane logowania. Natychmiast zwraca `JobId`.
2.  **Kolejka Zadań (System.Threading.Channels):** Buforuje zlecenia. Jeśli wielu użytkowników kliknie "Importuj", zadania ustawią się w kolejce do obsługi przez dostępną licencję.
3.  **Background Worker:** Proces działający w tle, który wyciąga zadania z kolejki, odpala Sferę i mieli dane.
4.  **SignalR Hub:** "Rura" komunikacyjna, przez którą Bridge wysyła do Twojej aplikacji statusy: *10% - Logowanie*, *55% - Dekretacja FV/123/2026* itd.

---

## 3. Plan Implementacji (Fazy)

### Faza 1: Fundament ASP.NET Core
* Stworzenie projektu Web API.
* Konfiguracja portów i wczytywanie `.env` (bezpieczeństwo haseł).
* **Test:** Endpoint `/ping` zwraca status 200.

### Faza 2: Obsługa Paczki EPP i Poświadczeń
* Endpoint `POST /api/jobs/import` przyjmujący `multipart/form-data`.
* Odbiór login/hasło oraz kolekcji plików `.epp` (od 15 do 200 sztuk).
* **Test:** Postman wysyła 10 plików, API potwierdza ich odebranie i rozmiar.

### Faza 3: Kolejka i Procesor Tła
* Wdrożenie `TaskQueue` i `BackgroundService`.
* Logika: API wrzuca paczkę do kolejki i zwraca `JobId`. Worker w tle pisze w konsoli: "Czekam na zadanie...".
* **Test:** Wysłanie 3 paczek pod rząd – API odpowiada od razu, konsola przetwarza je sekwencyjnie.

### Faza 4: Raportowanie SignalR
* Dodanie Huba SignalR.
* Podpięcie postępu z Workera do Huba (użycie `JobId` jako nazwy grupy).
* **Test:** Prosty skrypt HTML wyświetla pasek postępu zmieniający się w czasie pracy serwera.

### Faza 5: Integracja Sfery (Logika Biznesowa)
* Przeniesienie kodu dekretacji do Workera.
* **Agregacja EPP:** Worker czyta wszystkie pliki EPP z paczki, scala je w jedną listę `object[]` w pamięci RAM i raz synchronizuje słowniki (maksymalna wydajność).
* **Pętla Zwrotna:** Podpięcie zdarzeń z `CichaObslugaImportu` do SignalR.
* **Test:** Pełny przelot od kliknięcia "Importuj" do pojawienia się faktur w Nexo z postępem na żywo.

### Faza 6: Instalacja na VM 10
* Publikacja projektu (`dotnet publish`).
* Rejestracja jako Windows Service.
* Otwarcie portów w zaporze maszyn wirtualnych.

---

## 4. Wytyczne Techniczne (Clean Code)
* **Zasada 1:10:** Jeden start Sfery na jedną paczkę faktur (oszczędność czasu i licencji).
* **Brak Śmieci:** Pliki EPP są przetwarzane w RAM. Jeśli przekroczą 100MB, lądują w folderze `Temp/` i są czyszczone po zakończeniu Joba.
* **DI (Dependency Injection):** Sfera jest wstrzykiwana do serwisów, ale tworzona dynamicznie na podstawie poświadczeń z requestu.