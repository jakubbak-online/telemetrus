using RabbitMQ.Client;
using System.Text;

namespace FrontApi;

// RabbitMqPublisher obsługuje wiele kanałów publikacji.
// Każdy kanał to osobna kolejka: measurements.{channel} z własną DLQ: measurements.{channel}.dlq
// Kolejki są deklarowane leniwie — przy pierwszej publikacji do danego kanału.
public class RabbitMqPublisher : IDisposable
{
    private const string QueuePrefix = "measurements";

    private readonly IConnection _connection;
    private readonly IModel _channel;

    // Śledzimy które kolejki zostały już zadeklarowane — QueueDeclare jest idempotentne,
    // ale wywołanie go przy każdym żądaniu to zbędny narzut sieciowy.
    private readonly HashSet<string> _declaredQueues = new();
    private readonly object _declareLock = new();

    public RabbitMqPublisher(IConfiguration config)
    {
        var factory = new ConnectionFactory
        {
            HostName = config["RabbitMQ:Host"] ?? "localhost",
            Port = int.Parse(config["RabbitMQ:Port"] ?? "5672"),
            UserName = config["RabbitMQ:Username"] ?? "guest",
            Password = config["RabbitMQ:Password"] ?? "guest"
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
    }

    // Publikuje wiadomość do kolejki dla podanego kanału.
    // Jeśli kolejka jeszcze nie istnieje — tworzy ją wraz z DLQ.
    public void Publish(string messageJson, string channelName = "default")
    {
        var queueName = $"{QueuePrefix}.{channelName}";
        EnsureQueueDeclared(queueName);

        var body = Encoding.UTF8.GetBytes(messageJson);
        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true; // wiadomość przeżyje restart RabbitMQ

        _channel.BasicPublish(
            exchange: "",
            routingKey: queueName,
            basicProperties: properties,
            body: body
        );
    }

    // Deklaruje kolejkę główną i jej DLQ, jeśli jeszcze nie są zadeklarowane.
    // Lock jest potrzebny bo Publisher jest singletonem — może być wywołany z wielu wątków.
    private void EnsureQueueDeclared(string queueName)
    {
        lock (_declareLock)
        {
            if (_declaredQueues.Contains(queueName)) return;

            var dlqName = $"{queueName}.dlq";

            // DLQ musi być zadeklarowana PRZED kolejką główną
            _channel.QueueDeclare(
                queue: dlqName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null
            );

            // Kolejka główna kieruje odrzucone wiadomości (NACK) do DLQ
            var args = new Dictionary<string, object>
            {
                { "x-dead-letter-exchange", "" },
                { "x-dead-letter-routing-key", dlqName }
            };

            _channel.QueueDeclare(
                queue: queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: args
            );

            _declaredQueues.Add(queueName);
        }
    }

    public void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
    }
}
