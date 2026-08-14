using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace TelemetryWorker;

public class Worker : BackgroundService
{
    private const string QueuePrefix = "measurements";

    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _config;
    private readonly InfluxWriter _influxWriter;
    private IConnection? _connection;
    private IChannel? _channel;

    public Worker(ILogger<Worker> logger, IConfiguration config, InfluxWriter influxWriter)
    {
        _logger = logger;
        _config = config;
        _influxWriter = influxWriter;
    }

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

        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

        // Jeden wątek Workera przetwarza jedną wiadomość na raz — zapewnia kolejność i prostotę
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: cancellationToken);

        // Deklarujemy kolejki dla wszystkich skonfigurowanych kanałów
        // Dzięki temu Worker może startować przed API — kolejki będą gotowe
        var channels = GetConfiguredChannels();
        foreach (var channelName in channels)
        {
            await DeclareChannelQueuesAsync(channelName, cancellationToken);
        }

        _logger.LogInformation("Zadeklarowano kanały: {Channels}", string.Join(", ", channels));
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_channel == null) return;

        var channels = GetConfiguredChannels();
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += HandleMessageAsync;

        // Rejestrujemy konsumenta na każdym kanale — jeden handler obsługuje wszystkie kolejki
        foreach (var channelName in channels)
        {
            var queueName = $"{QueuePrefix}.{channelName}";
            await _channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
            _logger.LogInformation("Nasłuchuję na kanale '{Channel}' (kolejka: {Queue}).", channelName, queueName);
        }

        // Czekamy aż aplikacja zostanie zatrzymana — OperationCanceledException jest obsługiwany przez BackgroundService
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleMessageAsync(object model, BasicDeliverEventArgs eventArgs)
    {
        if (_channel == null) return;

        var body = eventArgs.Body.ToArray();
        var messageJson = Encoding.UTF8.GetString(body);

        _logger.LogInformation("Odebrano wiadomość z kolejki '{Queue}': {Message}",
            eventArgs.RoutingKey, messageJson);

        QueueMessage? message = null;

        try
        {
            // Deserializacja wiadomości z kolejki
            message = JsonSerializer.Deserialize<QueueMessage>(messageJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (message == null)
                throw new InvalidOperationException("Deserializacja zwróciła null.");
        }
        catch (JsonException ex)
        {
            _logger.LogError("[DLQ] Odrzucono wiadomość — błąd deserializacji JSON: {Error}", ex.Message);
            await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError("[DLQ] Odrzucono wiadomość — nieoczekiwany błąd deserializacji: {Error}", ex.Message);
            await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        try
        {
            // Weryfikacja integralności HMAC — porównujemy hash przesłany przez klienta z tym liczonym lokalnie
            string hmacKey = _config["Hmac:SecretKey"] ?? HmacHelper.FallbackKey;
            string expectedChecksum = HmacHelper.Compute(message.Data, hmacKey);

            if (!string.Equals(message.Checksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[DLQ] Odrzucono wiadomość — błąd integralności HMAC. " +
                    "Oczekiwano: {Expected} | Otrzymano: {Received} | Dane: {Data}",
                    expectedChecksum, message.Checksum, message.Data);

                await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            _logger.LogInformation("Checksum OK. Zapisuję do InfluxDB...");

            // Checksum się zgadza — zapisujemy pomiar do InfluxDB
            await _influxWriter.WriteAsync(message.Data);

            await _channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false);
            _logger.LogInformation("Wiadomość przetworzona i zapisana pomyślnie.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DLQ] Odrzucono wiadomość — nieoczekiwany błąd przetwarzania. Dane: {Data}", message.Data);
            await _channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker zatrzymuje się...");
        if (_channel != null) await _channel.CloseAsync(cancellationToken);
        if (_connection != null) await _connection.CloseAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    // Odczytuje listę kanałów z konfiguracji. Jeśli brak — używa kanału "default".
    private string[] GetConfiguredChannels()
    {
        var channels = _config.GetSection("Worker:Channels").Get<string[]>();
        return (channels == null || channels.Length == 0) ? new[] { "default" } : channels;
    }

    // Deklaruje kolejkę główną i DLQ dla podanego kanału (idempotentne — bezpieczne przy restarcie).
    private async Task DeclareChannelQueuesAsync(string channelName, CancellationToken cancellationToken)
    {
        if (_channel == null) return;

        var queueName = $"{QueuePrefix}.{channelName}";
        var dlqName = $"{queueName}.dlq";

        await _channel.QueueDeclareAsync(
            queue: dlqName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken
        );

        var queueArgs = new Dictionary<string, object?>
        {
            { "x-dead-letter-exchange", "" },
            { "x-dead-letter-routing-key", dlqName }
        };

        await _channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArgs,
            cancellationToken: cancellationToken
        );

        _logger.LogInformation("Zadeklarowano kolejkę '{Queue}' z DLQ '{Dlq}'.", queueName, dlqName);
    }
}

public class QueueMessage
{
    public string Data { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}
