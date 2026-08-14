# Telemetrus

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![RabbitMQ](https://img.shields.io/badge/RabbitMQ-AMQP-FF6600?logo=rabbitmq&logoColor=white)
![InfluxDB](https://img.shields.io/badge/InfluxDB-2.x-22ADF6?logo=influxdb&logoColor=white)
![SignalR](https://img.shields.io/badge/SignalR-realtime-512BD4)
![xUnit](https://img.shields.io/badge/tests-xUnit-informational)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)

*English version: [README.md](README.md)*

Niewielki rozproszony system telemetryczny do zbierania pomiarów z urządzeń IoT: REST API przyjmuje dane, RabbitMQ je kolejkuje, background worker weryfikuje ich integralność (HMAC-SHA256) i zapisuje w InfluxDB, a aplikacja z SignalR rozsyła alerty progowe do przeglądarki w czasie rzeczywistym.

Zrealizowane jako projekt zespołowy (2 osoby), którego celem było przećwiczenie architektury opartej o kolejki od początku do końca — rozdzielenie producenta od konsumenta, dead-lettering, weryfikacja integralności i push w czasie rzeczywistym — a nie kolejna aplikacja CRUD.

## Spis treści

- [Najważniejsze cechy](#najważniejsze-cechy)
- [Architektura](#architektura)
- [Stos technologiczny](#stos-technologiczny)
- [Struktura projektu](#struktura-projektu)
- [Konfiguracja lokalna](#konfiguracja-lokalna)
- [Uruchomienie](#uruchomienie)
- [Demo alertów w czasie rzeczywistym](#demo-alertów-w-czasie-rzeczywistym)
- [Testy](#testy)
- [Format API](#format-api)
- [Szczegóły komponentów](#szczegóły-komponentów)
- [Rozwiązywanie problemów](#rozwiązywanie-problemów)
- [Bezpieczeństwo](#bezpieczeństwo)
- [Dalszy rozwój](#dalszy-rozwój)
- [Zespół i podział pracy](#zespół-i-podział-pracy)
- [Autorzy i licencja](#autorzy-i-licencja)

## Najważniejsze cechy

- **Architektura oparta o kolejkę** — FrontApi nigdy nie czeka na przetworzenie wiadomości; RabbitMQ rozdziela przyjmowanie danych od ich przetwarzania, z durable queues i persistent messages, więc restart niczego nie gubi.
- **Weryfikacja integralności z automatycznym dead-letteringiem** — każda wiadomość jest sprawdzana przez worker HMAC-SHA256; wszystko uszkodzone, podrobione lub błędnie sformułowane trafia do DLQ per kanał, zamiast być po cichu odrzucone albo wywalić workera.
- **Alerty w czasie rzeczywistym** — threshold check w InfluxDB wywołuje webhook, który hub SignalR rozgłasza do każdej podłączonej przeglądarki przez WebSocket, bez pollingu i odświeżania strony.
- **Przetestowane wydajnościowo, nie tylko zademonstrowane** — plan JMeter generuje 1000 równoległych żądań (20 wątków) przez całą ścieżkę API → kolejka → worker → InfluxDB; szczegóły i wyniki w [jmeter/README.md](jmeter/README.md).
- **Testy jednostkowe logiki integralności** — testy xUnit pilnują, że FrontApi i TelemetryWorker liczą identyczne sumy HMAC — na tym założeniu opiera się cały model bezpieczeństwa.
- **Infrastruktura lokalna jedną komendą** — RabbitMQ i InfluxDB startują przez Docker Compose z healthchecks warunkującymi start aplikacji.

## Architektura

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

Pełny, komponent-po-komponencie opis tego diagramu jest w sekcji [Szczegóły komponentów](#szczegóły-komponentów).

## Stos technologiczny

| Technologia | Zastosowanie |
|---|---|
| **.NET 8 / ASP.NET Core** | REST API (FrontApi), host webhooka + SignalR (NotificationWebApp), worker jako `BackgroundService` (TelemetryWorker) |
| **RabbitMQ** | Broker AMQP rozdzielający producenta od konsumenta — durable queues, persistent messages, ACK/NACK, dead-letter exchange |
| **InfluxDB 2.x** | Baza czasowa dla pomiarów oraz wbudowany silnik alertów (Checks + Notification Rules) |
| **SignalR** | Push w czasie rzeczywistym serwer → przeglądarka (WebSocket, z fallbackiem na SSE/long-polling) |
| **HMAC-SHA256** | Integralność i autentyczność wiadomości między klientem, API i workerem |
| **Docker Compose** | Lokalna infrastruktura RabbitMQ + InfluxDB z healthchecks |
| **Apache JMeter** | Testy obciążeniowe całego pipeline'u pod równoległym ruchem |
| **xUnit** | Testy jednostkowe współdzielonej logiki HMAC |
| **PowerShell** | Skrypty demo i generowania danych testowych |

*Uzasadnienie wyboru każdej z tych technologii — w sekcji [Szczegóły komponentów](#szczegóły-komponentów).*

## Struktura projektu

```
telemetrus/
├── FrontApi/              REST API — walidacja i publikacja pomiarów            (port 5000)
├── TelemetryWorker/       BackgroundService — weryfikacja HMAC, DLQ, zapis do InfluxDB
├── NotificationWebApp/    Odbiornik webhooków + hub SignalR + UI alertów na żywo (port 5002)
├── Tests/                 Testy xUnit spójności HMAC między serwisami
├── jmeter/                Plan testu JMeter, wygenerowane dane testowe, wyniki
├── scripts/                Skrypty PowerShell demo i generowania danych
├── docs/                  Instrukcje konfiguracyjne (alerty InfluxDB)
├── docker-compose.yml     Infrastruktura RabbitMQ + InfluxDB
├── README.md / README.pl.md
└── LICENSE                MIT
```

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
3. `Hmac:SecretKey` w `FrontApi/appsettings.json` i `TelemetryWorker/appsettings.json` to wspólna wartość demonstracyjna (`telemetrus-demo-shared-secret`) używana lokalnie przez oba serwisy i skrypty w `scripts/` — nie chroni realnych danych, więc może zostać taka, jaka jest, do celów demo. W realnym wdrożeniu powinna trafić do menedżera sekretów.

## Uruchomienie

**Wymagania:** .NET SDK 8.0 · Docker + Docker Compose · PowerShell 7+ (skrypty demo) · Apache JMeter 5.6+ (test wydajnościowy)

**1. Uruchom infrastrukturę**

```bash
docker compose up -d
```

- Panel RabbitMQ: [http://localhost:15672](http://localhost:15672) (`guest` / `guest`)
- UI InfluxDB: [http://localhost:8086](http://localhost:8086) (`admin` / `admin12345`)

**2. Uruchom trzy aplikacje .NET** (każda w osobnym terminalu)

```bash
cd FrontApi && dotnet run             # przyjmuje pomiary, port 5000
cd TelemetryWorker && dotnet run      # konsumuje kolejkę, zapisuje do InfluxDB
cd NotificationWebApp && dotnet run   # webhook + SignalR + UI, port 5002
```

**3. Otwórz UI alertów** — [http://localhost:5002](http://localhost:5002). Status w prawym górnym rogu powinien zmienić się na „Połączono” (SignalR).

**4. Wyślij testowe pomiary**

```powershell
pwsh scripts/send-measurements.ps1
```

Skrypt wysyła serię żądań — poprawnych oraz kilku z celowo błędnym checksum. Co warto obejrzeć przy okazji:

1. logi **FrontApi** — dekodowanie Base64 i wysyłka do kolejki
2. logi **TelemetryWorker** — `Checksum OK. Zapisuję do InfluxDB...` dla poprawnych, `[DLQ] Odrzucono wiadomość` dla błędnych
3. **panel RabbitMQ** — kolejki `measurements.*` pustoszeją, `measurements.*.dlq` rośnie
4. **InfluxDB Data Explorer** — bucket `telemetry`, measurement `sensor_reading`, wykres wartości w czasie

## Demo alertów w czasie rzeczywistym

Zgodnie z instrukcją w [docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md):

1. w InfluxDB utwórz **Threshold Check** na `sensor_reading.value` (np. `> 80`)
2. dodaj **HTTP Notification Endpoint** wskazujący na `http://host.docker.internal:5002/webhook/influx`
3. dodaj **Notification Rule** łączącą check z endpointem

Następnie wyślij pomiar przekraczający próg:

```powershell
pwsh scripts/send-measurements.ps1 -HighValue
```

W przeglądarce na `http://localhost:5002` pojawi się alert w czasie rzeczywistym — cała ścieżka od API przez kolejkę, worker, InfluxDB, webhook i SignalR aż do UI zostaje wtedy przećwiczona end-to-end.

## Testy

### Testy jednostkowe

```bash
cd Tests
dotnet test
```

Weryfikują, że FrontApi i TelemetryWorker liczą tę samą sumę HMAC — fundament, na którym opiera się cała walidacja integralności.

### Test wydajnościowy (JMeter)

Scenariusz ([jmeter/telemetrus-load-test.jmx](jmeter/telemetrus-load-test.jmx)) symuluje wiele urządzeń telemetrycznych wysyłających dane równocześnie: **20 wątków**, ramp-up 10 s, 50 iteracji każdy → **1000 żądań łącznie**, z Gaussian Timerem (50 ms ± 20 ms) między żądaniami dla realistycznego obciążenia.

```powershell
# 1. Wygeneruj dane wejściowe (domyślnie 1000 wierszy z poprawnym HMAC)
pwsh scripts/generate-jmeter-csv.ps1 -Count 1000

# 2a. Uruchomienie z GUI (do prezentacji, widać wykres i drzewo żądań)
cd jmeter
jmeter -t telemetrus-load-test.jmx

# 2b. Uruchomienie headless z raportem HTML
jmeter -n -t telemetrus-load-test.jmx -l results.jtl -e -o report
```

Co przeanalizować w raporcie: **throughput** (żądań/s), **response time** (mediana / p90 / p95 / p99), **error rate**, czy kolejka `measurements.default` pozostaje pusta (worker nadąża), oraz czy `measurements.*.dlq` zostaje pusta (poprawny klucz HMAC end-to-end). Pełne szczegóły scenariusza i interpretacja wyników: [jmeter/README.md](jmeter/README.md).

## Format API

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

## Szczegóły komponentów

<details>
<summary><strong>Rozwiń, aby zobaczyć opis każdego komponentu z odnośnikami do kodu</strong></summary>

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
- API **nie weryfikuje** HMAC, to zadanie workera (separation of concerns) — API gwarantuje jedynie poprawność struktury

Błędne żądania kończą się `400 Bad Request` i nie trafiają do kolejki.

### 3. RabbitMQ, porty 5672 / 15672 (management)

Broker asynchronicznej komunikacji. Dla każdego kanału istnieje para kolejek:

- `measurements.{channel}`: kolejka główna (durable, persistent messages)
- `measurements.{channel}.dlq`: dead-letter queue (automatyczne przekierowanie przez `x-dead-letter-exchange`)

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

**Przykładowe zapytania Flux:**

```flux
// Ostatnie 5 minut pomiarów z sensor-1
from(bucket: "telemetry")
  |> range(start: -5m)
  |> filter(fn: (r) => r._measurement == "sensor_reading")
  |> filter(fn: (r) => r.deviceId == "sensor-1")

// Średnia wartość per urządzenie w ostatnich 10 minutach
from(bucket: "telemetry")
  |> range(start: -10m)
  |> filter(fn: (r) => r._measurement == "sensor_reading")
  |> group(columns: ["deviceId"])
  |> mean()
```

### 6. NotificationWebApp ([NotificationWebApp/](NotificationWebApp/)), port 5002

Most między InfluxDB a użytkownikiem końcowym.

- **webhook endpoint** (`POST /webhook/influx`) przyjmuje payload alertu z InfluxDB, wyciąga `_message` i `_level`
- **SignalR hub** (`/alertHub`) rozgłasza alert do wszystkich podłączonych klientów metodą `ReceiveAlert`
- **UI** ([wwwroot/index.html](NotificationWebApp/wwwroot/index.html)): strona w przeglądarce pokazująca status połączenia i listę alertów na żywo, z kolorowaniem wg poziomu (crit/warn/info/ok)

### 7. Przeglądarka, [http://localhost:5002](http://localhost:5002)

Klient SignalR utrzymujący otwarte połączenie WebSocket. Każdy nowy alert z InfluxDB pojawia się natychmiast, bez odświeżania strony.

---

**Dlaczego akurat te technologie:**

- **RabbitMQ**: dead-letter exchange, persistent messages i ACK/NACK są wbudowane, dokładnie to, czego potrzebuje pipeline typu „zweryfikuj, potem zapisz”. Rozdzielenie producenta od konsumenta oznacza, że API zostaje responsywne, nawet gdy worker nie działa.
- **InfluxDB**: zaprojektowana pod duże ilości zapisów ze znacznikiem czasu (bucket → measurement → tags → fields, zapytania w Flux), a wbudowane Checks/Notification Rules oznaczały, że nie trzeba było pisać własnego silnika alertów.
- **SignalR**: standardowy w .NET sposób na push serwer → przeglądarka bez pollingu, z automatyczną negocjacją transportu (WebSocket → SSE → long polling).
- **HMAC-SHA256**: daje jednocześnie integralność i autentyczność z jednego współdzielonego sekretu. FrontApi tylko przekazuje checksum dalej, weryfikuje go worker — dzięki temu nawet zmiana danych w trakcie, gdy leżą w RabbitMQ, zostanie wykryta.
- **Base64**: pakuje JSON w pole tekstowe, które bezpiecznie podróżuje przez JSON/HTTP, i symuluje realny scenariusz IoT, w którym urządzenia często wysyłają dane binarne zakodowane w Base64.

</details>

## Rozwiązywanie problemów

<details>
<summary><strong>Rozwiń, aby zobaczyć typowe problemy i ich rozwiązania</strong></summary>

**Worker nie łączy się z RabbitMQ.** Sprawdź `docker ps` i port 5672 w `appsettings.json`.

**Worker nie zapisuje do InfluxDB.** Zweryfikuj token w `TelemetryWorker/appsettings.Development.json` — patrz [Konfiguracja lokalna](#konfiguracja-lokalna) wyżej. Sprawdź też organizację (`myorg`) i bucket (`telemetry`).

**DLQ się zapełnia.** Prawdopodobnie klucz HMAC jest inny w FrontApi i TelemetryWorker. Sprawdź `Hmac:SecretKey` w obu `appsettings.json`. Podgląd kolejek DLQ: [http://localhost:15672](http://localhost:15672).

**SignalR UI pokazuje „Rozłączony”.** NotificationWebApp nie działa albo port 5002 jest zajęty.

**Webhook z InfluxDB nie dociera.** InfluxDB w kontenerze nie widzi hosta `localhost` — użyj `http://host.docker.internal:5002/webhook/influx`.

</details>

## Bezpieczeństwo

| Zagrożenie | Wektor | Ryzyko | Jak zaadresowane w projekcie |
|---|---|---|---|
| Podrobione pomiary | Atakujący nie zna współdzielonego sekretu | Niskie | Weryfikacja HMAC-SHA256 odrzuca każdą wiadomość z błędnym checksum |
| Przechwytywanie danych | Zwykłe HTTP, brak TLS | Wysokie w niezaufanej sieci | Poza zakresem lokalnego demo; w realnym wdrożeniu TLS na reverse proxy |
| Replay attack | Powtórzenie przechwyconego, poprawnego żądania | Średnie | Obecnie brak mitygacji — wymagałoby znacznika czasu/nonce i idempotency key |
| Zalewanie API (DoS) | Brak rate limiting | Średnie | Test JMeter pokazuje obecny sufit pojedynczej instancji (~210 req/s); przed wdrożeniem publicznym dodać rate limiting |
| Injection przez nazwę kanału | Niezaufane pole `channel` trafia do nazw kolejek | Niskie | Whitelist alphanumeric + `-`, `_`, zanim dotrze do RabbitMQ |
| XSS w UI alertów | Treść alertu z InfluxDB renderowana w przeglądarce | Niskie | Renderowane przez `textContent`, nigdy `innerHTML` |
| Rozproszenie sekretów | Klucz HMAC i token InfluxDB w plikach konfiguracyjnych | Wysokie w produkcji | OK do lokalnego demo (patrz [Konfiguracja lokalna](#konfiguracja-lokalna)); realne wdrożenie wymaga menedżera sekretów (Key Vault / Vault) |

**Co dodalibyśmy przed realnym wdrożeniem:** TLS/HTTPS na całej ścieżce, uwierzytelnianie JWT na API, rate limiting, idempotency key przeciw replay, menedżer sekretów zamiast `appsettings.json`, podpisane payloady webhooków z InfluxDB oraz CORS/CSP w UI alertów.

## Dalszy rozwój

Świadomie zostawione na później, bo nie blokowało pokazania działającego pipeline'u end-to-end:

- **Automatyczne testy integracyjne** — dziś ścieżka API → kolejka → worker → baza jest weryfikowana ręcznie przez `scripts/send-measurements.ps1`; `Testcontainers` uruchamiające RabbitMQ/InfluxDB w testach dałyby powtarzalność w CI.
- **Retry z backoffem** — chwilowy timeout czy `503` od InfluxDB dziś od razu wysyła wiadomość do DLQ; 2-3 próby z exponential backoff najpewniej odzyskałyby większość takich przypadków.
- **Współdzielony `HmacHelper`** — obecnie zduplikowany między FrontApi i TelemetryWorker; wydzielenie projektu `Telemetrus.Common` usunęłoby duplikację kosztem jednego dodatkowego projektu w solucji.
- **Realny monitoring** — dziś tylko logi. Prometheus + Grafana dałyby realny wgląd w głębokość kolejek, tempo trafień do DLQ i opóźnienia przetwarzania.
- **Pipeline CI** — build + `dotnet test` na każdym PR.

## Zespół i podział pracy

Projekt zespołowy, dwie osoby.

| | Jakub Bąk | Martyna Wawak |
|---|---|---|
| Główny obszar | TelemetryWorker (konsument, weryfikacja HMAC, zapis do InfluxDB), NotificationWebApp (webhook, hub SignalR, UI), Docker Compose, konfiguracja alertów InfluxDB | FrontApi (kontroler, middleware, publisher), testy xUnit, plan JMeter i generator CSV |
| Wspólne | Kontrakt `HmacHelper`, schemat `QueueMessage` między FrontApi a workerem, `docker-compose.yml` oraz ta dokumentacja | |

**Proces:** feature-branch workflow (`feature/<nazwa>` → PR → review → `main`), conventional commits (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`), wymagany zielony `dotnet build` + `dotnet test` przed merge'em.

## Autorzy i licencja

Stworzone przez [Jakuba Bąka](https://github.com/jakubbak-online) i Martynę Wawak.

Na licencji [MIT](LICENSE).
