using Microsoft.AspNetCore.SignalR;

namespace NotificationWebApp.Hubs;

// AlertHub to "centralka" SignalR.
// Klienci (przeglądarki) łączą się przez WebSocket i nasłuchują na zdarzenia "ReceiveAlert".
// Serwer (WebhookController) wywołuje Clients.All.SendAsync("ReceiveAlert", ...) żeby wysłać do wszystkich.
public class AlertHub : Hub
{
    private readonly ILogger<AlertHub> _logger;

    public AlertHub(ILogger<AlertHub> logger)
    {
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Klient połączony z AlertHub: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Klient rozłączony z AlertHub: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
