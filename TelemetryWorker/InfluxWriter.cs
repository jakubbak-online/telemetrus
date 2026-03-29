using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using System.Text.Json;

namespace TelemetryWorker;

// InfluxWriter odpowiada za zapis danych do bazy InfluxDB.
// InfluxDB to baza danych szeregów czasowych — idealna do pomiarów IoT.
// Każdy wpis ma: nazwę pomiaru, tagi (metadane), pola (wartości) i timestamp.
public static class InfluxWriter
{
    public static async Task WriteAsync(string dataJson, IConfiguration config, ILogger logger)
    {
        // Odczytujemy konfigurację InfluxDB z appsettings.json
        var url = config["InfluxDB:Url"] ?? "http://localhost:8086";
        var token = config["InfluxDB:Token"];
        var org = config["InfluxDB:Org"] ?? "myorg";
        var bucket = config["InfluxDB:Bucket"] ?? "telemetry";

        if (string.IsNullOrEmpty(token))
            throw new Exception("Brak tokena InfluxDB w konfiguracji!");

        // Parsujemy JSON żeby wyciągnąć wartości
        // Zakładamy że dane mają pola: deviceId i value
        // np. {"deviceId":"sensor1","value":23.5}
        // Usuwamy BOM i białe znaki które mogą pojawić się przy dekodowaniu Base64
        dataJson = dataJson.Trim().TrimStart('\uFEFF');
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;

        string deviceId = root.TryGetProperty("deviceId", out var d) ? d.GetString() ?? "unknown" : "unknown";
        double value = root.TryGetProperty("value", out var v) ? v.GetDouble() : 0;

        // Tworzymy klienta InfluxDB
        using var client = new InfluxDBClient(url, token);
        var writeApi = client.GetWriteApiAsync();

        // Budujemy "punkt" danych w formacie InfluxDB
        // measurement = nazwa tabeli/serii danych
        // Tag = metadana (indeksowana, dobra do filtrowania) — tu: deviceId
        // Field = wartość (nieindeksowana, przechowuje dane) — tu: value
        var point = PointData
            .Measurement("sensor_reading")       // nazwa pomiaru — jak nazwa tabeli
            .Tag("deviceId", deviceId)           // tag: po czym będziemy filtrować
            .Field("value", value)               // pole: co mierzymy
            .Timestamp(DateTime.UtcNow, WritePrecision.Ms); // kiedy — teraz

        await writeApi.WritePointAsync(point, bucket, org);

        logger.LogInformation("Zapisano do InfluxDB: deviceId={DeviceId}, value={Value}", deviceId, value);
    }
}
