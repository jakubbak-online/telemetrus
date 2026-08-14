using TelemetryWorker;

var builder = Host.CreateApplicationBuilder(args);

// Singleton — jeden klient InfluxDB przez całe życie aplikacji (nie tworzony per-wiadomość)
builder.Services.AddSingleton<InfluxWriter>();

// Rejestrujemy naszego Workera — .NET sam go uruchomi w tle
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
