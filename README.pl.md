# Telemetrus

*English version: [README.md](README.md)*

System telemetryczny do zbierania pomiarów z urządzeń IoT: przyjmuje dane przez REST API, kolejkuje je w RabbitMQ, weryfikuje integralność (HMAC-SHA256), zapisuje w bazie czasowej InfluxDB i rozsyła alerty w czasie rzeczywistym przez SignalR.

## Przepływ informacji w systemie

```
[Klient / JMeter]
      │  POST /measurement  (payload Base64 + checksum HMAC + channel)
      ▼
[FrontApi]  ──►  [RabbitMQ]  ──►  [TelemetryWorker]  ──►  [InfluxDB]
                                                              │ alert (webhook)
                                                              ▼
                                                   [NotificationWebApp]
                                                              │ SignalR
                                                              ▼
                                                   [Przeglądarka / UI]
```

### 1. Klient (JMeter lub skrypt PowerShell)

Generuje pomiar, koduje go i wysyła do API.

- tworzy JSON: `{"deviceId":"sensor-1","value":23.5}`
- liczy `checksum = HMAC-SHA256(json, SecretKey)`
- koduje JSON w Base64 → `payload`
- wysyła `POST /measurement` z polami `{ payload, checksum, channel }`

### 2. FrontApi ([FrontApi/](FrontApi/)), port 5000

REST API przyjmujące dane. Waliduje żądanie i publikuje do kolejki.

- **middleware** ([Program.cs:19-39](FrontApi/Program.cs#L19-L39)) loguje każdy request (metoda, ścieżka, body do 200 znaków)
- **walidacja** ([MeasurementController.cs](FrontApi/Controllers/MeasurementController.cs)) sprawdza obecność `payload` i `checksum`, poprawność nazwy `channel`, dekoduje Base64, parsuje JSON, wymaga `deviceId` i numerycznego `value`
- **publisher** ([RabbitMqPublisher.cs](FrontApi/RabbitMqPublisher.cs)) wysyła wiadomość do kolejki `measurements.{channel}` z flagą `persistent=true`; kolejki deklarowane są leniwie przy pierwszej publikacji
- API **nie weryfikuje** HMAC, to zadanie Workera (separation of concerns) — API gwarantuje jedynie poprawność struktury

Błędne żądania kończą się `400 Bad Request` i nie trafiają do kolejki.

### 3. RabbitMQ, porty 5672 / 15672 (management)

Broker asynchronicznej komunikacji. Dla każdego kanału istnieje para kolejek:

- `measurements.{channel}`: kolejka główna (durable, persistent messages)
- `measurements.{channel}.dlq`: dead-letter queue (automatyczne przekierowanie przez `x-dead-letter-exchange`)

Panel zarządzania: [http://localhost:15672](http://localhost:15672) (guest/guest).

### 4. TelemetryWorker ([TelemetryWorker/](TelemetryWorker/))

Konsument kolejek, walidator integralności i zapisywacz danych. Działa jako `BackgroundService`.

- łączy się z RabbitMQ i deklaruje kolejki dla wszystkich skonfigurowanych kanałów ([Worker.cs:25-53](TelemetryWorker/Worker.cs#L25-L53))
- `BasicQos(prefetchCount=1)`: przetwarza jedną wiadomość na raz
- dla każdej wiadomości:
  1. **Deserializacja** JSON → `QueueMessage { Data, Checksum }`. Błąd → `BasicNack(requeue=false)` → DLQ.
  2. **Weryfikacja HMAC** ([Worker.cs:111-124](TelemetryWorker/Worker.cs#L111-L124)): oblicza `expected = HMAC-SHA256(Data, SecretKey)`. Brak zgodności → DLQ.
  3. **Zapis do InfluxDB** ([InfluxWriter.cs](TelemetryWorker/InfluxWriter.cs)): tworzy `PointData` z tagiem `deviceId` i polem `value`, precyzja milisekundowa.
  4. `BasicAck` tylko po pomyślnym zapisie.

Dzięki temu DLQ zawiera wyłącznie błędy integralności / uszkodzone wiadomości, bo błędy struktury są już odfiltrowane na poziomie API.

### 5. InfluxDB, port 8086

Baza czasowa (time-series). Przechowuje pomiary w bucket'cie `telemetry` (organizacja `myorg`).

- każdy pomiar to punkt: `sensor_reading,deviceId=<id> value=<float> <timestamp>`
- alerty konfigurowane w UI InfluxDB ([docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md)): check (np. `value > 80`) + notification rule wysyłają HTTP POST (webhook) do NotificationWebApp

### 6. NotificationWebApp ([NotificationWebApp/](NotificationWebApp/)), port 5002

Most między InfluxDB a użytkownikiem końcowym.

- **webhook endpoint** (`POST /webhook/influx`) przyjmuje payload alertu z InfluxDB, wyciąga `_message` i `_level`
- **SignalR hub** (`/alertHub`) rozgłasza alert do wszystkich podłączonych klientów metodą `ReceiveAlert`
- **UI** ([wwwroot/index.html](NotificationWebApp/wwwroot/index.html)): strona w przeglądarce pokazująca status połączenia i listę alertów na żywo, z kolorowaniem wg poziomu (crit/warn/info/ok)

### 7. Przeglądarka, [http://localhost:5002](http://localhost:5002)

Klient SignalR utrzymujący otwarte połączenie WebSocket. Każdy nowy alert z InfluxDB pojawia się natychmiast, bez odświeżania strony.

---

## Konfiguracja lokalna

Repozytorium nie zawiera prawdziwych sekretów. Przed pierwszym uruchomieniem:

1. Skopiuj `.env.example` do `.env` i wpisz własny token InfluxDB (np. wygenerowany przez `openssl rand -base64 64`).
2. Utwórz `TelemetryWorker/appsettings.Development.json` z tą samą wartością tokena:
   ```json
   {
     "InfluxDB": { "Token": "<ta sama wartość co w .env>" }
   }
   ```
   Ten plik jest w `.gitignore` — ASP.NET Core wczytuje go automatycznie obok `appsettings.json` w środowisku Development.
3. `Hmac:SecretKey` w `FrontApi/appsettings.json` i `TelemetryWorker/appsettings.json` to wspólna wartość demonstracyjna (`telemetrus-demo-shared-secret`) używana lokalnie przez oba serwisy i skrypty w `scripts/` — nie chroni realnych danych, więc może zostać taka, jaka jest, do celów demo. W realnym wdrożeniu powinna trafić do menedżera sekretów (patrz sekcja niżej).

## Jak uruchomić i zaprezentować system

### Wymagania

- .NET SDK 8.0
- Docker + Docker Compose
- PowerShell 7+ (do skryptów demo)
- Apache JMeter 5.6+ (testy wydajnościowe)

### Krok 1: infrastruktura (RabbitMQ + InfluxDB)

```bash
docker compose up -d
```

Weryfikacja:

- RabbitMQ: [http://localhost:15672](http://localhost:15672), login `guest` / `guest`
- InfluxDB: [http://localhost:8086](http://localhost:8086), login `admin` / `admin12345`

### Krok 2: uruchom trzy aplikacje .NET (każda w osobnym terminalu)

**Terminal 1, FrontApi** (przyjmuje pomiary):

```bash
cd FrontApi
dotnet run
```

**Terminal 2, TelemetryWorker** (konsumuje kolejkę, zapisuje do InfluxDB):

```bash
cd TelemetryWorker
dotnet run
```

**Terminal 3, NotificationWebApp** (webhook + SignalR + UI):

```bash
cd NotificationWebApp
dotnet run
```

### Krok 3: otwórz UI alertów

[http://localhost:5002](http://localhost:5002)

W prawym górnym rogu powinien pojawić się status „Połączono” (SignalR).

### Krok 4: wyślij testowe pomiary

```powershell
pwsh scripts/send-measurements.ps1
```

Skrypt wysyła serię żądań: poprawne i kilka z celowo błędnym checksum.

Co warto obejrzeć przy okazji:

1. logi **FrontApi**: dekodowanie Base64 i wysyłka do kolejki
2. logi **TelemetryWorker**: `Checksum OK. Zapisuję do InfluxDB...` dla poprawnych, `[DLQ] Odrzucono wiadomość — błąd integralności HMAC` dla błędnych
3. panel **RabbitMQ** ([http://localhost:15672](http://localhost:15672)): kolejki `measurements.*` pustoszeją, w `measurements.*.dlq` rosną wiadomości
4. **InfluxDB Data Explorer** ([http://localhost:8086](http://localhost:8086)): bucket `telemetry`, measurement `sensor_reading`, wykres wartości w czasie

### Krok 5: skonfiguruj alert i pokaż realtime UI

Zgodnie z instrukcją w [docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md):

1. w InfluxDB utwórz **Threshold Check** na `sensor_reading.value` z progiem (np. `> 80`)
2. dodaj **HTTP Notification Endpoint** wskazujący na `http://host.docker.internal:5002/webhook/influx`
3. dodaj **Notification Rule** łączącą check z endpointem

Następnie wyślij pomiar przekraczający próg:

```powershell
pwsh scripts/send-measurements.ps1 -HighValue
```

W przeglądarce na `http://localhost:5002` pojawi się alert w czasie rzeczywistym: przepływ od API przez kolejkę, Worker, InfluxDB, webhook i SignalR aż do UI jest wtedy kompletny.

### Krok 6: testy wydajnościowe JMeter

Scenariusz JMeter symuluje wiele urządzeń telemetrycznych wysyłających dane równocześnie i sprawdza stabilność całego pipeline'u API → kolejka → Worker → InfluxDB pod obciążeniem.

**Scenariusz** ([jmeter/telemetrus-load-test.jmx](jmeter/telemetrus-load-test.jmx)):

- **Thread Group**: 20 wątków, ramp-up 10 s, 50 iteracji każdy → 1000 żądań łącznie
- **CSV Data Set Config**: każdy wątek czyta kolejne wiersze z `test-data.csv` (payload, checksum, channel)
- **HTTP Request Sampler**: `POST http://localhost:5000/measurement` z body JSON z CSV
- **Gaussian Random Timer**: 50 ms ± 20 ms opóźnienia między żądaniami (symulacja realnego obciążenia)
- **Response Assertion**: oczekiwany kod 200
- **View Results Tree + Summary Report**: wizualizacja wyników i statystyk

**Przebieg:**

```powershell
# 1. Wygeneruj dane wejściowe (domyślnie 1000 wierszy z poprawnym HMAC)
pwsh scripts/generate-jmeter-csv.ps1 -Count 1000

# 2a. Uruchomienie z GUI (do prezentacji, widać wykres i drzewo żądań)
cd jmeter
jmeter -t telemetrus-load-test.jmx

# 2b. Uruchomienie headless z raportem HTML (do dokumentacji)
jmeter -n -t telemetrus-load-test.jmx -l results.jtl -e -o report
```

**Co przeanalizować w raporcie:**

- **Throughput** (żądań/s): ile wiadomości API jest w stanie obsłużyć
- **Response time** (avg, median, 90/95/99 percentyl): opóźnienie API
- **Error rate**: czy wszystkie żądania zakończyły się `200`
- **Stabilność kolejki**: w panelu RabbitMQ obserwuj `measurements.default` podczas testu, czy Worker nadąża, czy rośnie zaległość
- **DLQ**: po teście z poprawnym kluczem HMAC kolejka `measurements.*.dlq` powinna być pusta

Szczegóły scenariusza i interpretacja wyników: [jmeter/README.md](jmeter/README.md).

### Krok 7: testy jednostkowe

```bash
cd Tests
dotnet test
```

Weryfikują spójność HMAC między FrontApi a TelemetryWorker — fundament walidacji integralności.

---

## Format żądania API

**POST** `http://localhost:5000/measurement`

```json
{
  "payload": "eyJkZXZpY2VJZCI6InNlbnNvci0xIiwidmFsdWUiOjIzLjV9",
  "checksum": "a3f1...(64 znaki hex)...",
  "channel": "temperature"
}
```

| Pole | Opis |
|---|---|
| `payload` | JSON `{"deviceId":"...","value":0.0}` zakodowany w Base64 |
| `checksum` | HMAC-SHA256 zdekodowanego JSON, klucz z `Hmac:SecretKey` |
| `channel` | opcjonalny kanał (domyślnie `default`), mapuje się na kolejkę `measurements.{channel}` |

| Kod | Znaczenie |
|---|---|
| 200 | Wiadomość wysłana do kolejki |
| 400 | Błąd walidacji (szczegóły w polu `error`) |
| 500 | Błąd połączenia z RabbitMQ |

---

## Rozwiązywanie problemów

**Worker nie łączy się z RabbitMQ.** Sprawdź `docker ps` i port 5672 w `appsettings.json`.

**Worker nie zapisuje do InfluxDB.** Zweryfikuj token w `TelemetryWorker/appsettings.Development.json` — patrz sekcja „Konfiguracja lokalna” wyżej. Sprawdź też organizację (`myorg`) i bucket (`telemetry`).

**DLQ się zapełnia.** Prawdopodobnie klucz HMAC jest inny w FrontApi i TelemetryWorker. Sprawdź `Hmac:SecretKey` w obu `appsettings.json`. Podgląd kolejek DLQ: [http://localhost:15672](http://localhost:15672).

**SignalR UI pokazuje „Rozłączony”.** NotificationWebApp nie działa albo port 5002 jest zajęty.

**Webhook z InfluxDB nie dociera.** InfluxDB w kontenerze nie widzi hosta `localhost` — użyj `http://host.docker.internal:5002/webhook/influx`.

---

## Użyte technologie

**.NET 8 / ASP.NET Core** — platforma do budowy wszystkich trzech aplikacji. Dostarcza serwer HTTP, routing, dependency injection i middleware, wykorzystane w FrontApi (kontrolery + middleware logujący) oraz NotificationWebApp (kontroler webhooka + SignalR hub). `BackgroundService` to bazowa klasa dla TelemetryWorkera, pozwala uruchamiać długożyjące procesy w obrębie hosta .NET.

**RabbitMQ** — broker kolejek oparty o AMQP, oddziela nadawcę (FrontApi) od odbiorcy (Worker): API nie czeka na przetworzenie wiadomości, tylko wrzuca ją do kolejki i odpowiada `200`. Wykorzystane mechanizmy: durable queues (kolejki przeżywają restart brokera), persistent messages, ACK/NACK (Worker potwierdza dopiero po zapisie do InfluxDB), dead-letter exchange (błędne wiadomości trafiają do `.dlq`), prefetch count (Worker pobiera jedną wiadomość naraz).

**InfluxDB 2.x** — baza time-series pod zapis dużych ilości pomiarów ze znacznikiem czasu. Dane są zorganizowane w bucket → measurement → tags (tu: `deviceId`) → fields (tu: `value`), zapytania w języku Flux. Ma też wbudowany silnik alertów: Checks monitorujące warunki na danych i Notification Rules wyzwalające akcje, w tym HTTP webhooks.

**SignalR** — biblioteka .NET do komunikacji real-time serwer → klient, korzysta z WebSocket z fallbackiem na SSE / long polling. `AlertHub` rozgłasza metodę `ReceiveAlert` do wszystkich podłączonych przeglądarek, więc gdy webhook z InfluxDB dociera do NotificationWebApp, alert pojawia się w UI bez odświeżania strony.

**HMAC-SHA256** — kryptograficzna suma kontrolna oparta o SHA-256 i współdzielony klucz. Gwarantuje integralność (dane nie zostały zmienione) i autentyczność (nadawca znał klucz). FrontApi tylko przekazuje checksum dalej, weryfikuje go Worker — dzięki temu nawet zmiana danych w RabbitMQ zostanie wykryta i wiadomość trafi do DLQ.

**Base64** — kodowanie binary → ASCII, tu użyte do zapakowania JSON-a w pole tekstowe `payload`, bezpiecznie transportowalne przez JSON/HTTP. Symuluje też realny scenariusz IoT, w którym urządzenia często wysyłają dane binarne zakodowane w Base64.

**Docker + Docker Compose** — uruchamiają RabbitMQ i InfluxDB w izolowanych kontenerach bez lokalnej instalacji. `docker-compose.yml` definiuje oba serwisy, porty, woluminy (dane przeżywają restart kontenera), zmienne środowiskowe oraz healthchecki sprawdzające gotowość przed startem aplikacji.

**Apache JMeter** — testy obciążeniowe symulujące wielu równoległych klientów HTTP. W scenariuszu: Thread Group (liczba wirtualnych użytkowników), CSV Data Set Config (każdy wątek dostaje własny wiersz danych), HTTP Request Sampler, Gaussian Timer (realistyczne odstępy między żądaniami), Assertions oraz Summary Report / View Results Tree do wizualizacji statystyk.

**xUnit** — framework testów jednostkowych dla .NET, weryfikuje że `HmacHelper` w FrontApi i TelemetryWorker produkuje identyczne sumy dla tych samych danych.

**PowerShell** — skrypty pomocnicze w [scripts/](scripts/): `send-measurements.ps1` (demo na żywo, wysyła serię poprawnych i błędnych żądań) oraz `generate-jmeter-csv.ps1` (przygotowuje CSV dla JMetera, licząc HMAC tą samą logiką co klient produkcyjny, z kodowaniem UTF-8 bez BOM wymaganym przez JMeter).
