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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.CurrentValue.ProbeIntervalSec));
        do
        {
            try
            {
                // Re-read configuration every cycle so edits to netprobe.jsonc (reloadOnChange)
                // take effect without a restart, and update the tick interval to match. Reading
                // CurrentValue re-runs validation, so an invalid live edit throws here -- caught
                // below so the service keeps running on the last good cadence instead of crashing.
                var options = _options.CurrentValue;
                timer.Period = TimeSpan.FromSeconds(options.ProbeIntervalSec);

                await RunProbeCycleAsync(options, stoppingToken).ConfigureAwait(false);
            }
            // A single failed cycle (bad reload, 'ping' can't be spawned, ...) shouldn't tear
            // down the host -- log it and try again next tick. Cancellation on shutdown is not
            // an error, so let it propagate out of the loop.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Probe cycle failed; retrying after the next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunProbeCycleAsync(NetprobeOptions options, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running ping probes...");

        var t0 = Stopwatch.GetTimestamp();
        var pingProbes = await Task.WhenAll(
                             options.Sites.Select(site => _pingProber.ProbeAsync(
                                                      site,
                                                      options.ProbeCountPerSite,
                                                      cancellationToken: stoppingToken)));
        LogPingProbesElapsedMs(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);

        // Probers report null for a figure they couldn't measure; treat a missing value as
        // the worst case (its threshold) so an outage drags the score down and is recorded
        // rather than silently disappearing.
        var score = options.Score;

        double lossSum = 0, latencySum = 0, jitterSum = 0;
        foreach (var pingProbe in pingProbes)
        {
            var loss    = pingProbe.Loss    ?? score.LossThreshold;
            var latency = pingProbe.Latency ?? score.LatencyThreshold;
            var jitter  = pingProbe.Jitter  ?? score.JitterThreshold;

            lossSum += loss;
            _networkStats.Record(
                loss,
                new KeyValuePair<string, object?>("type",   "loss"),
                new KeyValuePair<string, object?>("target", pingProbe.Site));

            latencySum += latency;
            _networkStats.Record(
                latency,
                new KeyValuePair<string, object?>("type",   "latency"),
                new KeyValuePair<string, object?>("target", pingProbe.Site));

            jitterSum += jitter;
            _networkStats.Record(
                jitter,
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
            var latency = dnsProbe.Latency ?? score.DnsThreshold;
            _dnsStats.Record(latency, new KeyValuePair<string, object?>("server", dnsProbe.Resolver.Name));

            if (string.Equals(dnsProbe.Resolver.Name, "My_DNS_Server", StringComparison.OrdinalIgnoreCase))
                usersDnsServer = latency;
        }

        _healthStats.Record(HealthScore.Compute(avgLoss, avgLatency, avgJitter, usersDnsServer, score));
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
