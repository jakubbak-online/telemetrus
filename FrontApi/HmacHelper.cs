using System.Security.Cryptography;
using System.Text;

namespace FrontApi;

// HmacHelper generuje "podpis cyfrowy" danych.
// HMAC-SHA256 to algorytm, który bierze dane + tajny klucz i zwraca 64-znakowy hash.
// Jeśli dane lub klucz się zmienią — hash będzie zupełnie inny.
// Worker używa tego samego klucza i tej samej metody, żeby sprawdzić czy dane są nienaruszone.
public static class HmacHelper
{
    // UWAGA: w prawdziwej aplikacji klucz powinien być w appsettings.json lub zmiennej środowiskowej,
    // nie hardkodowany w kodzie. Na potrzeby projektu laboratoryjnego jest tu dla prostoty.
    public const string SecretKey = "telemetrus-demo-shared-secret";

    public static string Compute(byte[] data, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);

        using var hmac = new HMACSHA256(keyBytes);
        byte[] hashBytes = hmac.ComputeHash(data);

        // Zamieniamy bajty na string hex, np. "a3f2c1..."
        return Convert.ToHexString(hashBytes).ToLower();
    }

    // Pomocnicza metoda — przyjmuje string zamiast byte[]
    public static string Compute(string data, string secret)
    {
        return Compute(Encoding.UTF8.GetBytes(data), secret);
    }
}
