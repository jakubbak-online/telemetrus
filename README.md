# Telemetrus

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![RabbitMQ](https://img.shields.io/badge/RabbitMQ-AMQP-FF6600?logo=rabbitmq&logoColor=white)
![InfluxDB](https://img.shields.io/badge/InfluxDB-2.x-22ADF6?logo=influxdb&logoColor=white)
![SignalR](https://img.shields.io/badge/SignalR-realtime-512BD4)
![xUnit](https://img.shields.io/badge/tests-xUnit-informational)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)

*Polska wersja: [README.pl.md](README.pl.md)*

A small distributed telemetry pipeline for IoT-style measurements: a REST API accepts readings, RabbitMQ queues them, a background worker verifies their integrity (HMAC-SHA256) and writes them to InfluxDB, and a SignalR app pushes threshold alerts to the browser in real time.

Built as a two-person team project to practice message-driven architecture end to end — producer/consumer decoupling, dead-lettering, integrity verification, and realtime push — rather than a single CRUD app.

## Table of Contents

- [Highlights](#highlights)
- [Architecture](#architecture)
- [Tech Stack](#tech-stack)
- [Project Structure](#project-structure)
- [Local Configuration](#local-configuration)
- [Getting Started](#getting-started)
- [Realtime Alerts Demo](#realtime-alerts-demo)
- [Testing](#testing)
- [API Reference](#api-reference)
- [Component Deep Dive](#component-deep-dive)
- [Troubleshooting](#troubleshooting)
- [Security Considerations](#security-considerations)
- [Roadmap](#roadmap)
- [Team & Contributions](#team--contributions)
- [Authors & License](#authors--license)

## Highlights

- **Message-driven architecture** — FrontApi never blocks on processing; RabbitMQ decouples ingestion from the worker, with durable queues and persistent messages so nothing is lost on a restart.
- **Integrity verification with automatic dead-lettering** — every message is HMAC-SHA256 checked by the worker; anything corrupted, forged, or malformed is routed to a per-channel DLQ instead of silently dropped or crashing the worker.
- **Realtime alerting** — an InfluxDB threshold check fires a webhook that a SignalR hub broadcasts to every connected browser over WebSocket, no polling, no page refresh.
- **Load-tested, not just demoed** — a JMeter plan drives 1000 concurrent requests (20 threads) through the full API → queue → worker → InfluxDB path; see [jmeter/README.md](jmeter/README.md) for the scenario and results.
- **Unit-tested integrity logic** — xUnit tests pin down that FrontApi and TelemetryWorker compute identical HMAC checksums, the assumption the whole security model rests on.
- **One-command local infra** — RabbitMQ and InfluxDB run via Docker Compose with healthchecks gating application startup.

## Architecture

```
[Client / JMeter]
      │  POST /measurement  (Base64 payload + HMAC checksum + channel)
      ▼
[FrontApi]  ──►  [RabbitMQ]  ──►  [TelemetryWorker]  ──►  [InfluxDB]
                                                              │ alert (webhook)
                                                              ▼
                                                   [NotificationWebApp]
                                                              │ SignalR
                                                              ▼
                                                     [Browser / UI]
```

A full step-by-step walkthrough of every component in this diagram is in [Component Deep Dive](#component-deep-dive).

## Tech Stack

| Technology | Used for |
|---|---|
| **.NET 8 / ASP.NET Core** | REST API (FrontApi), webhook + SignalR host (NotificationWebApp), `BackgroundService` worker (TelemetryWorker) |
| **RabbitMQ** | AMQP broker decoupling producer and consumer — durable queues, persistent messages, ACK/NACK, dead-letter exchange |
| **InfluxDB 2.x** | Time-series storage for readings, plus its built-in Checks + Notification Rules alerting engine |
| **SignalR** | Realtime server → browser push (WebSocket, with SSE/long-polling fallback) |
| **HMAC-SHA256** | Message integrity & authenticity between client, API, and worker |
| **Docker Compose** | Local RabbitMQ + InfluxDB infrastructure with healthchecks |
| **Apache JMeter** | Load-testing the full pipeline under concurrent traffic |
| **xUnit** | Unit tests for the shared HMAC logic |
| **PowerShell** | Demo and test-data generation scripts |

*Why each of these, specifically, is discussed in [Component Deep Dive](#component-deep-dive).*

## Project Structure

```
telemetrus/
├── FrontApi/              REST API — validates & publishes measurements        (port 5000)
├── TelemetryWorker/       BackgroundService — HMAC check, DLQ routing, InfluxDB writes
├── NotificationWebApp/    Webhook receiver + SignalR hub + live alerts UI      (port 5002)
├── Tests/                 xUnit tests for HMAC correctness across services
├── jmeter/                JMeter load-test plan, generated test data, results
├── scripts/                PowerShell demo & test-data generation scripts
├── docs/                  Setup guides (InfluxDB alert configuration)
├── docker-compose.yml     RabbitMQ + InfluxDB infrastructure
├── dokumentacja.md        Extended project write-up (Polish, academic report)
├── README.md / README.pl.md
└── LICENSE                MIT
```

## Local Configuration

The repository ships no real secrets. Before running it for the first time:

1. Copy `.env.example` to `.env` and fill in your own InfluxDB token (for example, generate one with `openssl rand -base64 64`).
2. Create `TelemetryWorker/appsettings.Development.json` with the same token value:
   ```json
   {
     "InfluxDB": { "Token": "<same value as in .env>" }
   }
   ```
   This file is gitignored; ASP.NET Core loads it automatically alongside `appsettings.json` in the Development environment.
3. `Hmac:SecretKey` in `FrontApi/appsettings.json` and `TelemetryWorker/appsettings.json` is a shared demo value (`telemetrus-demo-shared-secret`) used locally by both services and by the scripts in `scripts/`. It doesn't protect any real data, so it's fine to leave as-is for demo purposes — in a real deployment it would belong in a secrets manager instead.

## Getting Started

**Requirements:** .NET SDK 8.0 · Docker + Docker Compose · PowerShell 7+ (demo scripts) · Apache JMeter 5.6+ (load test)

**1. Start the infrastructure**

```bash
docker compose up -d
```

- RabbitMQ management UI: [http://localhost:15672](http://localhost:15672) (`guest` / `guest`)
- InfluxDB UI: [http://localhost:8086](http://localhost:8086) (`admin` / `admin12345`)

**2. Start the three .NET apps** (each in its own terminal)

```bash
cd FrontApi && dotnet run             # accepts measurements, port 5000
cd TelemetryWorker && dotnet run      # consumes the queue, writes to InfluxDB
cd NotificationWebApp && dotnet run   # webhook + SignalR + UI, port 5002
```

**3. Open the alerts UI** — [http://localhost:5002](http://localhost:5002). The status indicator should switch to "Connected" (SignalR).

**4. Send test measurements**

```powershell
pwsh scripts/send-measurements.ps1
```

This sends a batch of requests — valid ones, plus a few with a deliberately wrong checksum. Worth watching while it runs:

1. **FrontApi** logs — Base64 decoding and publishing to the queue
2. **TelemetryWorker** logs — `Checksum OK. Zapisuję do InfluxDB...` for valid messages, `[DLQ] Odrzucono wiadomość` for invalid ones
3. **RabbitMQ panel** — `measurements.*` queues drain, `measurements.*.dlq` fills up
4. **InfluxDB Data Explorer** — bucket `telemetry`, measurement `sensor_reading`, values plotted over time

## Realtime Alerts Demo

Following [docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md):

1. In InfluxDB, create a **Threshold Check** on `sensor_reading.value` (e.g. `> 80`).
2. Add an **HTTP Notification Endpoint** pointing at `http://host.docker.internal:5002/webhook/influx`.
3. Add a **Notification Rule** connecting the check to the endpoint.

Then send a measurement that crosses the threshold:

```powershell
pwsh scripts/send-measurements.ps1 -HighValue
```

An alert appears in the browser at `http://localhost:5002` in real time — the whole path from API through the queue, worker, InfluxDB, webhook, and SignalR to the UI is exercised end to end.

## Testing

### Unit tests

```bash
cd Tests
dotnet test
```

Verifies that FrontApi and TelemetryWorker agree on the HMAC computation — the foundation the whole integrity check rests on.

### Load test (JMeter)

The scenario ([jmeter/telemetrus-load-test.jmx](jmeter/telemetrus-load-test.jmx)) simulates many telemetry devices sending data concurrently: **20 threads**, 10 s ramp-up, 50 iterations each → **1000 requests total**, with a Gaussian timer (50 ms ± 20 ms) between requests to approximate realistic load.

```powershell
# 1. Generate input data (1000 rows with a valid HMAC by default)
pwsh scripts/generate-jmeter-csv.ps1 -Count 1000

# 2a. GUI run (live demo — chart + request tree)
cd jmeter
jmeter -t telemetrus-load-test.jmx

# 2b. Headless run with an HTML report
jmeter -n -t telemetrus-load-test.jmx -l results.jtl -e -o report
```

What to look at in the report: **throughput** (req/s), **response time** (median / p90 / p95 / p99), **error rate**, whether the `measurements.default` queue stays drained (worker keeping up), and whether `measurements.*.dlq` stays empty (correct HMAC key end to end). Full scenario details and how to read the results: [jmeter/README.md](jmeter/README.md).

## API Reference

**POST** `http://localhost:5000/measurement`

```json
{
  "payload": "eyJkZXZpY2VJZCI6InNlbnNvci0xIiwidmFsdWUiOjIzLjV9",
  "checksum": "a3f1...(64 hex characters)...",
  "channel": "temperature"
}
```

| Field | Description |
|---|---|
| `payload` | the JSON `{"deviceId":"...","value":0.0}`, Base64-encoded |
| `checksum` | HMAC-SHA256 of the decoded JSON, keyed with `Hmac:SecretKey` |
| `channel` | optional channel name (defaults to `default`), maps to the `measurements.{channel}` queue |

| Code | Meaning |
|---|---|
| 200 | Message published to the queue |
| 400 | Validation error (details in the `error` field) |
| 500 | Could not connect to RabbitMQ |

## Component Deep Dive

<details>
<summary><strong>Expand for a component-by-component walkthrough with source links</strong></summary>

### 1. Client (JMeter or a PowerShell script)

Builds a measurement, encodes it, and sends it to the API.

- creates the JSON: `{"deviceId":"sensor-1","value":23.5}`
- computes `checksum = HMAC-SHA256(json, SecretKey)`
- Base64-encodes the JSON into `payload`
- sends `POST /measurement` with `{ payload, checksum, channel }`

### 2. FrontApi ([FrontApi/](FrontApi/)), port 5000

The REST API that accepts incoming data, validates it, and publishes it to the queue.

- **middleware** ([Program.cs:19-39](FrontApi/Program.cs#L19-L39)) logs every request (method, path, first 200 characters of the body)
- **validation** ([MeasurementController.cs](FrontApi/Controllers/MeasurementController.cs)) checks that `payload` and `checksum` are present, that `channel` has a valid name, decodes Base64, parses the JSON, and requires `deviceId` and a numeric `value`
- **publisher** ([RabbitMqPublisher.cs](FrontApi/RabbitMqPublisher.cs)) sends the message to `measurements.{channel}` with `persistent=true`; queues are declared lazily on first publish
- the API does **not** verify the HMAC, that is the worker's job (separation of concerns): the API only guarantees the message is well-formed

Invalid requests get a `400 Bad Request` and never reach the queue.

### 3. RabbitMQ, ports 5672 / 15672 (management)

The message broker. Every channel gets a pair of queues:

- `measurements.{channel}`: the main queue (durable, persistent messages)
- `measurements.{channel}.dlq`: dead-letter queue, populated automatically via `x-dead-letter-exchange`

### 4. TelemetryWorker ([TelemetryWorker/](TelemetryWorker/))

Consumes the queues, checks message integrity, and writes valid readings to the database. Runs as a `BackgroundService`.

- connects to RabbitMQ and declares queues for every configured channel ([Worker.cs:25-53](TelemetryWorker/Worker.cs#L25-L53))
- `BasicQos(prefetchCount=1)`: processes one message at a time
- for each message:
  1. **Deserialize** the JSON into `QueueMessage { Data, Checksum }`. On failure → `BasicNack(requeue=false)` → DLQ.
  2. **Verify the HMAC** ([Worker.cs:111-124](TelemetryWorker/Worker.cs#L111-L124)): computes `expected = HMAC-SHA256(Data, SecretKey)`. Mismatch → DLQ.
  3. **Write to InfluxDB** ([InfluxWriter.cs](TelemetryWorker/InfluxWriter.cs)): builds a `PointData` with the `deviceId` tag and `value` field, millisecond precision.
  4. `BasicAck` only after a successful write.

Because structural errors are already filtered out by the API, the DLQ ends up holding only integrity failures or corrupted messages.

### 5. InfluxDB, port 8086

The time-series database. Stores readings in the `telemetry` bucket (organization `myorg`).

- each reading is a point: `sensor_reading,deviceId=<id> value=<float> <timestamp>`
- alerts are configured in the InfluxDB UI ([docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md)): a check (e.g. `value > 80`) plus a notification rule send an HTTP POST (webhook) to NotificationWebApp

### 6. NotificationWebApp ([NotificationWebApp/](NotificationWebApp/)), port 5002

The bridge between InfluxDB and the end user.

- **webhook endpoint** (`POST /webhook/influx`) accepts the alert payload from InfluxDB and pulls out `_message` and `_level`
- **SignalR hub** (`/alertHub`) broadcasts the alert to every connected client via `ReceiveAlert`
- **UI** ([wwwroot/index.html](NotificationWebApp/wwwroot/index.html)): a browser page showing connection status and a live list of alerts, color-coded by level (crit/warn/info/ok)

### 7. Browser, [http://localhost:5002](http://localhost:5002)

A SignalR client holding an open WebSocket connection. Every new alert from InfluxDB shows up immediately, with no page refresh.

---

**Why these technologies, specifically:**

- **RabbitMQ**: a dead-letter exchange, persistent messages, and ACK/NACK come for free, exactly what a "verify then store" pipeline needs. Decoupling producer from consumer means the API stays responsive even if the worker is down.
- **InfluxDB**: purpose-built for high-volume timestamped writes (bucket → measurement → tags → fields, queried with Flux), and its built-in Checks/Notification Rules meant no custom alerting engine had to be written.
- **SignalR**: the standard .NET way to push server → browser without polling, with automatic transport negotiation (WebSocket → SSE → long polling).
- **HMAC-SHA256**: gives both integrity and authenticity from a single shared secret. FrontApi only forwards the checksum; the worker verifies it, so a change to the data while it sits in RabbitMQ still gets caught.
- **Base64**: packs the JSON payload into a text field that travels safely over JSON/HTTP, and mirrors a realistic IoT scenario where devices often send binary sensor data Base64-encoded.

</details>

## Troubleshooting

<details>
<summary><strong>Expand for common issues and fixes</strong></summary>

**Worker can't connect to RabbitMQ.** Check `docker ps` and the port 5672 setting in `appsettings.json`.

**Worker isn't writing to InfluxDB.** Check the token in `TelemetryWorker/appsettings.Development.json`, see [Local Configuration](#local-configuration) above. Also check the organization (`myorg`) and bucket (`telemetry`).

**DLQ keeps filling up.** The HMAC key is probably different between FrontApi and TelemetryWorker. Check `Hmac:SecretKey` in both `appsettings.json` files. Inspect the DLQ queues at [http://localhost:15672](http://localhost:15672).

**SignalR UI shows "Rozłączony" (Disconnected).** NotificationWebApp isn't running, or port 5002 is already in use.

**Webhook from InfluxDB never arrives.** The InfluxDB container can't resolve `localhost` as the host machine, use `http://host.docker.internal:5002/webhook/influx` instead.

</details>

## Security Considerations

| Threat | Vector | Risk | Mitigation in this project |
|---|---|---|---|
| Forged measurements | Attacker doesn't know the shared secret | Low | HMAC-SHA256 verification rejects any message with a wrong checksum |
| Traffic interception | Plain HTTP, no TLS | High on untrusted networks | Out of scope for a local demo; terminate TLS at a reverse proxy for real deployment |
| Replay attacks | Resending a captured valid request | Medium | Not currently mitigated — would need a timestamp/nonce plus an idempotency key |
| API flooding (DoS) | No rate limiting | Medium | The JMeter test surfaces the current single-instance ceiling (~200 req/s); add rate limiting before an internet-facing deployment |
| Injection via channel name | Untrusted `channel` field feeds into queue names | Low | Whitelisted to alphanumerics, `-`, `_` before it ever reaches RabbitMQ |
| XSS in the alerts UI | Alert text from InfluxDB rendered in the browser | Low | Rendered via `textContent`, never `innerHTML` |
| Secret sprawl | HMAC key & InfluxDB token live in config files | High in production | Fine for a local demo (see [Local Configuration](#local-configuration)); a real deployment needs a secrets manager (Key Vault / Vault) |

**Production hardening we'd add before a real deployment:** TLS/HTTPS end to end, JWT bearer auth on the API, rate limiting, idempotency keys against replay, a secrets manager instead of `appsettings.json`, signed webhook payloads from InfluxDB, and CORS/CSP on the alerts UI.

## Roadmap

Deliberately left for later, since none of it blocked showing a working end-to-end pipeline:

- **Automated integration tests** — today the API → queue → worker → DB path is verified manually via `scripts/send-measurements.ps1`; `Testcontainers` spinning up RabbitMQ/InfluxDB in tests would make this repeatable in CI.
- **Retry with backoff** — a transient InfluxDB timeout or `503` currently sends the message straight to the DLQ; 2-3 retries with exponential backoff would likely recover most of those.
- **Shared `HmacHelper`** — currently duplicated between FrontApi and TelemetryWorker; extracting a `Telemetrus.Common` project would remove the duplication at the cost of one more project in the solution.
- **Real observability** — logs only, today. Prometheus + Grafana would give actual visibility into queue depth, DLQ rate, and processing latency.
- **A CI pipeline** — build + `dotnet test` on every PR.

## Team & Contributions

A two-person team project.

| | Jakub Bąk | Martyna Wawak |
|---|---|---|
| Primary focus | TelemetryWorker (consumer, HMAC check, InfluxDB writer), NotificationWebApp (webhook, SignalR hub, UI), Docker Compose, InfluxDB alert setup | FrontApi (controller, middleware, publisher), xUnit tests, JMeter plan and CSV generator |
| Shared | `HmacHelper` contract, the `QueueMessage` schema between FrontApi and the worker, `docker-compose.yml`, and this documentation | |

**Process:** a feature-branch workflow (`feature/<name>` → PR → review → `main`), conventional commits (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`), and a green `dotnet build` + `dotnet test` required before merge.

## Authors & License

Built by [Jakub Bąk](https://github.com/jakubbak-online) and Martyna Wawak.

Licensed under the [MIT License](LICENSE).
