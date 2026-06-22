using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using NetprobeSharp;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var configPath = Environment.GetEnvironmentVariable("NETPROBE_ConfigPath");
if (string.IsNullOrWhiteSpace(configPath))
    configPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

var builder = Host.CreateEmptyApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        ApplicationName = "NetprobeSharp",
        Args            = args,
        ContentRootPath = configPath,
        DisableDefaults = true,
        EnvironmentName = "Production"
    });

builder.ConfigureContainer(
    new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));

builder.Logging.AddSimpleConsole(opts =>
{
    opts.ColorBehavior = LoggerColorBehavior.Default;
    opts.IncludeScopes = true;
    opts.SingleLine    = false;
});
builder.Logging.AddDebug();
builder.Logging.AddEventSourceLogger();

builder.Configuration.AddJsonFile("netprobe.jsonc", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("NETPROBE_");
builder.Configuration.AddCommandLine(args);

builder.Services.AddOptionsWithValidateOnStart<NetprobeOptions>().Bind(builder.Configuration);
builder.Services.AddSingleton<IValidateOptions<NetprobeOptions>, NetprobeOptionsValidator>();

builder.Services
       .AddOpenTelemetry()
       .WithMetrics(metricsBuilder =>
        {
            metricsBuilder.AddMeter(ProberService.MeterName).AddPrometheusHttpListener();
        });

builder.Services.AddTransient<IDnsProber, DnsProber>();
builder.Services.AddTransient<IPingProber, PingProber>();
builder.Services.AddHostedService<ProberService>();

var app = builder.Build();

await app.RunAsync();

// Uri.CheckHostName()
