using FrontApi;

var builder = WebApplication.CreateBuilder(args);

// Rejestrujemy RabbitMqPublisher jako "singleton" — jedna instancja przez całe życie aplikacji.
// Dzięki temu połączenie z RabbitMQ jest otwarte od startu i nie otwieramy go przy każdym żądaniu.
builder.Services.AddSingleton<RabbitMqPublisher>();

// Rejestrujemy kontrolery API
builder.Services.AddControllers();

// Swagger — interfejs webowy do testowania API w przeglądarce (http://localhost:5000/swagger)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Globalny handler wyjątków — musi być PIERWSZY w pipeline, żeby łapać też błędy spoza akcji
// kontrolera (np. RabbitMqPublisher rzuca w konstruktorze, gdy RabbitMQ jest nieosiągalne;
// to dzieje się podczas tworzenia kontrolera przez DI, więc żaden try/catch w akcji tego nie
// złapie). Bez tego middleware ASPNETCORE_ENVIRONMENT=Development (ustawione w docker-compose.yml,
// żeby działał Swagger) zwraca klientowi pełny stack trace .NET zamiast czytelnego 500.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Nieobsłużony wyjątek podczas przetwarzania żądania {Path}.", context.Request.Path);

        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Błąd wewnętrzny serwera." });
        }
    }
});

// Middleware do logowania każdego żądania — wywoływany przed każdym requestem
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

    // Czytamy body żądania (musimy skopiować stream bo można go czytać tylko raz)
    context.Request.EnableBuffering();
    var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
    context.Request.Body.Position = 0; // resetujemy pozycję żeby kontroler też mógł przeczytać

    logger.LogInformation(
        "[{Time}] {Method} {Path} | Body: {Body}",
        DateTime.UtcNow.ToString("HH:mm:ss"),
        context.Request.Method,
        context.Request.Path,
        body.Length > 200 ? body[..200] + "..." : body // nie logujemy więcej niż 200 znaków
    );

    await next(); // przekazujemy żądanie dalej (do kontrolera)

    logger.LogInformation("[{Time}] Odpowiedź: {StatusCode}", DateTime.UtcNow.ToString("HH:mm:ss"), context.Response.StatusCode);
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();
