using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using NetPace.Core;
using NetPace.Core.Clients.Ookla;
using NetPace.Core.Clients.Ookla.Settings;
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
               .AddMeter(SpeedTester.MeterName)
               .AddPrometheusExporter();
        });

builder.Services.AddHealthChecks()
       .AddCheck<ProberServiceHealthCheck>("prober")
       .AddCheck<SpeedTesterHealthCheck>("speedtest");

builder.Services.AddTransient<IDnsProber, DnsProber>();
builder.Services.AddTransient<IPingProber, PingProber>();
builder.Services.AddSingleton<ProberService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProberService>());

// SpeedTest: use an explicit factory so ctor params are visible and adjustable here.
builder.Services.AddSingleton<ISpeedTestService>(_ =>
{
    return new OoklaSpeedtest(
        speedtestSettings: new OoklaSpeedtestSettings
                           {
                               DownloadTest = new DownloadTestSettings
                                              {
                                                  DownloadSizes          = [ 2000, 2500, 3000, 3500, 4000 ],
                                                  DownloadSizeIterations = 12,
                                                  DownloadParallelTasks  = 16,
                                              },
                               UploadTest = new UploadTestSettings
                                            {
                                                UploadSizeIncrementKb = 500,
                                                UploadIncrements      = 8,
                                                UploadSizeIterations  = 12,
                                                UploadParallelTasks   = 16,
                                            }
                           },
        httpClientOverride: null,
        delayProviderOverride: new DelayProvider());
});
builder.Services.AddSingleton<SpeedTester>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SpeedTester>());

var app = builder.Build();

app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapHealthChecks("/health");

await app.RunAsync();
