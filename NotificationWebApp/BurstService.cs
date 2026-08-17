using Microsoft.AspNetCore.SignalR;
using NotificationWebApp.Hubs;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NotificationWebApp;

// BurstService generuje sztuczny ruch pomiarowy w tle: rozkłada zadaną liczbę żądań równomiernie
// w czasie (domyślnie 1000 w ciągu 60s) i wysyła je do FrontApi tą samą ścieżką co DemoController
// (Base64 + HMAC-SHA256, więc żadna walidacja ani weryfikacja checksum nie jest omijana).
// Część pomiarów losowo przekracza próg alertu InfluxDB, żeby dało się zademonstrować pełną
// ścieżkę: API -> RabbitMQ -> Worker -> InfluxDB -> Check -> webhook -> SignalR -> UI.
//
// Zarejestrowany jako singleton — trzyma stan JEDNEGO aktywnego przebiegu naraz (proste demo,
// nie wieloużytkownikowy load-test runner). Kolejny Start() podczas trwającego przebiegu jest
// odrzucany (Conflict w kontrolerze), zamiast startować równoległe serie.
public class BurstService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly IHubContext<AlertHub> _hubContext;
    private readonly ILogger<BurstService> _logger;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _runningTask;

    public BurstService(IHttpClientFactory httpClientFactory, IConfiguration config,
        IHubContext<AlertHub> hubContext, ILogger<BurstService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _hubContext = hubContext;
        _logger = logger;
    }

    public bool IsRunning
    {
        get { lock (_lock) { return _runningTask is { IsCompleted: false }; } }
    }

    public bool Start(BurstOptions options)
    {
        lock (_lock)
        {
            if (_runningTask is { IsCompleted: false }) return false;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _runningTask = Task.Run(() => RunAsync(options, token), CancellationToken.None);
            return true;
        }
    }

    public bool Stop()
    {
        lock (_lock)
        {
            if (_runningTask is not { IsCompleted: false }) return false;
            _cts?.Cancel();
            return true;
        }
    }

    private async Task RunAsync(BurstOptions options, CancellationToken token)
    {
        var totalCount = options.TotalCount;
        var interval = TimeSpan.FromSeconds((double)options.DurationSeconds / totalCount);
        var frontApiUrl = _config["FrontApi:Url"] ?? "http://localhost:5000";
        var hmacKey = _config["Hmac:SecretKey"] ?? HmacHelper.FallbackKey;
        var client = _httpClientFactory.CreateClient(nameof(BurstService));
        var rnd = new Random();

        int sent = 0, ok = 0, failed = 0, highValueSent = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation(
            "Burst start: {Count} pomiarów w {Duration}s (~{Rate:F1}/s), {Pct:P0} > progu.",
            totalCount, options.DurationSeconds, totalCount / (double)options.DurationSeconds, options.HighValueRatio);

        await SafeBroadcast("BurstStarted", new { totalCount, options.DurationSeconds });

        using var timer = new PeriodicTimer(interval);
        for (int i = 0; i < totalCount && !token.IsCancellationRequested; i++)
        {
            bool isHigh = rnd.NextDouble() < options.HighValueRatio;
            double value = isHigh
                ? Math.Round(options.HighValueMin + rnd.NextDouble() * (options.HighValueMax - options.HighValueMin), 2)
                : Math.Round(options.NormalMin + rnd.NextDouble() * (options.NormalMax - options.NormalMin), 2);
            if (isHigh) Interlocked.Increment(ref highValueSent);

            string deviceId = $"burst-sensor-{i % 20:00}";

            // Fire-and-forget: NIE czekamy na odpowiedź w pętli, żeby nie zaburzać tempa wysyłki
            // czasem odpowiedzi FrontApi. Liczniki aktualizowane atomowo po zakończeniu każdego żądania.
            _ = SendOneAsync(client, frontApiUrl, hmacKey, deviceId, value)
                .ContinueWith(t =>
                {
                    Interlocked.Increment(ref sent);
                    if (t.Result) Interlocked.Increment(ref ok); else Interlocked.Increment(ref failed);
                }, TaskScheduler.Default);

            if ((i + 1) % 25 == 0 || i == totalCount - 1)
            {
                await SafeBroadcast("BurstProgress", new
                {
                    sent = Volatile.Read(ref sent),
                    total = totalCount,
                    ok = Volatile.Read(ref ok),
                    failed = Volatile.Read(ref failed),
                    highValueSent = Volatile.Read(ref highValueSent),
                    elapsedSeconds = sw.Elapsed.TotalSeconds
                });
            }

            try { await timer.WaitForNextTickAsync(token); }
            catch (OperationCanceledException) { break; }
        }

        // Krótki bufor, żeby ostatnie "fire-and-forget" żądania zdążyły dobić i zaktualizować liczniki
        await Task.Delay(1500, CancellationToken.None);

        bool cancelled = token.IsCancellationRequested;
        _logger.LogInformation(
            "Burst zakończony{Cancelled}: wysłano={Sent} ok={Ok} błędy={Failed} >próg={High} czas={Elapsed:F1}s",
            cancelled ? " (przerwany)" : "", sent, ok, failed, highValueSent, sw.Elapsed.TotalSeconds);

        await SafeBroadcast("BurstFinished", new
        {
            sent, ok, failed, highValueSent, elapsedSeconds = sw.Elapsed.TotalSeconds, cancelled
        });
    }

    private async Task<bool> SendOneAsync(HttpClient client, string frontApiUrl, string hmacKey, string deviceId, double value)
    {
        try
        {
            string dataJson = JsonSerializer.Serialize(new { deviceId, value });
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(dataJson));
            string checksum = HmacHelper.Compute(dataJson, hmacKey);

            using var response = await client.PostAsJsonAsync($"{frontApiUrl}/measurement",
                new { payload, checksum, channel = "default" });
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Burst: pojedyncze żądanie nie powiodło się.");
            return false;
        }
    }

    private async Task SafeBroadcast(string method, object payload)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(method, payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Burst: nie udało się rozgłosić {Method} przez SignalR.", method);
        }
    }
}

public class BurstOptions
{
    public int TotalCount { get; set; } = 1000;
    public int DurationSeconds { get; set; } = 60;
    public double HighValueRatio { get; set; } = 0.05; // 5% pomiarów przekracza próg CRIT
    public double HighValueMin { get; set; } = 81;
    public double HighValueMax { get; set; } = 99;
    public double NormalMin { get; set; } = 0;
    public double NormalMax { get; set; } = 55;
}
