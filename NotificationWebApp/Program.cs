using NotificationWebApp.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Rejestrujemy SignalR — obsługuje komunikację real-time z klientami przez WebSockets
builder.Services.AddSignalR();

// Kontrolery potrzebne do odbioru webhooków z InfluxDB
builder.Services.AddControllers();

// Swagger do testowania webhook endpointa
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Middleware logujący każde żądanie (zgodnie z wymaganiem "Czytelność logów")
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("[{Time}] {Method} {Path}",
        DateTime.UtcNow.ToString("HH:mm:ss"),
        context.Request.Method,
        context.Request.Path);
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Statyczne pliki UI (index.html, signalr client)
app.UseDefaultFiles();
app.UseStaticFiles();

// Endpoint webhooka i kontrolery
app.MapControllers();

// SignalR hub — klienci łączą się pod adresem /alertHub
app.MapHub<AlertHub>("/alertHub");

app.Run();
