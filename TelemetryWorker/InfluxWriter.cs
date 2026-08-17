using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using System.Text.Json;

namespace TelemetryWorker;

// InfluxWriter jest rejestrowany jako singleton — jeden klient HTTP przez całe życie aplikacji.
// Tworzenie klienta per-wiadomość przy dużym obciążeniu powoduje wyczerpanie portów TCP.
public class InfluxWriter : IDisposable
{
    private readonly InfluxDBClient _client;
    private readonly string _org;
    private readonly string _bucket;
    private readonly ILogger<InfluxWriter> _logger;

    public InfluxWriter(IConfiguration config, ILogger<InfluxWriter> logger)
    {
        _logger = logger;

        var url = config["InfluxDB:Url"] ?? "http://localhost:8086";
        var token = config["InfluxDB:Token"];

        // "??" łapie tylko null — appsettings.json domyślnie ma Token: "" (pusty string, nie null),
        // więc bez tej jawnej kontroli pusty token przechodziłby dalej i wybuchał mniej czytelnym
        // ArgumentException dopiero wewnątrz konstruktora InfluxDBClient.
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Brak tokena InfluxDB w konfiguracji (InfluxDB:Token)!");

        _org = config["InfluxDB:Org"] ?? "myorg";
        _bucket = config["InfluxDB:Bucket"] ?? "telemetry";

        _client = new InfluxDBClient(url, token);
    }

    // Parsujemy JSON i zapisujemy punkt do InfluxDB.
    // Oczekiwany format: {"deviceId":"sensor1","value":23.5}
    public async Task WriteAsync(string dataJson)
    {
        // Usuwamy BOM i białe znaki które mogą pojawić się przy dekodowaniu Base64 na niektórych systemach
        dataJson = dataJson.Trim().TrimStart('\uFEFF');

        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;

        string deviceId = root.TryGetProperty("deviceId", out var d) ? d.GetString() ?? "unknown" : "unknown";
        double value = root.TryGetProperty("value", out var v) ? v.GetDouble() : 0;

        var writeApi = _client.GetWriteApiAsync();

        // Budujemy "punkt" danych w formacie InfluxDB
        // Tag = metadana indeksowana (tu: deviceId) — po czym filtrujemy w zapytaniach
        // Field = wartość nieindeksowana (tu: value) — co mierzymy
        var point = PointData
            .Measurement("sensor_reading")
            .Tag("deviceId", deviceId)
            .Field("value", value)
            .Timestamp(DateTime.UtcNow, WritePrecision.Ms);

        await writeApi.WritePointAsync(point, _bucket, _org);

        _logger.LogInformation("Zapisano do InfluxDB: deviceId={DeviceId}, value={Value}", deviceId, value);
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
