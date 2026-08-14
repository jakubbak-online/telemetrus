# Konfiguracja alertu i webhooka w InfluxDB

Ta instrukcja opisuje jak skonfigurować regułę alertową w InfluxDB 2.x, która przy przekroczeniu progu wartości wyśle webhook do NotificationWebApp.

## 1. Logowanie do InfluxDB

Otwórz: http://localhost:8086
Login: `admin` / Hasło: `admin12345` (zgodnie z docker-compose.yml)

## 2. Weryfikacja że dane dochodzą

Najpierw upewnij się, że Worker zapisuje dane:

1. **Data Explorer** → bucket `telemetry` → measurement `sensor_reading`
2. Powinieneś zobaczyć punkty z polem `value` i tagiem `deviceId`

Jeśli brak danych — wyślij testowe żądanie:
```powershell
pwsh scripts/send-measurements.ps1
```

## 3. Utworzenie Notification Endpoint (webhook)

1. Lewe menu → **Alerts** → zakładka **Notification Endpoints**
2. **Create** → typ: **HTTP**
3. Konfiguracja:
   - **Name:** `Telemetrus WebApp`
   - **URL:** `http://host.docker.internal:5002/webhook/influx`
     > Na Linuxie w Docker użyj: `http://172.17.0.1:5002/webhook/influx`
     > Jeśli NotificationWebApp działa w tym samym kontenerze Docker: użyj nazwy serwisu
   - **HTTP Method:** `POST`
   - **Auth Method:** None
4. **Create Notification Endpoint**

## 4. Utworzenie Check (reguły alertowej)

1. **Alerts** → **Checks** → **Create** → **Threshold Check**
2. **Define Query:**
   - From: bucket `telemetry`
   - Filter: `_measurement = sensor_reading`
   - Filter: `_field = value`
   - Aggregate: `mean` co `1 minute`
3. **Configure Check:**
   - **Name:** `Wysoka wartość pomiaru`
   - **Schedule Every:** `1m`
   - **Thresholds:**
     - **CRIT** jeśli `value > 80`
     - **WARN** jeśli `value > 60`
     - **OK** jeśli `value < 50`
   - **Status Message Template:**
     ```
     ${r._check_name}: ${r._level} - deviceId=${r.deviceId}, value=${r._value}
     ```
4. **Save**

## 5. Utworzenie Notification Rule (powiązanie Check → Endpoint)

1. **Alerts** → **Notification Rules** → **Create**
2. Konfiguracja:
   - **Name:** `Powiadom WebApp o alertach`
   - **Schedule Every:** `1m`
   - **Conditions:** `When status is equal to CRIT or WARN`
   - **Endpoint:** `Telemetrus WebApp` (utworzony w kroku 3)
3. **Create Notification Rule**

## 6. Test całości

1. Uruchom NotificationWebApp: `cd NotificationWebApp && dotnet run`
2. Otwórz UI: http://localhost:5002
3. Wygeneruj pomiar przekraczający próg:
   ```powershell
   # wartość 95 — powyżej progu CRIT (80)
   $json = '{"deviceId":"sensor-high","value":95}'
   $payload = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
   $key = [System.Text.Encoding]::UTF8.GetBytes("telemetrus-demo-shared-secret")
   $data = [System.Text.Encoding]::UTF8.GetBytes($json)
   $hmac = New-Object System.Security.Cryptography.HMACSHA256($key)
   $checksum = ([BitConverter]::ToString($hmac.ComputeHash($data)) -replace '-', '').ToLower()

   Invoke-RestMethod -Uri http://localhost:5000/measurement -Method Post `
     -ContentType application/json `
     -Body (@{payload=$payload; checksum=$checksum} | ConvertTo-Json)
   ```
4. Poczekaj do 1 minuty (Check uruchamia się co minutę)
5. W UI NotificationWebApp powinien pojawić się alert

## 7. Testowanie webhooka bez InfluxDB

Jeśli chcesz zademonstrować SignalR bez czekania na InfluxDB Check, użyj endpointa testowego:

```bash
curl -X POST http://localhost:5002/webhook/test \
  -H "Content-Type: application/json" \
  -d '{"level":"crit","message":"Przekroczony próg temperatury!"}'
```

Alert pojawi się natychmiast w UI.

## Alternatywa: plik JSON (programowa konfiguracja)

Można też skonfigurować alert przez API InfluxDB używając taska Flux — ale UI jest znacznie prostsze dla demo.

## Rozwiązywanie problemów

**Webhook nie dochodzi do WebApp:**
- Sprawdź adres URL: InfluxDB w Dockerze nie widzi `localhost` maszyny hosta — użyj `host.docker.internal`
- Sprawdź logi InfluxDB: `docker logs telemetrus-influxdb`
- Sprawdź czy NotificationWebApp słucha na porcie 5002

**Check się nie uruchamia:**
- W Data Explorer zweryfikuj czy zapytanie zwraca dane
- Sprawdź **Alerts → History** czy są ewaluacje

**Alert pokazany ale nie wysłany:**
- Status `OK` → `WARN/CRIT` tylko **na zmianę stanu**. Jeśli cały czas WARN, nie powtarzają się — dodaj krótki okres OK.
