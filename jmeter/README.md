# Testy wydajnościowe JMeter

## Pliki

- `telemetrus-load-test.jmx` — plan testowy JMeter
- `test-data.csv` — dane wejściowe (generowane skryptem)

## Uruchomienie

### 1. Wygeneruj dane testowe

```powershell
pwsh ../scripts/generate-jmeter-csv.ps1 -Count 1000
```

Tworzy plik `test-data.csv` z 1000 wierszami: payload (Base64), checksum (HMAC) i channel.

### 2. Uruchom FrontApi

```bash
cd ../FrontApi
dotnet run
```

### 3. Uruchom JMeter GUI (dla eksploracji)

```bash
jmeter -t telemetrus-load-test.jmx
```

### 4. Uruchom w trybie headless (dla raportów)

```bash
jmeter -n -t telemetrus-load-test.jmx -l results.jtl -e -o report/
```

Raport HTML pojawi się w katalogu `report/`.

## Parametry planu

- **Liczba wątków (users):** 20
- **Ramp-up:** 10 sekund
- **Pętli na wątek:** 50 (razem 1000 żądań)
- **Timer:** Gaussian Random Timer (50ms ±20ms) — symuluje realistyczne opóźnienia między zdarzeniami
- **Assertion:** oczekuje HTTP 200

## Co jest weryfikowane

1. **Przepustowość** — ile requestów/sekundę API obsługuje
2. **Czas odpowiedzi** — średni, percentyl 90/95/99
3. **Stabilność kolejki** — czy RabbitMQ kumuluje wiadomości
4. **Zachowanie Workera** — czy nadąża z zapisem do InfluxDB (panel RabbitMQ pokaże długość kolejki)
