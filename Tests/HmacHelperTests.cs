using Xunit;

namespace Tests;

// Testy jednostkowe sprawdzające spójność algorytmu HMAC między FrontApi a TelemetryWorker.
// HMAC jest fundamentem walidacji integralności — oba projekty MUSZĄ produkować ten sam hash.
public class HmacHelperTests
{
    private const string TestKey = "test-key-123";
    private const string TestData = "{\"deviceId\":\"sensor-1\",\"value\":23.5}";

    [Fact]
    public void Compute_ZwracaTakiSamHashDlaTychSamychDanych()
    {
        // Arrange & Act
        var apiHash = FrontApi.HmacHelper.Compute(TestData, TestKey);
        var workerHash = TelemetryWorker.HmacHelper.Compute(TestData, TestKey);

        // Assert — API i Worker muszą generować identyczny hash
        Assert.Equal(apiHash, workerHash);
    }

    [Fact]
    public void Compute_HashMaZawsze64ZnakiHex()
    {
        // HMAC-SHA256 zawsze produkuje 32 bajty = 64 znaki hex
        var hash = TelemetryWorker.HmacHelper.Compute(TestData, TestKey);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    [Fact]
    public void Compute_RozneDaneDajaRozneHashe()
    {
        var hash1 = TelemetryWorker.HmacHelper.Compute("{\"value\":1}", TestKey);
        var hash2 = TelemetryWorker.HmacHelper.Compute("{\"value\":2}", TestKey);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Compute_RozneKluczeDajaRozneHashe()
    {
        var hash1 = TelemetryWorker.HmacHelper.Compute(TestData, "key-A");
        var hash2 = TelemetryWorker.HmacHelper.Compute(TestData, "key-B");

        Assert.NotEqual(hash1, hash2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"a\":1}")]
    [InlineData("długi tekst z polskimi znakami ąęółńśź")]
    public void Compute_ObslugujeRozneDlugosciWejscia(string input)
    {
        var hash = TelemetryWorker.HmacHelper.Compute(input, TestKey);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Compute_ZmianaJednegoZnakuCałkowicieZmieniaHash()
    {
        // Efekt lawinowy — cecha dobrego hasha kryptograficznego
        var original = TelemetryWorker.HmacHelper.Compute("{\"value\":23.5}", TestKey);
        var modified = TelemetryWorker.HmacHelper.Compute("{\"value\":23.6}", TestKey);

        Assert.NotEqual(original, modified);

        // Co najmniej połowa znaków powinna się różnić (efekt lawinowy)
        int different = 0;
        for (int i = 0; i < original.Length; i++)
            if (original[i] != modified[i]) different++;

        Assert.True(different > original.Length / 2,
            $"Oczekiwano efektu lawinowego, różnic: {different}/{original.Length}");
    }
}
