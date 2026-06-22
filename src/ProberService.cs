using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;

namespace NetprobeSharp;

public sealed partial class ProberService : BackgroundService
{
    public static readonly string MeterName = "NetprobeSharp";

    private readonly ILogger<ProberService>           _logger;
    private readonly IOptionsMonitor<NetprobeOptions> _options;
    private readonly IPingProber                      _pingProber;
    private readonly IDnsProber                       _dnsProber;
    private readonly Meter                            _meter;
    private readonly Gauge<double>                    _networkStats, _dnsStats, _healthStats;

    public ProberService(
        ILogger<ProberService>           logger,
        IOptionsMonitor<NetprobeOptions> options,
        IPingProber                      pingProber,
        IDnsProber                       dnsProber)
    {
        _logger     = logger;
        _options    = options;
        _pingProber = pingProber;
        _dnsProber  = dnsProber;
        _meter      = new Meter(MeterName);
        _networkStats = _meter.CreateGauge<double>(
            "Network_Stats",
            description: "Average Latency, Packet Loss and Jitter for pings to each site.");
        _dnsStats = _meter.CreateGauge<double>(
            "DNS_Stats",
            description: "Average DNS Latency to each resolver.");
        _healthStats = _meter.CreateGauge<double>(
            "Health_Stats",
            description: "Internet health score calculation.");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var       options = _options.CurrentValue;
        using var timer   = new PeriodicTimer(TimeSpan.FromSeconds(options.ProbeIntervalSec));
        do
        {
            _logger.LogInformation("Running ping probes...");

            var t0 = Stopwatch.GetTimestamp();
            var pingProbes = await Task.WhenAll(
                                 options.Sites.Select(site => _pingProber.ProbeAsync(
                                                          site,
                                                          options.ProbeCountPerSite,
                                                          cancellationToken: stoppingToken)));
            LogPingProbesElapsedMs(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);

            double lossSum = 0, latencySum = 0, jitterSum = 0;
            foreach (var pingProbe in pingProbes)
            {
                lossSum += pingProbe.Loss;
                _networkStats.Record(
                    pingProbe.Loss,
                    new KeyValuePair<string, object?>("type",   "loss"),
                    new KeyValuePair<string, object?>("target", pingProbe.Site));

                latencySum += pingProbe.Latency;
                _networkStats.Record(
                    pingProbe.Latency,
                    new KeyValuePair<string, object?>("type",   "latency"),
                    new KeyValuePair<string, object?>("target", pingProbe.Site));

                jitterSum += pingProbe.Jitter;
                _networkStats.Record(
                    pingProbe.Jitter,
                    new KeyValuePair<string, object?>("type",   "jitter"),
                    new KeyValuePair<string, object?>("target", pingProbe.Site));
            }
            var avgLoss    = lossSum    / pingProbes.Length;
            var avgLatency = latencySum / pingProbes.Length;
            var avgJitter  = jitterSum  / pingProbes.Length;

            _logger.LogInformation("Running DNS probes...");

            t0 = Stopwatch.GetTimestamp();
            var dnsProbes = await Task.WhenAll(
                                options.DnsResolvers.Select(resolver => _dnsProber.ProbeAsync(
                                                                new DnsResolver(
                                                                    resolver.Key,
                                                                    IPAddress.Parse(resolver.Value)),
                                                                options.DnsTestSite,
                                                                cancellationToken: stoppingToken)));
            LogDnsProbesElapsedMs(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);

            double usersDnsServer = 0;
            foreach (var dnsProbe in dnsProbes)
            {
                _dnsStats.Record(dnsProbe.Latency, new KeyValuePair<string, object?>("server", dnsProbe.Resolver.Name));

                if (string.Equals(dnsProbe.Resolver.Name, "My_DNS_Server", StringComparison.OrdinalIgnoreCase))
                    usersDnsServer = dnsProbe.Latency;
            }

            var scoreLoss    = options.Score.LossWeight    * Math.Min(1.0, avgLoss / options.Score.LossThreshold);
            var scoreLatency = options.Score.LatencyWeight * Math.Min(1.0, avgLatency / options.Score.LatencyThreshold);
            var scoreJitter  = options.Score.JitterWeight  * Math.Min(1.0, avgJitter / options.Score.JitterThreshold);
            var scoreDns     = options.Score.DnsWeight     * Math.Min(1.0, usersDnsServer / options.Score.DnsThreshold);
            var score        = 1 - scoreLoss - scoreLatency - scoreJitter - scoreDns;
            _healthStats.Record(score);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }

    [LoggerMessage(LogLevel.Information, "Ping probes executed in {ElapsedMs}ms")]
    partial void LogPingProbesElapsedMs(double elapsedMs);

    [LoggerMessage(LogLevel.Information, "Dns probes executed in {ElapsedMs}ms")]
    partial void LogDnsProbesElapsedMs(double elapsedMs);
}
