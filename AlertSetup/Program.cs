using System.Net.Http.Json;
using System.Text.Json;

// AlertSetup — jednorazowy provisioner alertów InfluxDB.
//
// Tworzy przez REST API InfluxDB dokładnie to, co docs/influxdb-alert-setup.md każe kliknąć
// ręcznie w UI: Notification Endpoint (webhook -> NotificationWebApp), Threshold Check
// (sensor_reading.value: CRIT > 80, WARN > 60, OK < 50, co 1 minutę) i Notification Rule
// łączącą oba. Dzięki temu alerty działają od razu po `docker compose up`, bez konfiguracji.
//
// Uruchamiany jako usługa "alertsetup" w docker-compose.yml po tym, jak influxdb przejdzie
// healthcheck — to jednorazowy proces (kontener kończy działanie po Main), nie długo żyjąca
// usługa. Idempotentny: bezpieczny przy każdym `docker compose up`, bo szuka istniejących
// zasobów po nazwie przed utworzeniem nowych.
//
// docs/influxdb-alert-setup.md zostaje jako opis ręcznej ścieżki — przydatny do zmiany progów
// czy debugowania, ale nie jest już wymagany do podstawowego działania alertów.

string influxUrl = Environment.GetEnvironmentVariable("INFLUXDB_URL") ?? "http://influxdb:8086";
string token = Environment.GetEnvironmentVariable("INFLUXDB_ADMIN_TOKEN")
    ?? throw new InvalidOperationException("INFLUXDB_ADMIN_TOKEN nie jest ustawiony.");
string org = Environment.GetEnvironmentVariable("INFLUXDB_ORG") ?? "myorg";
string bucket = Environment.GetEnvironmentVariable("INFLUXDB_BUCKET") ?? "telemetry";
string webhookUrl = Environment.GetEnvironmentVariable("NOTIFICATION_WEBHOOK_URL")
    ?? "http://notificationwebapp:8080/webhook/influx";

const string EndpointName = "Telemetrus WebApp";
const string CheckName = "Wysoka wartosc pomiaru";
const string RuleName = "Powiadom WebApp o alertach";

using var client = new HttpClient { BaseAddress = new Uri(influxUrl) };
client.DefaultRequestHeaders.Add("Authorization", $"Token {token}");

Console.WriteLine("[influxdb-setup] Czekam az InfluxDB API odpowie...");
string? orgId = null;
for (int i = 0; i < 30 && orgId is null; i++)
{
    try
    {
        var resp = await client.GetAsync($"/api/v2/orgs?org={Uri.EscapeDataString(org)}");
        if (resp.IsSuccessStatusCode)
        {
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (doc.TryGetProperty("orgs", out var orgs) && orgs.GetArrayLength() > 0)
                orgId = orgs[0].GetProperty("id").GetString();
        }
    }
    catch (HttpRequestException)
    {
        // InfluxDB jeszcze nie gotowe mimo healthchecka — próbujemy dalej
    }

    if (orgId is null) await Task.Delay(2000);
}

if (orgId is null)
{
    Console.WriteLine($"[influxdb-setup] BŁĄD: nie udało się pobrać ID organizacji '{org}' po 60s.");
    Console.WriteLine("[influxdb-setup] Pomijam konfigurację alertów — skonfiguruj ręcznie wg docs/influxdb-alert-setup.md, albo uruchom ponownie: docker compose up alertsetup.");
    return 0; // nie failujemy calego `docker compose up` z powodu tego kroku
}
Console.WriteLine($"[influxdb-setup] Org '{org}' -> {orgId}");

// --- 1. Notification Endpoint (HTTP -> NotificationWebApp) ---
string? endpointId = await FindByNameAsync($"/api/v2/notificationEndpoints?orgID={orgId}", "notificationEndpoints", EndpointName);
if (endpointId is null)
{
    Console.WriteLine($"[influxdb-setup] Tworzę Notification Endpoint '{EndpointName}'...");
    var resp = await client.PostAsJsonAsync("/api/v2/notificationEndpoints", new
    {
        name = EndpointName,
        orgID = orgId,
        type = "http",
        method = "POST",
        url = webhookUrl,
        authMethod = "none",
        status = "active"
    });

    if (!resp.IsSuccessStatusCode)
    {
        Console.WriteLine($"[influxdb-setup] BŁĄD tworzenia Endpoint ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        return 0;
    }
    endpointId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
    Console.WriteLine($"[influxdb-setup] Endpoint utworzony: {endpointId}");
}
else
{
    Console.WriteLine($"[influxdb-setup] Endpoint już istnieje: {endpointId}");
}

// --- 2. Threshold Check na sensor_reading.value ---
string? checkId = await FindByNameAsync($"/api/v2/checks?orgID={orgId}", "checks", CheckName);
if (checkId is null)
{
    Console.WriteLine($"[influxdb-setup] Tworzę Threshold Check '{CheckName}'...");

    // UWAGA: statusMessageTemplate celowo używa TYLKO ${r._check_name} i ${r._level}. Check
    // tworzony przez API (bez konfiguracji "tags" jak w UI) generuje task z pustym "tags: {}",
    // przez co zarówno ${r.deviceId} JAK I ${r._value} bywają null przy realnych danych i wywalają
    // task błędem "interpolated expression produced a null value" — zweryfikowane eksperymentalnie
    // (dwa kolejne warianty szablonu obie te zmienne wywaliły runa). deviceId/value i tak trafiają
    // do webhooka jako osobne pola najwyższego poziomu (nie przez interpolację), więc UI je pokazuje
    // w sekcji "raw" alertu mimo uproszczonej treści _message.
    // fn: max, NIE mean — a threshold check istnieje po to, żeby złapać KAŻDY odczyt powyżej progu.
    // Uśrednianie rozmywa pojedyncze skoki: przy większym ruchu (np. panel "Test obciążeniowy" w UI,
    // ~17 pomiarów/s na 20 urządzeń) średnia z ~50 odczytów na urządzenie w oknie 1-minutowym
    // praktycznie nigdy nie przekroczy progu, nawet jeśli kilka z nich faktycznie go przekroczyło
    // (zweryfikowane eksperymentalnie — burst z 46 odczytami >80 na 1000 nie wywołał alertu z fn: mean).
    string queryText =
        $"from(bucket: \"{bucket}\") |> range(start: -5m) " +
        "|> filter(fn: (r) => r._measurement == \"sensor_reading\") " +
        "|> filter(fn: (r) => r._field == \"value\") " +
        "|> aggregateWindow(every: 1m, fn: max, createEmpty: false)";

    var resp = await client.PostAsJsonAsync("/api/v2/checks", new
    {
        type = "threshold",
        name = CheckName,
        orgID = orgId,
        status = "active",
        query = new { name = "query1", text = queryText },
        every = "1m",
        offset = "0s",
        thresholds = new object[]
        {
            new { type = "greater", value = 80, level = "CRIT", allValues = false },
            new { type = "greater", value = 60, level = "WARN", allValues = false },
            new { type = "lesser", value = 50, level = "OK", allValues = false }
        },
        statusMessageTemplate = "${ r._check_name }: ${ r._level }"
    });

    if (!resp.IsSuccessStatusCode)
        Console.WriteLine($"[influxdb-setup] BŁĄD tworzenia Check ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    else
        Console.WriteLine($"[influxdb-setup] Check utworzony: {(await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}");
}
else
{
    Console.WriteLine($"[influxdb-setup] Check już istnieje: {checkId}");
}

// --- 3. Notification Rule: CRIT lub WARN -> Endpoint ---
string? ruleId = await FindByNameAsync($"/api/v2/notificationRules?orgID={orgId}", "notificationRules", RuleName);
if (ruleId is null)
{
    Console.WriteLine($"[influxdb-setup] Tworzę Notification Rule '{RuleName}'...");
    var resp = await client.PostAsJsonAsync("/api/v2/notificationRules", new
    {
        name = RuleName,
        orgID = orgId,
        endpointID = endpointId,
        status = "active",
        every = "1m",
        type = "http",
        statusRules = new object[]
        {
            new { currentLevel = "CRIT" },
            new { currentLevel = "WARN" }
        }
    });

    if (!resp.IsSuccessStatusCode)
        Console.WriteLine($"[influxdb-setup] BŁĄD tworzenia Rule ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    else
        Console.WriteLine($"[influxdb-setup] Rule utworzona: {(await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}");
}
else
{
    Console.WriteLine($"[influxdb-setup] Rule już istnieje: {ruleId}");
}

Console.WriteLine("[influxdb-setup] Gotowe. Pomiar > 80 -> CRIT, > 60 -> WARN. Alert dotrze do UI (:5002) w ciągu do 1 minuty (harmonogram Check).");
return 0;

// Szuka zasobu o danej nazwie na jednostronicowej liście (domyślny limit API to 20 — wystarcza
// dla garstki zasobów tworzonych przez ten skrypt).
async Task<string?> FindByNameAsync(string path, string arrayProp, string name)
{
    try
    {
        var resp = await client.GetAsync(path);
        if (!resp.IsSuccessStatusCode) return null;

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        if (!doc.TryGetProperty(arrayProp, out var arr)) return null;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var n) && n.GetString() == name)
                return item.GetProperty("id").GetString();
        }
    }
    catch (HttpRequestException)
    {
        // brak połączenia — traktujemy jak "nie znaleziono", wywołujący spróbuje utworzyć
    }
    return null;
}
