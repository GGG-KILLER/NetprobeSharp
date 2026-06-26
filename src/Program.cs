using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using NetprobeSharp;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;
using OpenTelemetry.Metrics;

var configPath = Environment.GetEnvironmentVariable("NETPROBE_ConfigPath");
if (string.IsNullOrWhiteSpace(configPath))
    configPath = AppContext.BaseDirectory;

var builder = WebApplication.CreateSlimBuilder(
    new WebApplicationOptions
    {
        ApplicationName = "NetprobeSharp",
        Args            = args,
        ContentRootPath = configPath,
        EnvironmentName = "Production",
    });

// Default listen address — users can override via ASPNETCORE_URLS or the "urls" key.
// Kestrel accepts "+" as a wildcard host (equivalent to 0.0.0.0 / [::]).
builder.Configuration["urls"] ??= "http://+:9464";

builder.Logging.AddSimpleConsole(opts =>
{
    opts.ColorBehavior = LoggerColorBehavior.Default;
    opts.IncludeScopes = true;
    opts.SingleLine    = false;
});
builder.Logging.AddDebug();

builder.Configuration.AddJsonFile("netprobe.jsonc", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("NETPROBE_");
builder.Configuration.AddCommandLine(args);

builder.Services.AddOptionsWithValidateOnStart<NetprobeOptions>().Bind(builder.Configuration);
builder.Services.AddSingleton<IValidateOptions<NetprobeOptions>, NetprobeOptionsValidator>();

builder.Services
       .AddOpenTelemetry()
       .WithMetrics(metricsBuilder =>
        {
            metricsBuilder
                .AddMeter(ProberService.MeterName)
                .AddPrometheusExporter();
        });

builder.Services.AddHealthChecks();

builder.Services.AddTransient<IDnsProber, DnsProber>();
builder.Services.AddTransient<IPingProber, PingProber>();
builder.Services.AddHostedService<ProberService>();

var app = builder.Build();

app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapHealthChecks("/health");

await app.RunAsync();
