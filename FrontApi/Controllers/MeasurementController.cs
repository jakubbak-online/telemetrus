using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace FrontApi.Controllers;

[ApiController]
[Route("[controller]")]
public class MeasurementController : ControllerBase
{
    private readonly RabbitMqPublisher _publisher;
    private readonly ILogger<MeasurementController> _logger;

    public MeasurementController(RabbitMqPublisher publisher, ILogger<MeasurementController> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    // POST http://localhost:5000/measurement
    // Klient przysyła:
    //   payload  — dane pomiarowe zakodowane w Base64 (JSON: {"deviceId":"...", "value":...})
    //   checksum — HMAC-SHA256 obliczony przez klienta z dekodowanego JSON + tajny klucz
    //   channel  — (opcjonalne) nazwa kanału RabbitMQ, np. "temperature", "sensors" (domyślnie: "default")
    [HttpPost]
    public IActionResult Post([FromBody] MeasurementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Payload))
        {
            _logger.LogWarning("Odrzucono żądanie — brak pola 'payload'.");
            return BadRequest(new { error = "Pole 'payload' jest wymagane." });
        }

        if (string.IsNullOrWhiteSpace(request.Checksum))
        {
            _logger.LogWarning("Odrzucono żądanie — brak pola 'checksum'.");
            return BadRequest(new { error = "Pole 'checksum' jest wymagane." });
        }

        string channel = string.IsNullOrWhiteSpace(request.Channel) ? "default" : request.Channel.Trim().ToLower();

        if (!channel.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
        {
            _logger.LogWarning("Odrzucono żądanie — niepoprawna nazwa kanału: {Channel}", channel);
            return BadRequest(new { error = "Pole 'channel' może zawierać tylko litery, cyfry, myślniki i podkreślenia." });
        }

        byte[] decodedBytes;
        try
        {
            decodedBytes = Convert.FromBase64String(request.Payload);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Odrzucono żądanie — niepoprawny Base64.");
            return BadRequest(new { error = "Pole 'payload' nie jest poprawnym Base64." });
        }

        string decodedJson = Encoding.UTF8.GetString(decodedBytes);

        // Sprawdzamy strukturę PRZED wysłaniem do kolejki, żeby błędne dane nie trafiały do Workera
        try
        {
            using var doc = JsonDocument.Parse(decodedJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("deviceId", out var deviceIdEl) || string.IsNullOrWhiteSpace(deviceIdEl.GetString()))
            {
                _logger.LogWarning("Odrzucono żądanie — brak lub puste pole 'deviceId' w payload.");
                return BadRequest(new { error = "Payload musi zawierać niepuste pole 'deviceId'." });
            }

            if (!root.TryGetProperty("value", out var valueEl))
            {
                _logger.LogWarning("Odrzucono żądanie — brak pola 'value' w payload.");
                return BadRequest(new { error = "Payload musi zawierać pole 'value'." });
            }

            if (valueEl.ValueKind != JsonValueKind.Number)
            {
                _logger.LogWarning("Odrzucono żądanie — pole 'value' nie jest liczbą (typ: {Kind}).", valueEl.ValueKind);
                return BadRequest(new { error = "Pole 'value' w payload musi być liczbą." });
            }
        }
        catch (JsonException)
        {
            _logger.LogWarning("Odrzucono żądanie — zdekodowany payload nie jest poprawnym JSON.");
            return BadRequest(new { error = "Zdekodowany payload nie jest poprawnym JSON. Oczekiwano: {\"deviceId\":\"...\",\"value\":0.0}" });
        }

        _logger.LogInformation("Zdekodowany payload: {Json} | kanał: {Channel}", decodedJson, channel);

        // Checksum weryfikuje Worker — API przekazuje go dalej bez weryfikacji (separation of concerns)
        var message = new QueueMessage
        {
            Data = decodedJson,
            Checksum = request.Checksum,
            ReceivedAt = DateTime.UtcNow
        };

        string messageJson = JsonSerializer.Serialize(message);

        try
        {
            _publisher.Publish(messageJson, channel);
            _logger.LogInformation("Wiadomość wysłana do kanału '{Channel}'.", channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd przy wysyłaniu do RabbitMQ.");
            return StatusCode(500, new { error = "Błąd wewnętrzny serwera." });
        }

        return Ok(new { status = "ok", channel });
    }
}

public class MeasurementRequest
{
    public string Payload { get; set; } = string.Empty;    // dane zakodowane w Base64
    public string Checksum { get; set; } = string.Empty;   // HMAC-SHA256 obliczony przez klienta
    public string? Channel { get; set; }                   // kanał RabbitMQ (opcjonalne, domyślnie "default")
}

public class QueueMessage
{
    public string Data { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}
