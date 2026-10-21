# Projekt mostku między sferą nexo a automatycznym klasyfikatorem faktur
## Program wymaga importu bibliotek nexo
- należy wskazać je w `Infrastruktura/SilnikSfery.cs` w zmiennej `nexoBinPath`
## Dodanie faktury w formacie `.epp` wygląda następująco:
- w folderze `/faktury` umieszczamy plik `.epp`
- zmieniamy jego nazwę na `Faktura.epp`

## Dodatkowa dokumentacja
- plan integracji importu RCP z NexoBridge: `docs/RcpImportPlan.md`

## Zasady zmian i testów
- **Każdą zmianę lub test dotykający Sfery/nexo najpierw sprawdzamy w NexoBridgeKonsola** (`C:\Automatyzacja\NexoBridgeKonsola`).
  To projekt poligonowy — można go dowolnie modyfikować (nowe flagi diagnostyczne, prototypy, pomiary).
- Do NexoBridge przenosimy dopiero kod sprawdzony w Konsoli. W NexoBridge nie dodajemy trybów diagnostycznych.
- Uruchamianie testów (NexoBridge / NexoBridgeKonsola, także przez asystenta AI):
  - **odczyty na dowolnej bazie** — można uruchamiać bez pytania,
  - **zapisy wyłącznie na bazie testowej WARSZTATOWNIA** (`Nexo_CIERACHOWSKA DOROTA-WARSZTATOWNIA`) — można uruchamiać bez pytania,
  - **usuwanie danych lub zapis na jakiejkolwiek innej bazie** — wymaga wcześniejszej zgody.
  - Nowa ścieżka, której „tylko odczyt” nie jest jeszcze potwierdzony (np. `Wylicz`/`Nalicz` w Sferze), idzie najpierw na WARSZTATOWNIĘ z kontrolą `@@DBTS` przed/po.
