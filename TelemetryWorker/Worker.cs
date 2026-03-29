using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace TelemetryWorker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _config;
    private IConnection? _connection;
    private IChannel? _channel;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    // Ta metoda wykonuje się raz przy starcie — podłączamy się do RabbitMQ
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker startuje, łączę się z RabbitMQ...");

        var factory = new ConnectionFactory
        {
            HostName = _config["RabbitMQ:Host"] ?? "localhost",
            Port = int.Parse(_config["RabbitMQ:Port"] ?? "5672"),
            UserName = _config["RabbitMQ:Username"] ?? "guest",
            Password = _config["RabbitMQ:Password"] ?? "guest"
        };

        // W nowej wersji RabbitMQ.Client wszystko jest async
        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

        // Mówimy RabbitMQ żeby dawał nam tylko 1 wiadomość na raz
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: cancellationToken);

        _logger.LogInformation("Połączono z RabbitMQ. Nasłuchuję na kolejce 'measurements'...");

        await base.StartAsync(cancellationToken);
    }

    // Ta metoda to główna pętla Workera — działa dopóki aplikacja jest uruchomiona
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_channel == null) return;

        // W nowej wersji używamy AsyncEventingBasicConsumer zamiast EventingBasicConsumer
        var consumer = new AsyncEventingBasicConsumer(_channel);

        // Ten kod wykona się za każdym razem gdy przyjdzie nowa wiadomość
        consumer.ReceivedAsync += async (model, eventArgs) =>
        {
            var body = eventArgs.Body.ToArray();
            var messageJson = Encoding.UTF8.GetString(body);

            _logger.LogInformation("Odebrano wiadomość: {Message}", messageJson);

            try
            {
                var message = JsonSerializer.Deserialize<QueueMessage>(messageJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (message == null)
                    throw new Exception("Nie udało się zdeserializować wiadomości.");

                // Weryfikacja integralności danych przez checksum HMAC
                string expectedChecksum = HmacHelper.Compute(message.Data, HmacHelper.SecretKey);

                if (message.Checksum != expectedChecksum)
                {
                    _logger.LogWarning("BŁĄD INTEGRALNOŚCI! Oczekiwano: {Expected}, otrzymano: {Received}",
                        expectedChecksum, message.Checksum);

                    // NACK — wiadomość trafi do DLQ
                    await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false);
                    return;
                }

                _logger.LogInformation("Checksum OK. Zapisuję do InfluxDB...");

                await InfluxWriter.WriteAsync(message.Data, _config, _logger);

                // ACK — potwierdzamy że przetworzyliśmy pomyślnie
                await _channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false);
                _logger.LogInformation("Wiadomość przetworzona i zapisana pomyślnie.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nieoczekiwany błąd podczas przetwarzania wiadomości.");
                await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false);
            }
        };

        await _channel.BasicConsumeAsync(queue: "measurements", autoAck: false, consumer: consumer, cancellationToken: stoppingToken);

        // Czekamy aż aplikacja zostanie zatrzymana
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => Task.CompletedTask);
    }

    // Ta metoda wykonuje się gdy aplikacja się kończy — sprzątamy połączenia
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker zatrzymuje się...");
        if (_channel != null) await _channel.CloseAsync(cancellationToken);
        if (_connection != null) await _connection.CloseAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}

// Model wiadomości z kolejki — musi być identyczny jak w FrontApi
public class QueueMessage
{
    public string Data { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}
