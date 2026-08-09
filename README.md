# Telemetrus

*Polska wersja: [README.pl.md](README.pl.md)*

A telemetry pipeline for IoT-style measurements: a REST API accepts readings, RabbitMQ queues them, a background worker checks their integrity (HMAC-SHA256) and writes them to InfluxDB, and a SignalR app pushes threshold alerts to the browser in real time.

## Data flow

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
- the API does **not** verify the HMAC, that is the Worker's job (separation of concerns): the API only guarantees the message is well-formed

Invalid requests get a `400 Bad Request` and never reach the queue.

### 3. RabbitMQ, ports 5672 / 15672 (management)

The message broker. Every channel gets a pair of queues:

- `measurements.{channel}`: the main queue (durable, persistent messages)
- `measurements.{channel}.dlq`: dead-letter queue, populated automatically via `x-dead-letter-exchange`

Management UI: [http://localhost:15672](http://localhost:15672) (guest/guest).

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

## Local configuration

The repository does not ship any real secrets. Before running it for the first time:

1. Copy `.env.example` to `.env` and fill in your own InfluxDB token (for example, generate one with `openssl rand -base64 64`).
2. Create `TelemetryWorker/appsettings.Development.json` with the same token value:
   ```json
   {
     "InfluxDB": { "Token": "<same value as in .env>" }
   }
   ```
   This file is gitignored; ASP.NET Core loads it automatically alongside `appsettings.json` in the Development environment.
3. `Hmac:SecretKey` in `FrontApi/appsettings.json` and `TelemetryWorker/appsettings.json` is a shared demo value (`telemetrus-demo-shared-secret`) used locally by both services and by the scripts in `scripts/`. It doesn't protect any real data, so it's fine to leave as-is for demo purposes. In a real deployment it would belong in a secrets manager instead (see the technologies section below).

## Running and demoing the system

### Requirements

- .NET SDK 8.0
- Docker + Docker Compose
- PowerShell 7+ (for the demo scripts)
- Apache JMeter 5.6+ (for the load test)

### Step 1: infrastructure (RabbitMQ + InfluxDB)

```bash
docker compose up -d
```

Check that it's up:

- RabbitMQ: [http://localhost:15672](http://localhost:15672), login `guest` / `guest`
- InfluxDB: [http://localhost:8086](http://localhost:8086), login `admin` / `admin12345`

### Step 2: start the three .NET apps (each in its own terminal)

**Terminal 1, FrontApi** (accepts measurements):

```bash
cd FrontApi
dotnet run
```

**Terminal 2, TelemetryWorker** (consumes the queue, writes to InfluxDB):

```bash
cd TelemetryWorker
dotnet run
```

**Terminal 3, NotificationWebApp** (webhook + SignalR + UI):

```bash
cd NotificationWebApp
dotnet run
```

### Step 3: open the alerts UI

[http://localhost:5002](http://localhost:5002)

The status indicator in the top-right corner should switch to "Connected" (SignalR).

### Step 4: send some test measurements

```powershell
pwsh scripts/send-measurements.ps1
```

The script sends a batch of requests: valid ones, plus a few with a deliberately wrong checksum.

Worth watching while it runs:

1. **FrontApi** logs: Base64 decoding and publishing to the queue
2. **TelemetryWorker** logs: `Checksum OK. Zapisuję do InfluxDB...` for valid messages, `[DLQ] Odrzucono wiadomość — błąd integralności HMAC` for invalid ones
3. the **RabbitMQ** panel ([http://localhost:15672](http://localhost:15672)): `measurements.*` queues drain, `measurements.*.dlq` fills up
4. the **InfluxDB Data Explorer** ([http://localhost:8086](http://localhost:8086)): bucket `telemetry`, measurement `sensor_reading`, a chart of values over time

### Step 5: configure an alert and watch the realtime UI

Following [docs/influxdb-alert-setup.md](docs/influxdb-alert-setup.md):

1. in InfluxDB, create a **Threshold Check** on `sensor_reading.value` with a threshold (e.g. `> 80`)
2. add an **HTTP Notification Endpoint** pointing at `http://host.docker.internal:5002/webhook/influx`
3. add a **Notification Rule** connecting the check to the endpoint

Then send a measurement that crosses the threshold:

```powershell
pwsh scripts/send-measurements.ps1 -HighValue
```

An alert will appear in the browser at `http://localhost:5002` in real time: the whole path from API through the queue, Worker, InfluxDB, webhook, and SignalR to the UI is now exercised end to end.

### Step 6: JMeter load test

The JMeter scenario simulates many telemetry devices sending data concurrently and checks how the whole API → queue → Worker → InfluxDB pipeline holds up under load.

**Scenario** ([jmeter/telemetrus-load-test.jmx](jmeter/telemetrus-load-test.jmx)):

- **Thread Group**: 20 threads, 10 s ramp-up, 50 iterations each → 1000 requests total
- **CSV Data Set Config**: each thread reads its own rows from `test-data.csv` (payload, checksum, channel)
- **HTTP Request Sampler**: `POST http://localhost:5000/measurement` with the JSON body from the CSV
- **Gaussian Random Timer**: 50 ms ± 20 ms delay between requests, to approximate realistic load
- **Response Assertion**: expects a `200` status code
- **View Results Tree + Summary Report**: visualizes results and aggregate stats

**Running it:**

```powershell
# 1. Generate the input data (1000 rows with a valid HMAC by default)
pwsh scripts/generate-jmeter-csv.ps1 -Count 1000

# 2a. Run with the GUI (for a live demo, shows the chart and request tree)
cd jmeter
jmeter -t telemetrus-load-test.jmx

# 2b. Run headless with an HTML report (for documentation)
jmeter -n -t telemetrus-load-test.jmx -l results.jtl -e -o report
```

**What to look at in the report:**

- **Throughput** (requests/s): how many messages the API can handle
- **Response time** (avg, median, 90th/95th/99th percentile): API latency
- **Error rate**: whether every request ended with `200`
- **Queue stability**: watch `measurements.default` in the RabbitMQ panel during the test, does the Worker keep up or does a backlog build?
- **DLQ**: with a correct HMAC key, `measurements.*.dlq` should stay empty after the test

Scenario details and how to read the results: [jmeter/README.md](jmeter/README.md).

### Step 7: unit tests

```bash
cd Tests
dotnet test
```

These verify that FrontApi and TelemetryWorker agree on the HMAC computation, the foundation the whole integrity check rests on.

---

## API request format

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

---

## Troubleshooting

**Worker can't connect to RabbitMQ.** Check `docker ps` and the port 5672 setting in `appsettings.json`.

**Worker isn't writing to InfluxDB.** Check the token in `TelemetryWorker/appsettings.Development.json`, see "Local configuration" above. Also check the organization (`myorg`) and bucket (`telemetry`).

**DLQ keeps filling up.** The HMAC key is probably different between FrontApi and TelemetryWorker. Check `Hmac:SecretKey` in both `appsettings.json` files. Inspect the DLQ queues at [http://localhost:15672](http://localhost:15672).

**SignalR UI shows "Rozłączony" (Disconnected).** NotificationWebApp isn't running, or port 5002 is already in use.

**Webhook from InfluxDB never arrives.** The InfluxDB container can't resolve `localhost` as the host machine, use `http://host.docker.internal:5002/webhook/influx` instead.

---

## Technologies used

**.NET 8 / ASP.NET Core** powers all three applications. It provides the HTTP server, routing, dependency injection and middleware, used in FrontApi (controllers plus the logging middleware) and in NotificationWebApp (the webhook controller and the SignalR hub). `BackgroundService` is the base class for TelemetryWorker, letting it run a long-lived process inside the .NET host.

**RabbitMQ** is the AMQP broker that decouples the producer (FrontApi) from the consumer (Worker): the API doesn't wait for a message to be processed, it just drops it on the queue and returns `200`. Mechanisms in use: durable queues (survive a broker restart), persistent messages, ACK/NACK (the Worker only acknowledges after a successful InfluxDB write), a dead-letter exchange (bad messages land in `.dlq`), and prefetch count (the Worker pulls one message at a time).

**InfluxDB 2.x** is a time-series database built for high-volume timestamped writes. Data is organized as bucket → measurement → tags (here, `deviceId`) → fields (here, `value`), queried with Flux. It also ships a built-in alerting engine: Checks that watch conditions on the data, and Notification Rules that trigger actions, including HTTP webhooks.

**SignalR** is the .NET library for real-time server-to-client communication, using WebSocket with a fallback to SSE / long polling. `AlertHub` broadcasts a `ReceiveAlert` call to every connected browser, so once a webhook from InfluxDB reaches NotificationWebApp, the alert shows up in the UI without a page reload.

**HMAC-SHA256** is a cryptographic checksum built from SHA-256 and a shared secret key. It guarantees both integrity (the data wasn't altered) and authenticity (the sender knew the key). FrontApi only forwards the checksum; the Worker is the one that verifies it, so even a change to the data while it sits in RabbitMQ gets caught and routed to the DLQ.

**Base64** is a binary-to-ASCII encoding, used here to pack the JSON payload into a text field that travels safely over JSON/HTTP. It also mirrors a realistic IoT scenario, where devices often send binary sensor data Base64-encoded.

**Docker + Docker Compose** run RabbitMQ and InfluxDB in isolated containers with no local install required. `docker-compose.yml` defines both services, their ports, volumes (so data survives a container restart), environment variables, and healthchecks that gate application startup on the services actually being ready.

**Apache JMeter** drives the load test, simulating many concurrent HTTP clients. The scenario uses a Thread Group (number of virtual users), a CSV Data Set Config (each thread gets its own row of data), an HTTP Request Sampler, a Gaussian Timer (realistic gaps between requests), assertions, and a Summary Report / View Results Tree for the resulting statistics.

**xUnit** is the .NET unit testing framework, used here to check that `HmacHelper` in FrontApi and in TelemetryWorker produce identical checksums for the same input.

**PowerShell** backs the helper scripts in [scripts/](scripts/): `send-measurements.ps1` (a live demo that sends a mix of valid and invalid requests) and `generate-jmeter-csv.ps1` (builds the JMeter CSV, computing the HMAC with the same logic as the real client, encoded as UTF-8 without a BOM, which JMeter requires).
