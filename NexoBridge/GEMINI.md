# Dokumentacja Projektu: NexoBridge (Sfera Nexo 59+)
**Cel:** Automatyczny import faktur z plików EPP do InsERT nexo (Rachmistrz/Rewizor) dla biura rachunkowego, a następnie ich dekretacja.

---

## 1. Kluczowe Odkrycia w SDK (Wersja 59+)
W najnowszych wersjach SDK mechanizm importu EPP uległ zmianie. Nie korzystamy już z ogólnego "Agenta", lecz z dedykowanych interfejsów dla Biur Rachunkowych.

### Główne Interfejsy i Klasy:
*   `IOdbiorDanychKlientaBiuraRachunkowego`: Główny menedżer odpowiedzialny za inicjację procesu odbioru dokumentów od klienta.
*   `ISerializatorEPP`: Służy do dekodowania fizycznego pliku `.epp` na tablicę obiektów `object[]` rozumianych przez Sferę.
*   `IPolowicznaOperacjaKomunikacjiZMedium`: Interfejs reprezentujący pierwszy etap importu (Słowniki).
*   `IOperacjaOdbioruDokumentowDoKsiegowaniaKlientaBiuraRachunkowego`: Interfejs drugiego etapu (Dokumenty).

---

## 2. Logika Procesu (Workflow)
Proces importu jest **ściśle dwuetapowy** i oparty na wzorcu Fluent API:

1.  **Deserializacja:** Wczytanie pliku EPP przez `ISerializatorEPP`.
2.  **Inicjacja:** Wywołanie `OdbierzOfflineEpp` na menedżerze głównym.
3.  **Etap 1 (Słowniki):** Wywołanie `Zapisz()` na operacji słowników. Metoda ta synchronizuje kontrahentów, towary, waluty itp. 
    *   *Ważne:* Zwraca ona obiekt kolejnego etapu, a nie tylko status `bool`.
4.  **Etap 2 (Dokumenty):** Wywołanie `Zapisz()` na obiekcie otrzymanym z Etapu 1. To faktycznie umieszcza faktury w module "Dokumenty do dekretacji".

---

## 3. Rozwiązania Techniczne (Fixy)
Aby uruchomić Sferę w aplikacji konsolowej/serwisowej bez błędów zależnośći i UI, zastosowaliśmy:

*   **AssemblyResolve:** Dynamiczne ładowanie bibliotek DLL prosto z folderu `nexo\Bin` (brak konieczności kopiowania setek plików do projektu).
*   **[STAThread]:** Wymagany atrybut nad metodą `Main` dla obsługi bibliotek nexo.
*   **WPF Context:** Inicjalizacja `new System.Windows.Application()` w celu uniknięcia błędu `Resolution of the dependency failed` przy budowaniu formatki błędów Sfery.
*   **Konfiguracja .csproj:** Włączenie `<UseWPF>true</UseWPF>` oraz `<UseWindowsForms>true</UseWindowsForms>`.

---

## 4. Architektura Docelowa (Produkcyjna)
System rozproszony na maszynach wirtualnych (VM):

*   **VM 10 (Nexo Node):** Tu stoi `NexoBridge` jako Web API (ASP.NET Core). Ma bezpośredni dostęp do DLLek nexo i SQL-a.
*   **VM 12-14 (App):** Aplikacja klasyfikująca faktury. Wysyła żądania REST do VM 10.
*   **Logika Sesji:** NexoBridge trzyma zalogowaną instancję Sfery w pamięci (Singleton), aby uniknąć 20-sekundowego logowania przy każdej fakturze.
*   **Mapowanie:** System mapuje NIP klienta na konkretną nazwę bazy danych nexo w SQL.

---

## 5. Kompletny Kod PoC (Proof of Concept)
```csharp
[STAThread]
private static void Main(string[] args)
{
    AppDomain.CurrentDomain.AssemblyResolve += ResolvingAssemblies; // Mechanizm auto-szukania DLL
    try {
        if (System.Windows.Application.Current == null) new System.Windows.Application();

        using (var sfera = Uchwyty.UtworzNowy(dane, new PostepLadowaniaSfery())) {
            var menedzer = sfera.PodajObiektTypu<IOdbiorDanychKlientaBiuraRachunkowego>();
            var serializator = sfera.PodajObiektTypu<ISerializatorEPP>();
            
            // 1. Wczytanie
            var daneEpp = serializator.DeserializujObiektyZPliku(sciezka).ToArray();
            
            // 2. Etap 1: Słowniki
            var opSlownikow = menedzer.OdbierzOfflineEpp(daneEpp, baza, new ProstyInformator());
            var opDokumentow = opSlownikow.Zapisz(); // Zapisuje słowniki i zwraca etap 2
            
            // 3. Etap 2: Dokumenty
            var ostatecznyWynik = opDokumentow.Zapisz(); // Zapisuje faktury
            
            if (ostatecznyWynik.CzyZakonczonoSukcesem) Console.WriteLine("Sukces!");
        }
    } catch (Exception ex) { Console.WriteLine(ex.ToString()); }
}
```

