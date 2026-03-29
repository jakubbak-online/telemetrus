using TelemetryWorker;

var builder = Host.CreateApplicationBuilder(args);

// Rejestrujemy naszego Workera — .NET sam go uruchomi w tle
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
