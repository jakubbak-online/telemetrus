using System.Security.Cryptography;
using System.Text;

namespace NotificationWebApp;

// Ten plik jest identyczny jak w FrontApi i TelemetryWorker — musi być, bo używamy tego samego
// klucza i algorytmu. DemoController liczy HMAC lokalnie (po stronie serwera), żeby
// przycisk "Wyślij testowy pomiar" w UI mógł symulować prawdziwego klienta bez wystawiania
// współdzielonego sekretu w kodzie przeglądarki.
public static class HmacHelper
{
    // Wartość fallback gdy brak klucza w konfiguracji — preferuj ustawienie Hmac:SecretKey w appsettings.json
    public const string FallbackKey = "telemetrus-demo-shared-secret";

    public static string Compute(string data, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(data);

        using var hmac = new HMACSHA256(keyBytes);
        byte[] hashBytes = hmac.ComputeHash(dataBytes);

        return Convert.ToHexString(hashBytes).ToLower();
    }
}
