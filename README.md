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
- **Realtime alerting, zero manual setup** — an InfluxDB threshold check fires a webhook that a SignalR hub broadcasts to every connected browser over WebSocket, no polling, no page refresh. The Notification Endpoint, Check, and Notification Rule are provisioned automatically by `AlertSetup` on every `docker compose up` — nothing to click in the InfluxDB UI.
- **Load-tested from the browser or from JMeter** — the alerts UI has a burst panel that drives up to 1000 requests/minute (with a configurable share crossing the alert threshold) straight from `NotificationWebApp`; for heavier scenarios, a JMeter plan drives 1000 concurrent requests (20 threads) through the full API → queue → worker → InfluxDB path — see [jmeter/README.md](jmeter/README.md).
- **Unit-tested integrity logic** — xUnit tests pin down that FrontApi and TelemetryWorker compute identical HMAC checksums, the assumption the whole security model rests on.
- **One-command full stack** — `docker compose up -d --build` runs the entire pipeline (RabbitMQ, InfluxDB, all three .NET apps, and the one-shot alert provisioner) with healthchecks gating startup order, or start just the infra and run the apps locally for hot-reload debugging.

## Architecture

```
[Client / JMeter / burst panel]
      │  POST /measurement  (Base64 payload + HMAC checksum + channel)
      ▼
[FrontApi]  ──►  [RabbitMQ]  ──►  [TelemetryWorker]  ──►  [InfluxDB]  ◄── provisioned once by [AlertSetup]
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
| **Docker Compose** | Full application stack (RabbitMQ, InfluxDB, all three .NET apps, and the one-shot `AlertSetup` provisioner) with healthchecks and service dependencies — or just the infra, for local hot-reload dev |
| **Apache JMeter** | Load-testing the full pipeline under concurrent traffic |
| **xUnit** | Unit tests for the shared HMAC logic |
| **PowerShell** | Demo and test-data generation scripts |

*Why each of these, specifically, is discussed in [Component Deep Dive](#component-deep-dive).*

## Project Structure

```
telemetrus/
├── FrontApi/              REST API — validates & publishes measurements        (port 5000, Dockerfile included)
├── TelemetryWorker/       BackgroundService — HMAC check, DLQ routing, InfluxDB writes  (Dockerfile included)
├── NotificationWebApp/    Webhook receiver + SignalR hub + live alerts UI + burst load panel (port 5002, Dockerfile included)
├── AlertSetup/            One-shot console app — provisions InfluxDB alert Check/Endpoint/Rule (Dockerfile included)
├── Tests/                 xUnit tests for HMAC correctness across services
├── jmeter/                JMeter load-test plan, generated test data, results
├── scripts/                PowerShell demo & test-data generation scripts
├── docs/                  Setup guides (InfluxDB alert configuration — manual walkthrough + what AlertSetup automates)
├── docker-compose.yml     Full stack: RabbitMQ, InfluxDB, all three .NET apps, AlertSetup
├── README.md / README.pl.md
└── LICENSE                MIT
```

## Local Configuration

The repository ships no real secrets, but it does ship working demo defaults, so **Option A needs no setup at all** — `docker compose up -d --build` works straight after cloning.

1. `INFLUXDB_ADMIN_TOKEN` falls back to a built-in demo value (`telemetrus-demo-influxdb-admin-token`) baked into `docker-compose.yml` when no `.env` file is present. That's fine for trying the project out. To use your own token instead (e.g. if you want to reuse this InfluxDB instance for something else), copy `.env.example` to `.env` and fill in a value — for example, generate one with `openssl rand -base64 64` — **before the first `docker compose up`**. InfluxDB sets its admin token only once, the first time its volume is initialized; changing `.env` afterwards has no effect until you `docker compose down -v` (wipes InfluxDB's and RabbitMQ's data) and start fresh.
2. **Only if you run TelemetryWorker locally via `dotnet run`** (Option B below) — create `TelemetryWorker/appsettings.Development.json` with a token value (the same one as in `.env`, if you created one; otherwise the demo default above works too, as long as it matches what InfluxDB was initialized with):
   ```json
   {
     "InfluxDB": { "Token": "<token — see above>" }
   }
   ```
   This file is gitignored; ASP.NET Core loads it automatically alongside `appsettings.json` in the Development environment. Running the full stack via Docker Compose (Option A) injects the token automatically — nothing to paste there.
3. `Hmac:SecretKey` in `FrontApi/appsettings.json` and `TelemetryWorker/appsettings.json` is a shared demo value (`telemetrus-demo-shared-secret`) used locally by both services and by the scripts in `scripts/`. It doesn't protect any real data, so it's fine to leave as-is for demo purposes — in a real deployment it would belong in a secrets manager instead.

## Getting Started

**Requirements:** Docker + Docker Compose (Option A) · additionally .NET SDK 8.0 for Option B · PowerShell 7+ (demo scripts) · Apache JMeter 5.6+ (load test)

Don't run Option A and Option B for the same app at the same time — both bind the same host ports (5000/5002), so the second one to start will fail.

**Service URLs & credentials** (same for both options — these are demo defaults set in `docker-compose.yml`, not real secrets):

| Service | URL | Login |
|---|---|---|
| FrontApi (Swagger) | [http://localhost:5000/swagger](http://localhost:5000/swagger) | — |
| NotificationWebApp (alerts UI) | [http://localhost:5002](http://localhost:5002) | — |
| RabbitMQ management UI | [http://localhost:15672](http://localhost:15672) | `guest` / `guest` |
| InfluxDB UI | [http://localhost:8086](http://localhost:8086) | `admin` / `admin12345` |

### Option A — everything in Docker (fastest way to try it)

```bash
docker compose up -d --build
```

This builds and starts all six services — RabbitMQ, InfluxDB, FrontApi, TelemetryWorker, NotificationWebApp, and the one-shot `AlertSetup` (provisions InfluxDB alerting, then exits — see `docker logs telemetrus-alertsetup`) — with healthchecks gating startup order. No `dotnet run` needed, no `.env` needed either (see [Local Configuration](#local-configuration)). Skip straight to step 3 below.

### Option B — hybrid (infra in Docker, apps local — best for debugging/hot-reload)

**1. Start the infrastructure**

```bash
docker compose up -d rabbitmq influxdb
```

- RabbitMQ management UI: [http://localhost:15672](http://localhost:15672) (`guest` / `guest`)
- InfluxDB UI: [http://localhost:8086](http://localhost:8086) (`admin` / `admin12345`)

**2. Start the three .NET apps** (each in its own terminal)

```bash
cd FrontApi && dotnet run             # accepts measurements, port 5000
cd TelemetryWorker && dotnet run      # consumes the queue, writes to InfluxDB
cd NotificationWebApp && dotnet run   # webhook + SignalR + UI, port 5002
```

### Both options continue here

**3. Open the alerts UI** — [http://localhost:5002](http://localhost:5002). The status indicator should switch to "Connected" (SignalR).

**4. Send test measurements**

Either from the browser — the alerts UI has a **"Wyślij testowy pomiar"** panel (device ID, value, channel, and a "break checksum" checkbox to demo the DLQ path) — or from the terminal:

```powershell
pwsh scripts/send-measurements.ps1
```

The script sends a batch of requests — valid ones, plus a few with a deliberately wrong checksum. Worth watching while either runs:

1. **FrontApi** logs — Base64 decoding and publishing to the queue
2. **TelemetryWorker** logs — `Checksum OK. Zapisuję do InfluxDB...` for valid messages, `[DLQ] Odrzucono wiadomość` for invalid ones
3. **RabbitMQ panel** — `measurements.*` queues drain, `measurements.*.dlq` fills up
4. **InfluxDB Data Explorer** — bucket `telemetry`, measurement `sensor_reading`, values plotted over time

The UI panel doesn't talk to FrontApi directly from the browser — it posts to `NotificationWebApp`'s own `POST /demo/measurement`, which computes the HMAC server-side (same shared secret as FrontApi/TelemetryWorker) and forwards the request to FrontApi, exactly like a real client would. This keeps the shared secret out of browser-side code and exercises the full pipeline unmodified.

## Realtime Alerts Demo

Alerting is provisioned automatically — no clicking through the InfluxDB UI required. On every `docker compose up`, `AlertSetup` creates (or confirms it already created) a Threshold Check on `sensor_reading.value` (CRIT `> 80`, WARN `> 60`, evaluated every minute using `max` — not `mean`, so a single spike isn't averaged away by surrounding normal readings), an HTTP Notification Endpoint pointed at `NotificationWebApp`, and a Notification Rule connecting the two. Check `docker logs telemetrus-alertsetup` to confirm it ran; [docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md) explains exactly what gets created and how to customize thresholds, plus the equivalent manual UI walkthrough if you'd rather configure it by hand (or need to debug it).

Send a measurement that crosses the threshold — the **"Wyślij wysoką wartość (95, demo alertu)"** button in the alerts UI does this in one click, or from the terminal:

```powershell
pwsh scripts/send-measurements.ps1 -HighValue
```

Within up to a minute (the Check's schedule), an alert appears in the browser at `http://localhost:5002` in real time — the whole path from API through the queue, worker, InfluxDB, Check, webhook, and SignalR to the UI is exercised end to end.

### Load test from the browser (burst panel)

The alerts UI also has a **"Test obciążeniowy (burst)"** panel — no JMeter setup needed for a quick load demo. It drives configurable load (defaults: 1000 requests over 60 seconds, ~5% of them exceeding the CRIT threshold) straight from `NotificationWebApp`'s `BurstService`, spread evenly over the chosen duration and fired server-side (not from a browser-tab timer, which throttles heavily once the tab loses focus). Progress streams live over the same SignalR connection the alerts use (`BurstProgress`/`BurstFinished` events) — sent/OK/failed/over-threshold counts update in real time — and any threshold crossings during the run surface as alerts exactly like a single high-value measurement would.

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
- alerting (Threshold Check + Notification Endpoint + Notification Rule) is provisioned automatically by `AlertSetup` — see below — rather than clicked together in the UI; a check (`value > 80` CRIT / `> 60` WARN, aggregated with `max`) plus a notification rule send an HTTP POST (webhook) to NotificationWebApp

### 6. NotificationWebApp ([NotificationWebApp/](NotificationWebApp/)), port 5002

The bridge between InfluxDB and the end user.

- **webhook endpoint** (`POST /webhook/influx`) accepts the alert payload from InfluxDB and pulls out `_message` and `_level`
- **SignalR hub** (`/alertHub`) broadcasts the alert to every connected client via `ReceiveAlert`
- **demo endpoint** (`POST /demo/measurement`) computes the HMAC server-side and forwards a test measurement to FrontApi — backs the "Wyślij testowy pomiar" panel in the UI, an in-browser alternative to `scripts/send-measurements.ps1`
- **burst endpoints** (`POST /demo/burst/start`, `/stop`, `GET /status`) start/stop [BurstService.cs](NotificationWebApp/BurstService.cs), a singleton that paces up to thousands of HMAC-signed measurements per run at a configurable rate and reports live progress over the same SignalR hub (`BurstStarted`/`BurstProgress`/`BurstFinished`) — backs the "Test obciążeniowy" panel
- **UI** ([wwwroot/index.html](NotificationWebApp/wwwroot/index.html)): a browser page showing connection status, a live list of alerts color-coded by level (crit/warn/info/ok), a form to trigger test measurements, and the burst load panel

### 7. AlertSetup ([AlertSetup/](AlertSetup/)) — runs once, then exits

A small console app, not a long-running service. On stack startup (after InfluxDB passes its healthcheck) it calls the InfluxDB REST API to create the Notification Endpoint, Threshold Check, and Notification Rule described in [docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md) — the same three resources you'd otherwise click together by hand. Looks resources up by name first, so re-running it (every `docker compose up` does) is a no-op once they exist.

### 8. Browser, [http://localhost:5002](http://localhost:5002)

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

**Webhook from InfluxDB never arrives.** Check the Notification Endpoint URL against how you started the stack — full Docker mode needs the service name (`http://notificationwebapp:8080/webhook/influx`), hybrid mode needs `http://host.docker.internal:5002/webhook/influx` (InfluxDB's container can't resolve `localhost` as the host machine). A URL from one mode won't resolve in the other; see [docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md) for details.

**`telemetryworker` container keeps restarting.** Check `docker compose logs telemetryworker` — it fails fast (by design) if `InfluxDB:Token` is empty. Under Docker Compose this shouldn't happen even without a `.env` file (the built-in demo token kicks in — see [Local Configuration](#local-configuration)); this mainly bites hybrid mode (Option B) if `TelemetryWorker/appsettings.Development.json` doesn't have a matching token, or if InfluxDB's data volume was initialized with a different token than what you're now passing in (`docker compose down -v` to reset it).

**`frontapi` / `telemetryworker` / `notificationwebapp` image fails to build with a `NETSDK1064: Package ... was not found` error.** This means stale local `bin/`/`obj/` build artifacts got copied into the image and clobbered the container's own NuGet restore. Each service directory has its own `.dockerignore` to prevent this (Docker only honors a `.dockerignore` at the root of the build context, and each service's context is its own subfolder — the repo-root `.dockerignore` doesn't apply here). If you hit this anyway, delete the local `bin/`/`obj/` folders for that service and rebuild with `docker compose build --no-cache <service>`.

**Worker writes are silently going to the DLQ with a 401 Unauthorized in the logs, even though `.env` looks correct.** You likely changed `INFLUXDB_ADMIN_TOKEN` in `.env` *after* the stack had already run once. InfluxDB only sets its admin token the first time its volume is created — a later `.env` change or `docker compose restart` doesn't update it, so the worker's token no longer matches. Fix: `docker compose down -v` (wipes InfluxDB's and RabbitMQ's data, forcing a clean re-init) then `docker compose up -d --build` again.

**Port already in use / container fails to bind 5000 or 5002.** You're probably running both Option A (Docker) and Option B (`dotnet run`) for the same app at once. Stop one before starting the other, e.g. `docker compose stop frontapi`.

**`rabbitmq` container exits right after the very first `docker compose up` on a brand-new volume.** A known first-boot race on some Docker Desktop setups (`.erlang.cookie` permission error in `docker logs telemetrus-rabbitmq`). `restart: unless-stopped` in `docker-compose.yml` brings it back up automatically within a few seconds — `frontapi`/`telemetryworker` just wait longer on their `depends_on: service_healthy` gate. If it doesn't recover on its own, `docker compose up -d` again.

**Sending a high-value measurement doesn't produce an alert.** Check `docker logs telemetrus-alertsetup` — if it shows errors (usually because InfluxDB wasn't ready yet the first time), re-run it: `docker compose up alertsetup`. If it shows all three resources already existing, the Check runs on a 1-minute schedule — wait up to a minute after sending the measurement. If it's a burst of measurements rather than a single one, the Check aggregates with `max` specifically so individual spikes aren't averaged away — a check still using `mean` (e.g. reconfigured by hand) will rarely cross the threshold under load. See [docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md) for what `alertsetup` configures and how to inspect/customize it.

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
