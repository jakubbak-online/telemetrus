using System.Security.Cryptography;
using System.Text;

namespace TelemetryWorker;

// Ten plik jest identyczny jak w FrontApi — musi być, bo używamy tego samego klucza i algorytmu.
// Worker oblicza hash z odebranych danych i porównuje z hashem który przysłało API.
// Jeśli się zgadzają — dane są nienaruszone. Jeśli nie — coś po drodze poszło nie tak.
public static class HmacHelper
{
    // MUSI być identyczny jak w FrontApi/HmacHelper.cs !
    public const string SecretKey = "telemetrus-demo-shared-secret";

    public static string Compute(string data, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(data);

        using var hmac = new HMACSHA256(keyBytes);
        byte[] hashBytes = hmac.ComputeHash(dataBytes);

        return Convert.ToHexString(hashBytes).ToLower();
    }
}
