using RabbitMQ.Client;
using System.Text;

namespace FrontApi;

// RabbitMqPublisher to "serwis" — klasa odpowiedzialna wyłącznie za komunikację z RabbitMQ.
// Trzymamy go osobno od kontrolera, żeby kontroler był prosty i czytelny.
public class RabbitMqPublisher : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private const string QueueName = "measurements";
    private const string DeadLetterQueueName = "measurements.dlq"; // kolejka dla błędnych wiadomości

    public RabbitMqPublisher(IConfiguration config)
    {
        // Odczytujemy adres RabbitMQ z konfiguracji (appsettings.json)
        var factory = new ConnectionFactory
        {
            HostName = config["RabbitMQ:Host"] ?? "localhost",
            Port = int.Parse(config["RabbitMQ:Port"] ?? "5672"),
            UserName = config["RabbitMQ:Username"] ?? "guest",
            Password = config["RabbitMQ:Password"] ?? "guest"
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        // Deklarujemy kolejkę DLQ (Dead Letter Queue) — dla błędnych wiadomości
        // Jeśli Worker wyśle NACK, wiadomość trafi tutaj zamiast zniknąć
        _channel.QueueDeclare(
            queue: DeadLetterQueueName,
            durable: true,      // przeżyje restart RabbitMQ
            exclusive: false,
            autoDelete: false,
            arguments: null
        );

        // Deklarujemy kolejkę główną z argumentem x-dead-letter-exchange
        // To mówi RabbitMQ: "gdy wiadomość zostanie odrzucona, wyślij ją do DLQ"
        var args = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", "" },          // pusty string = domyślny exchange
            { "x-dead-letter-routing-key", DeadLetterQueueName } // klucz routingu = nazwa DLQ
        };

        _channel.QueueDeclare(
            queue: QueueName,
            durable: true,      // kolejka przeżyje restart RabbitMQ
            exclusive: false,   // może być używana przez wiele połączeń
            autoDelete: false,  // nie usuwa się automatycznie gdy brak konsumentów
            arguments: args     // tutaj podpinamy DLQ
        );
    }

    // Metoda, którą wywołuje kontroler żeby wysłać wiadomość
    public void Publish(string messageJson)
    {
        var body = Encoding.UTF8.GetBytes(messageJson);

        // IBasicProperties to "koperta" — metadane wiadomości
        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true; // wiadomość przeżyje restart RabbitMQ

        _channel.BasicPublish(
            exchange: "",           // pusty = domyślny exchange, routing przez nazwę kolejki
            routingKey: QueueName,  // nazwa kolejki docelowej
            basicProperties: properties,
            body: body
        );
    }

    // Zwalniamy zasoby gdy aplikacja się kończy
    public void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
    }
}
