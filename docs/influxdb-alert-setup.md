# Konfiguracja alertu i webhooka w InfluxDB

> **Od `docker compose up` to dzieje się automatycznie.** Usługa `alertsetup` (projekt
> [AlertSetup/](../AlertSetup/)) tworzy Notification Endpoint, Threshold Check i Notification Rule
> opisane niżej przez REST API InfluxDB, przy każdym starcie stacku — nic nie trzeba klikać ręcznie.
> Ten dokument zostaje jako: (a) wyjaśnienie co dokładnie `alertsetup` konfiguruje i dlaczego,
> (b) instrukcja, gdy chcesz zmienić progi/nazwy przez UI zamiast edytować [AlertSetup/Program.cs](../AlertSetup/Program.cs),
> (c) ścieżka ręczna, gdyby `alertsetup` z jakiegoś powodu zawiódł (patrz [Rozwiązywanie problemów](#rozwiązywanie-problemów) niżej).

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
   - **URL** zależy od trybu uruchomienia:
     - **Cały stack w Dockerze** (`docker compose up -d --build`, tryb domyślny): `http://notificationwebapp:8080/webhook/influx`
       (nazwa serwisu z docker-compose.yml + wewnętrzny port kontenera 8080 — InfluxDB i NotificationWebApp są w tej samej sieci Docker)
     - **Tryb hybrydowy** (NotificationWebApp uruchomiony lokalnie przez `dotnet run`): `http://host.docker.internal:5002/webhook/influx`
       > Na Linuxie w Docker użyj: `http://172.17.0.1:5002/webhook/influx`
   - **HTTP Method:** `POST`
   - **Auth Method:** None
4. **Create Notification Endpoint**

## 4. Utworzenie Check (reguły alertowej)

1. **Alerts** → **Checks** → **Create** → **Threshold Check**
2. **Define Query:**
   - From: bucket `telemetry`
   - Filter: `_measurement = sensor_reading`
   - Filter: `_field = value`
   - Aggregate: `max` co `1 minute` (NIE `mean` — uśrednianie rozmywa pojedyncze skoki przy większym ruchu, np. w panelu „Test obciążeniowy”; `max` łapie każdy odczyt powyżej progu)
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

1. Uruchom NotificationWebApp: `cd NotificationWebApp && dotnet run` (tylko w trybie hybrydowym — w pełnym Dockerze już działa dzięki `docker compose up -d --build`)
2. Otwórz UI: http://localhost:5002
3. Wygeneruj pomiar przekraczający próg — najprościej: `pwsh scripts/send-measurements.ps1 -HighValue` (dokłada scenariusz z wartością 95). Albo ręcznie:
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

## Automatyczna konfiguracja (AlertSetup)

To, co kroki 3-5 każą kliknąć ręcznie, robi automatycznie przez REST API InfluxDB usługa
`alertsetup` — mały konsolowy projekt .NET w [AlertSetup/](../AlertSetup/), uruchamiany raz przy
każdym `docker compose up` (po tym, jak `influxdb` przejdzie healthcheck), zaraz kończący
działanie (exit 0 — to normalne, nie długo działająca usługa). Idempotentny: szuka zasobów po
nazwie przed utworzeniem, więc bezpieczny do wielokrotnego uruchamiania.

Żeby zmienić progi (`> 80` / `> 60` / `< 50`), nazwy zasobów albo harmonogram — edytuj
[AlertSetup/Program.cs](../AlertSetup/Program.cs) i przebuduj: `docker compose up -d --build alertsetup`
(albo po prostu zrób to ręcznie w UI wg kroków 3-5 wyżej — `alertsetup` wykrywa istniejące zasoby
po nazwie i nie tworzy duplikatów, ale też nie nadpisuje ręcznych zmian w już istniejących).

**Pułapka przy tworzeniu Check przez API, nie UI:** Check utworzony przez `POST /api/v2/checks`
bez pola `tags` (a UI je ustawia automatycznie) generuje zadanie Flux z `tags: {}`. W efekcie
`${r.deviceId}` ORAZ `${r._value}` w `statusMessageTemplate` bywają `null` przy realnych danych i
wywalają task błędem `interpolated expression produced a null value` — ale TYLKO gdy jest
faktycznie coś do zaraportowania (okna bez przekroczenia progu "udają", że działa, bo nie ma nic
do interpolacji). Dlatego `AlertSetup`'s `statusMessageTemplate` używa tylko bezpiecznych
`${r._check_name}` i `${r._level}` — `deviceId`/`value` i tak trafiają do webhooka jako osobne pola
najwyższego poziomu, więc UI pokazuje je w sekcji „raw” alertu.

## Rozwiązywanie problemów

**Alert w ogóle się nie pojawia mimo wysokiej wartości:** Sprawdź czy `alertsetup` faktycznie
skonfigurował zasoby: `docker logs telemetrus-alertsetup`. Jeśli tam błąd (np. InfluxDB nie
zdążyło wystartować) — uruchom ponownie: `docker compose up alertsetup`. Jeśli zasoby istnieją,
ale alert i tak nie przychodzi w ciągu ~1 minuty — sprawdź logi taska Check w UI InfluxDB
(**Alerts → Checks → [nazwa checka] → widok historii uruchomień**) pod kątem błędów runtime.

**Webhook nie dochodzi do WebApp:**
- Sprawdź adres URL zgodnie z trybem uruchomienia (patrz krok 3 wyżej):
  - cały stack w Dockerze → `http://notificationwebapp:8080/webhook/influx` (nazwa serwisu zadziała tylko wewnątrz sieci Docker)
  - tryb hybrydowy → InfluxDB w Dockerze nie widzi `localhost` maszyny hosta, użyj `http://host.docker.internal:5002/webhook/influx`
  - adres z jednego trybu nie zadziała w drugim — to najczęstsza przyczyna błędu po przełączeniu się między trybami
- Sprawdź logi InfluxDB: `docker logs telemetrus-influxdb`
- Sprawdź logi NotificationWebApp: `docker logs telemetrus-notificationwebapp` (tryb Docker) lub konsolę `dotnet run` (tryb hybrydowy)
- Sprawdź czy NotificationWebApp słucha na porcie 5002

**Check się nie uruchamia:**
- W Data Explorer zweryfikuj czy zapytanie zwraca dane
- Sprawdź **Alerts → History** czy są ewaluacje

**Alert pokazany ale nie wysłany:**
- Status `OK` → `WARN/CRIT` tylko **na zmianę stanu**. Jeśli cały czas WARN, nie powtarzają się — dodaj krótki okres OK.
