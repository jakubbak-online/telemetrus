using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NotificationWebApp.Controllers;

// DemoController pozwala wygenerować testowy pomiar z poziomu przeglądarki (przycisk w UI),
// bez uruchamiania scripts/send-measurements.ps1 z terminala.
//
// Liczy Base64 + HMAC-SHA256 dokładnie tak samo jak scripts/send-measurements.ps1, a potem
// przekazuje żądanie do FrontApi jak zrobiłby to prawdziwy zewnętrzny klient — NIE omija
// walidacji struktury ani weryfikacji checksum w Workerze. To wygodny skrót demo, nie osobna
// ścieżka wpisywania danych do systemu.
[ApiController]
[Route("demo")]
public class DemoController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DemoController> _logger;

    public DemoController(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<DemoController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    // POST http://localhost:5002/demo/measurement
    // Body: { "deviceId": "...", "value": 0.0, "channel": "default", "breakChecksum": false }
    [HttpPost("measurement")]
    public async Task<IActionResult> SendMeasurement([FromBody] DemoMeasurementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return BadRequest(new { error = "Pole 'deviceId' jest wymagane." });
        }

        string channel = string.IsNullOrWhiteSpace(request.Channel) ? "default" : request.Channel.Trim().ToLower();

        string dataJson = JsonSerializer.Serialize(new { deviceId = request.DeviceId, value = request.Value });
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(dataJson));

        string hmacKey = _config["Hmac:SecretKey"] ?? HmacHelper.FallbackKey;

        // breakChecksum symuluje scenariusz 3 z send-measurements.ps1 — do demo routingu do DLQ
        string checksum = request.BreakChecksum
            ? "0000000000000000000000000000000000000000000000000000000000000000"
            : HmacHelper.Compute(dataJson, hmacKey);

        string frontApiUrl = _config["FrontApi:Url"] ?? "http://localhost:5000";
        var client = _httpClientFactory.CreateClient();

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync($"{frontApiUrl}/measurement", new
            {
                payload,
                checksum,
                channel
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Demo: nie udało się połączyć z FrontApi pod {Url}.", frontApiUrl);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Nie można połączyć się z FrontApi ({frontApiUrl}). Czy kontener/proces frontapi działa?" });
        }

        string body = await response.Content.ReadAsStringAsync();
        _logger.LogInformation(
            "Demo pomiar -> FrontApi: {Json} | kanał={Channel} | zły checksum={Broken} -> HTTP {Status}",
            dataJson, channel, request.BreakChecksum, (int)response.StatusCode);

        // Przekazujemy dalej dokładnie to, co odpowiedziało FrontApi (status + treść JSON),
        // żeby UI pokazywało prawdziwy wynik walidacji, nie własną interpretację.
        object parsedBody;
        try
        {
            parsedBody = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException)
        {
            parsedBody = new { raw = body };
        }

        return StatusCode((int)response.StatusCode, parsedBody);
    }
}

public class DemoMeasurementRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public double Value { get; set; }
    public string? Channel { get; set; }
    public bool BreakChecksum { get; set; }
}
