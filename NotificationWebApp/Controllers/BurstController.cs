using Microsoft.AspNetCore.Mvc;

namespace NotificationWebApp.Controllers;

// BurstController steruje BurstService z UI — start/stop/status jednego przebiegu testu
// obciążeniowego uruchamianego z przeglądarki (panel "Test obciążeniowy" w wwwroot/index.html).
// Postęp jest rozgłaszany przez SignalR (BurstStarted/BurstProgress/BurstFinished), nie przez
// odpowiedź HTTP tych endpointów — Start tylko odpala przebieg i wraca od razu.
[ApiController]
[Route("demo/burst")]
public class BurstController : ControllerBase
{
    private const int MaxTotalCount = 20_000;
    private const int MaxDurationSeconds = 900;

    private readonly BurstService _burstService;

    public BurstController(BurstService burstService)
    {
        _burstService = burstService;
    }

    // POST http://localhost:5002/demo/burst/start
    [HttpPost("start")]
    public IActionResult Start([FromBody] BurstStartRequest? request)
    {
        request ??= new BurstStartRequest();

        if (request.TotalCount < 1 || request.TotalCount > MaxTotalCount)
            return BadRequest(new { error = $"'totalCount' musi być z zakresu 1-{MaxTotalCount}." });

        if (request.DurationSeconds < 1 || request.DurationSeconds > MaxDurationSeconds)
            return BadRequest(new { error = $"'durationSeconds' musi być z zakresu 1-{MaxDurationSeconds}." });

        if (request.HighValueRatio < 0 || request.HighValueRatio > 1)
            return BadRequest(new { error = "'highValueRatio' musi być z zakresu 0.0-1.0." });

        var options = new BurstOptions
        {
            TotalCount = request.TotalCount,
            DurationSeconds = request.DurationSeconds,
            HighValueRatio = request.HighValueRatio
        };

        bool started = _burstService.Start(options);
        if (!started)
            return Conflict(new { error = "Test obciążeniowy już trwa. Zatrzymaj go najpierw." });

        return Accepted(new { status = "started", options.TotalCount, options.DurationSeconds, options.HighValueRatio });
    }

    // POST http://localhost:5002/demo/burst/stop
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        bool stopped = _burstService.Stop();
        return Ok(new { status = stopped ? "stopping" : "not_running" });
    }

    // GET http://localhost:5002/demo/burst/status
    [HttpGet("status")]
    public IActionResult Status() => Ok(new { running = _burstService.IsRunning });
}

public class BurstStartRequest
{
    public int TotalCount { get; set; } = 1000;
    public int DurationSeconds { get; set; } = 60;
    public double HighValueRatio { get; set; } = 0.05;
}
