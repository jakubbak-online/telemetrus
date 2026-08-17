# send-measurements.ps1
# Skrypt demonstracyjny - wysyla do FrontApi serie wiadomosci:
#   - POPRAWNA (HMAC sie zgadza, JSON OK)           -> powinna trafic do InfluxDB
#   - BLEDNY HMAC (celowo przekrecony checksum)     -> powinna trafic do DLQ
#   - BLEDNY JSON w Base64                          -> 400 z API (nie trafi do kolejki)
#   - BRAK POLA 'value'                             -> 400 z API
#   - KANAL: temperature (inna kolejka)             -> idzie do measurements.temperature
#
# Uruchomienie (PowerShell 5.1 lub 7+):
#   powershell -File scripts\send-measurements.ps1
#   pwsh -File scripts/send-measurements.ps1
#   pwsh -File scripts/send-measurements.ps1 -HighValue   # + pomiar 95 (> progu CRIT z docs/influxdb-alert-setup.md), do demo alertow
#
# Wymaga: FrontApi uruchomione na http://localhost:5000

[CmdletBinding()] # bez tego nieznane parametry (np. literowka) sa po cichu ignorowane zamiast zglaszac blad
param(
    [string]$ApiUrl = "http://localhost:5000/measurement",
    [string]$SecretKey = "telemetrus-demo-shared-secret",
    [switch]$HighValue    # dolacza dodatkowy scenariusz z wartoscia powyzej progu CRIT (do demo realtime alerts)
)

$ErrorActionPreference = 'Stop'

# ---- Funkcje pomocnicze ----

function Encode-Base64([string]$text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    return [Convert]::ToBase64String($bytes)
}

function Compute-Hmac([string]$text, [string]$key) {
    $keyBytes = [System.Text.Encoding]::UTF8.GetBytes($key)
    $textBytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $hmac = New-Object System.Security.Cryptography.HMACSHA256(,$keyBytes)
    try {
        $hash = $hmac.ComputeHash($textBytes)
    } finally {
        $hmac.Dispose()
    }
    return ([BitConverter]::ToString($hash) -replace '-', '').ToLower()
}

function Send-Measurement {
    param(
        [string]$Label,
        [string]$Payload,
        [string]$Checksum,
        [string]$Channel
    )

    Write-Host ""
    Write-Host "=== $Label ===" -ForegroundColor Cyan

    $body = @{
        payload  = $Payload
        checksum = $Checksum
    }
    if ($Channel) { $body.channel = $Channel }

    $json = $body | ConvertTo-Json -Compress

    try {
        $response = Invoke-RestMethod -Uri $ApiUrl -Method Post `
            -ContentType 'application/json; charset=utf-8' `
            -Body $json `
            -ErrorAction Stop
        Write-Host "[OK] Odpowiedz API: $($response | ConvertTo-Json -Compress)" -ForegroundColor Green
    } catch {
        # Obsluga zarowna dla PS 5.1 (WebException) jak i PS 7+ (HttpResponseException)
        $statusCode = $null
        $errorBody = $null

        if ($_.Exception.Response) {
            try {
                $statusCode = [int]$_.Exception.Response.StatusCode
            } catch { }

            # W PS 7+ tresc bledu jest w ErrorDetails.Message
            if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
                $errorBody = $_.ErrorDetails.Message
            } else {
                # W PS 5.1 trzeba recznie przeczytac stream
                try {
                    $stream = $_.Exception.Response.GetResponseStream()
                    $reader = New-Object System.IO.StreamReader($stream)
                    $errorBody = $reader.ReadToEnd()
                    $reader.Dispose()
                } catch { }
            }
        }

        if ($statusCode) {
            Write-Host "[HTTP $statusCode] $errorBody" -ForegroundColor Yellow
        } else {
            Write-Host "[BLAD] $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

# ---- Weryfikacja dostepnosci API ----
Write-Host "Sprawdzam dostepnosc API: $ApiUrl" -ForegroundColor Gray
try {
    # Probna wiadomosc - jesli API zwroci cokolwiek (nawet 400) to znaczy ze dziala
    Invoke-WebRequest -Uri $ApiUrl -Method Post -Body '{}' `
        -ContentType 'application/json' -ErrorAction SilentlyContinue -UseBasicParsing | Out-Null
} catch {
    if ($_.Exception.Response) {
        Write-Host "API odpowiada - kontynuuje." -ForegroundColor Gray
    } else {
        Write-Host ""
        Write-Host "BLAD: Nie mozna polaczyc sie z $ApiUrl" -ForegroundColor Red
        Write-Host "Czy FrontApi jest uruchomione? (cd FrontApi; dotnet run)" -ForegroundColor Red
        exit 1
    }
}

# ==============================================================
# SCENARIUSZ 1: poprawna wiadomosc (HMAC zgodny, JSON poprawny)
# ==============================================================
$jsonData = '{"deviceId":"sensor-1","value":23.5}'
$payload  = Encode-Base64 $jsonData
$checksum = Compute-Hmac $jsonData $SecretKey
Send-Measurement -Label "1. POPRAWNA WIADOMOSC (-> InfluxDB)" -Payload $payload -Checksum $checksum

# ==============================================================
# SCENARIUSZ 2: poprawna wiadomosc z kanalem "temperature"
# ==============================================================
$jsonData = '{"deviceId":"temp-sensor-A","value":42.1}'
$payload  = Encode-Base64 $jsonData
$checksum = Compute-Hmac $jsonData $SecretKey
Send-Measurement -Label "2. POPRAWNA WIADOMOSC, kanal 'temperature'" -Payload $payload -Checksum $checksum -Channel "temperature"

# ==============================================================
# SCENARIUSZ 3: BLEDNY HMAC - dane OK ale checksum celowo zly
# ==============================================================
$jsonData = '{"deviceId":"sensor-2","value":10.0}'
$payload  = Encode-Base64 $jsonData
$badChecksum = "0000000000000000000000000000000000000000000000000000000000000000"
Send-Measurement -Label "3. BLEDNY HMAC (-> DLQ)" -Payload $payload -Checksum $badChecksum

# ==============================================================
# SCENARIUSZ 4: BLEDNY JSON w Base64
# ==============================================================
$invalidJson = "to nie jest json"
$payload  = Encode-Base64 $invalidJson
$checksum = Compute-Hmac $invalidJson $SecretKey
Send-Measurement -Label "4. PAYLOAD NIE JEST JSON (-> 400)" -Payload $payload -Checksum $checksum

# ==============================================================
# SCENARIUSZ 5: JSON bez pola 'value'
# ==============================================================
$jsonData = '{"deviceId":"sensor-3"}'
$payload  = Encode-Base64 $jsonData
$checksum = Compute-Hmac $jsonData $SecretKey
Send-Measurement -Label "5. BRAK POLA 'value' (-> 400)" -Payload $payload -Checksum $checksum

# ==============================================================
# SCENARIUSZ 6: 'value' jest stringiem, nie liczba
# ==============================================================
$jsonData = '{"deviceId":"sensor-4","value":"nie-liczba"}'
$payload  = Encode-Base64 $jsonData
$checksum = Compute-Hmac $jsonData $SecretKey
Send-Measurement -Label "6. 'value' nie jest liczba (-> 400)" -Payload $payload -Checksum $checksum

# ==============================================================
# SCENARIUSZ 7: niepoprawny Base64
# ==============================================================
Send-Measurement -Label "7. NIEPOPRAWNY Base64 (-> 400)" -Payload "!!!-to-nie-jest-base64-!!!" -Checksum "abc"

# ==============================================================
# SCENARIUSZ 8 (opcjonalny, -HighValue): wartosc powyzej progu CRIT
# Do demo Realtime Alerts z docs/influxdb-alert-setup.md (CRIT > 80).
# ==============================================================
if ($HighValue) {
    $jsonData = '{"deviceId":"sensor-high","value":95}'
    $payload  = Encode-Base64 $jsonData
    $checksum = Compute-Hmac $jsonData $SecretKey
    Send-Measurement -Label "8. WYSOKA WARTOSC 95 (-> InfluxDB, CRIT > 80 -> powinien wywolac alert)" -Payload $payload -Checksum $checksum
}

Write-Host ""
Write-Host "=== Koniec scenariuszy ===" -ForegroundColor Magenta
Write-Host "Sprawdz:"
Write-Host "  - Logi Workera (tam zobaczysz [DLQ] dla scenariusza 3)"
Write-Host "  - Panel RabbitMQ: http://localhost:15672 (kolejki measurements.* i *.dlq)"
Write-Host "  - InfluxDB Data Explorer: http://localhost:8086 (dane z scenariuszy 1 i 2)"
