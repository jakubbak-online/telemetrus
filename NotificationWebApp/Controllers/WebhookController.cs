using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NotificationWebApp.Hubs;
using System.Text.Json;

namespace NotificationWebApp.Controllers;

// WebhookController przyjmuje HTTP POST od InfluxDB gdy reguła alertowa się uruchomi.
// InfluxDB wysyła POST z JSON zawierającym szczegóły alertu — my rozgłaszamy je przez SignalR.
[ApiController]
[Route("[controller]")]
public class WebhookController : ControllerBase
{
    private readonly IHubContext<AlertHub> _hubContext;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(IHubContext<AlertHub> hubContext, ILogger<WebhookController> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    // POST http://localhost:5002/webhook/influx
    // Endpoint dla InfluxDB. Przyjmuje dowolny JSON — InfluxDB wysyła strukturę zależną od konfiguracji.
    [HttpPost("influx")]
    public async Task<IActionResult> ReceiveInfluxAlert([FromBody] JsonElement payload)
    {
        _logger.LogInformation("Otrzymano webhook z InfluxDB: {Payload}", payload.ToString());

        // Wyciągamy kluczowe pola jeśli są (InfluxDB może wysyłać różne struktury)
        string message = payload.TryGetProperty("_message", out var msg) ? msg.GetString() ?? "" :
                         payload.TryGetProperty("message", out var msg2) ? msg2.GetString() ?? "" :
                         "Alert z InfluxDB";

        string level = payload.TryGetProperty("_level", out var lvl) ? lvl.GetString() ?? "warn" :
                       payload.TryGetProperty("level", out var lvl2) ? lvl2.GetString() ?? "warn" :
                       "warn";

        var alert = new
        {
            timestamp = DateTime.UtcNow,
            level,
            message,
            raw = payload.ToString()
        };

        // Wysyłamy do WSZYSTKICH podłączonych klientów
        await _hubContext.Clients.All.SendAsync("ReceiveAlert", alert);

        _logger.LogInformation("Alert rozgłoszony do klientów SignalR: {Level} | {Message}", level, message);

        return Ok(new { status = "alert_broadcasted" });
    }

    // Endpoint testowy — pozwala ręcznie wywołać alert bez InfluxDB (przydatne do demo/prezentacji)
    [HttpPost("test")]
    public async Task<IActionResult> TestAlert([FromBody] TestAlertRequest request)
    {
        var alert = new
        {
            timestamp = DateTime.UtcNow,
            level = request.Level ?? "info",
            message = request.Message ?? "Testowy alert",
            raw = "(manual test)"
        };

        await _hubContext.Clients.All.SendAsync("ReceiveAlert", alert);
        _logger.LogInformation("Testowy alert rozgłoszony: {Message}", alert.message);

        return Ok(alert);
    }
}

public class TestAlertRequest
{
    public string? Level { get; set; }
    public string? Message { get; set; }
}
