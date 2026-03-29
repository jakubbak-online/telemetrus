using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace FrontApi.Controllers;

[ApiController]
[Route("[controller]")]
public class MeasurementController : ControllerBase
{
    private readonly RabbitMqPublisher _publisher;
    private readonly ILogger<MeasurementController> _logger;

    // Wstrzykujemy RabbitMqPublisher i logger przez konstruktor (dependency injection)
    public MeasurementController(RabbitMqPublisher publisher, ILogger<MeasurementController> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    // Ten endpoint odpowiada na POST http://localhost:5000/measurement
    [HttpPost]
    public IActionResult Post([FromBody] MeasurementRequest request)
    {
        // Walidacja — sprawdzamy czy pole payload w ogóle przyszło
        if (string.IsNullOrWhiteSpace(request.Payload))
        {
            _logger.LogWarning("Otrzymano żądanie bez pola payload.");
            return BadRequest(new { error = "Pole 'payload' jest wymagane." });
        }

        // Dekodowanie Base64
        // Base64 to sposób zapisu binarnych danych jako tekst.
        // np. "eyJkZXZpY2VJZCI6InNlbnNvcjEifQ==" → {"deviceId":"sensor1"}
        byte[] decodedBytes;
        try
        {
            decodedBytes = Convert.FromBase64String(request.Payload);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Nie udało się zdekodować Base64: {Payload}", request.Payload);
            return BadRequest(new { error = "Pole 'payload' nie jest poprawnym Base64." });
        }

        // Zamieniamy bajty z powrotem na string (UTF-8 bez BOM)
        // Używamy GetString z offsetem żeby pominąć ewentualny BOM na początku
        string decodedJson = new UTF8Encoding(false).GetString(decodedBytes).Trim().TrimStart('\uFEFF');
        _logger.LogInformation("Zdekodowany payload: {Json}", decodedJson);

        // Obliczamy HMAC checksum i dołączamy do wiadomości razem z danymi
        // Dzięki temu Worker może sprawdzić czy dane nie zostały zmienione po drodze
        string hmac = HmacHelper.Compute(decodedBytes, HmacHelper.SecretKey);

        // Tworzymy wiadomość, którą wyślemy do RabbitMQ
        // Owijamy dane + checksum w jeden obiekt JSON
        var message = new QueueMessage
        {
            Data = decodedJson,
            Checksum = hmac,
            ReceivedAt = DateTime.UtcNow
        };

        string messageJson = JsonSerializer.Serialize(message);

        // Wysyłamy wiadomość do kolejki RabbitMQ
        try
        {
            _publisher.Publish(messageJson);
            _logger.LogInformation("Wiadomość wysłana do kolejki. Checksum: {Checksum}", hmac);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd przy wysyłaniu do RabbitMQ.");
            return StatusCode(500, new { error = "Błąd wewnętrzny — nie udało się wysłać do kolejki." });
        }

        return Ok(new { status = "ok", checksum = hmac });
    }
}

// Model danych przychodzących z zewnątrz — mapuje się na JSON {"payload": "..."}
public class MeasurementRequest
{
    public string Payload { get; set; } = string.Empty;
}

// Model wiadomości wysyłanej do kolejki RabbitMQ
public class QueueMessage
{
    public string Data { get; set; } = string.Empty;      // zdekodowany JSON
    public string Checksum { get; set; } = string.Empty;  // HMAC — do weryfikacji przez Workera
    public DateTime ReceivedAt { get; set; }               // kiedy API odebrało wiadomość
}
