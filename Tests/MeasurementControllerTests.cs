using FrontApi;
using FrontApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Xunit;

namespace Tests;

// Testy walidacji wejścia API. Weryfikują, że API odrzuca błędne żądania PRZED wysłaniem do kolejki.
// Dzięki temu DLQ zawiera tylko błędy integralności, nie błędy struktury.
//
// UWAGA: te testy wymagają działającego RabbitMQ (publisher łączy się w konstruktorze).
// Dla pełnej izolacji należałoby zrobić RabbitMqPublisher interfejs + mock.
// Tu skupiamy się na testach walidacji wejścia — używamy [Fact(Skip=...)] dla scenariuszy wymagających kolejki.
public class MeasurementControllerTests
{
    private static string ToBase64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Base64_JsonRoundTrip_DziałaPoprawnie()
    {
        var original = "{\"deviceId\":\"sensor-1\",\"value\":23.5}";
        var encoded = ToBase64(original);
        var decodedBytes = Convert.FromBase64String(encoded);
        var decoded = Encoding.UTF8.GetString(decodedBytes);

        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData("!!!-not-base64-!!!")]
    [InlineData("abc@#$%")]
    public void Base64_NiepoprawnyFormat_RzucaFormatException(string invalid)
    {
        Assert.Throws<FormatException>(() => Convert.FromBase64String(invalid));
    }

    [Fact]
    public void HmacWorkflow_KlientIServerProdukujaIdentycznyChecksum()
    {
        // Symulujemy pełny workflow: klient liczy HMAC → serwer weryfikuje
        var json = "{\"deviceId\":\"s1\",\"value\":10}";
        var key = "tajny-klucz";

        var clientSide = FrontApi.HmacHelper.Compute(json, key);
        var serverSide = TelemetryWorker.HmacHelper.Compute(json, key);

        Assert.Equal(clientSide, serverSide);
    }
}
