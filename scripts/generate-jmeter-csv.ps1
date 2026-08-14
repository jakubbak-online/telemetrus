# generate-jmeter-csv.ps1
# Generuje plik CSV z poprawnie zakodowanymi wiadomosciami dla JMetera.
# Kazdy wiersz zawiera: payload (Base64), checksum (HMAC) i channel.
#
# Uruchomienie (PowerShell 5.1 lub 7+):
#   powershell -File scripts\generate-jmeter-csv.ps1 -Count 1000
#   pwsh -File scripts/generate-jmeter-csv.ps1 -Count 1000

param(
    [int]$Count = 1000,
    [string]$SecretKey = "telemetrus-demo-shared-secret",
    [string]$OutputFile = "jmeter/test-data.csv"
)

$ErrorActionPreference = 'Stop'

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

# Wyznaczamy sciezke wzgledna do korzenia repo (skrypt jest w /scripts)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
$outPath   = Join-Path $repoRoot $OutputFile
$outDir    = Split-Path -Parent $outPath
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$channels = @("default", "temperature", "humidity", "pressure")

# Budujemy caly zawartosc w StringBuilder, potem zapisujemy JEDNYM wywolaniem.
# WAZNE: uzywamy UTF8Encoding($false) - BEZ BOM - bo JMeter nie parsuje CSV z BOM.
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("payload,checksum,channel")

$rng = New-Object System.Random
for ($i = 1; $i -le $Count; $i++) {
    $deviceId = "device-{0:D4}" -f $rng.Next(1, 100)
    $value    = [Math]::Round($rng.NextDouble() * 100, 2)
    $channel  = $channels[$rng.Next(0, $channels.Length)]

    # Uzywamy "$value" w formacie invariant (kropka jako separator dziesietny)
    $valueStr = $value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $json     = '{"deviceId":"' + $deviceId + '","value":' + $valueStr + '}'
    $payload  = Encode-Base64 $json
    $checksum = Compute-Hmac $json $SecretKey

    [void]$sb.AppendLine("$payload,$checksum,$channel")

    if ($i % 100 -eq 0) {
        Write-Progress -Activity "Generuje CSV" -Status "$i / $Count" -PercentComplete (($i / $Count) * 100)
    }
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($outPath, $sb.ToString(), $utf8NoBom)

Write-Progress -Activity "Generuje CSV" -Completed
Write-Host "Wygenerowano $Count wierszy do: $outPath" -ForegroundColor Green
