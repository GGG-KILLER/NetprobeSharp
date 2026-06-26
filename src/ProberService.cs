using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Options;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;
using OpenTelemetry.Metrics;

namespace NetprobeSharp;

public sealed partial class ProberService : BackgroundService
{
    public static readonly string MeterName = "NetprobeSharp";

    // RTT bucket boundaries in seconds, sized for network latency (1 ms – 2 s).
    private static readonly double[] s_rttBuckets =
    [
        0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.0
    ];

    private readonly ILogger<ProberService>           _logger;
    private readonly IOptionsMonitor<NetprobeOptions> _options;
    private readonly IPingProber                      _pingProber;
    private readonly IDnsProber                       _dnsProber;
    internal readonly Meter                            _meter;

    // Histograms — emit _bucket / _sum / _count automatically.
    private readonly Histogram<double> _pingRtt;
    private readonly Histogram<double> _dnsQueryDuration;

    // Tracks when the last probe cycle completed — read by ProberServiceHealthCheck.
    // Zero means "never completed". Written via Interlocked to avoid torn reads on 32-bit.
    internal long _lastCycleTicks;
    internal DateTimeOffset? LastCycleCompletedAt =>
        _lastCycleTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _lastCycleTicks), TimeSpan.Zero);

    // Gauges
    private readonly Gauge<double> _pingJitter;
    private readonly Gauge<double> _pingLossRatio;
    private readonly Gauge<double> _pingUp;
    private readonly Gauge<double> _dnsUp;
    private readonly Gauge<double> _healthScore;
    private readonly Gauge<double> _buildInfo;

    public ProberService(
        ILogger<ProberService>           logger,
        IOptionsMonitor<NetprobeOptions> options,
        IPingProber                      pingProber,
        IDnsProber                       dnsProber,
        MeterProvider                    meterProvider) // ensures OTel pipeline is started
    {
        _logger     = logger;
        _options    = options;
        _pingProber = pingProber;
        _dnsProber  = dnsProber;
        _meter      = new Meter(MeterName);

        _pingRtt = _meter.CreateHistogram<double>(
            "netprobe_ping_rtt",
            unit: "s",
            description: "Round-trip time per ping reply.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = s_rttBuckets });

        _dnsQueryDuration = _meter.CreateHistogram<double>(
            "netprobe_dns_query_duration",
            unit: "s",
            description: "DNS query round-trip latency.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = s_rttBuckets });

        _pingJitter = _meter.CreateGauge<double>(
            "netprobe_ping_jitter",
            unit: "s",
            description: "Ping RTT population stddev (mdev) per target.");

        _pingLossRatio = _meter.CreateGauge<double>(
            "netprobe_ping_packet_loss",
            unit: "ratio",
            description: "Fraction of pings that received no reply (0–1).");

        _pingUp = _meter.CreateGauge<double>(
            "netprobe_ping_up",
            description: "1 if the last ping probe returned a parseable summary, else 0.");

        _dnsUp = _meter.CreateGauge<double>(
            "netprobe_dns_up",
            description: "1 if the last DNS probe received a reply, else 0.");

        _healthScore = _meter.CreateGauge<double>(
            "netprobe_health_score",
            description: "Internet quality score (0–1, higher is better).");

        // Conventional version exposure: always 1, version in a label.
        _buildInfo = _meter.CreateGauge<double>(
            "netprobe_build_info",
            description: "Build information; value is always 1.");

        var version = Assembly
                     .GetEntryAssembly()
                    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                   ?? "unknown";
        _buildInfo.Record(1, new KeyValuePair<string, object?>("version", version));
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

    internal async Task RunProbeCycleAsync(NetprobeOptions options, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running ping probes...");

        var t0 = Stopwatch.GetTimestamp();
        var pingProbes = await Task.WhenAll(
                             options.Sites.Select(site => _pingProber.ProbeAsync(
                                                      site,
                                                      options.ProbeCountPerSite,
                                                      options.PingTimeoutMs,
                                                      options.PingSpacingMs,
                                                      stoppingToken)));
        LogPingProbesElapsedMs(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);

        var score = options.Score;

        // Accumulators for the health score (in ms / %, matching ScoreOptions thresholds).
        double lossSum = 0, latencySum = 0, jitterSum = 0;

        foreach (var pingProbe in pingProbes)
        {
            var target = new KeyValuePair<string, object?>("target", pingProbe.Site);
            var up     = pingProbe.Loss.HasValue ? 1.0 : 0.0;
            _pingUp.Record(up, target);

            // Loss ratio — always recorded (1 when totally unreachable or parse failure).
            var lossRatio = (pingProbe.Loss ?? 100.0) / 100.0;
            _pingLossRatio.Record(lossRatio, target);

            // RTT histogram — only when we have actual replies.
            foreach (var rtt in pingProbe.Rtts)
                _pingRtt.Record(rtt / 1000.0, target); // ms → s

            // Jitter — only when measurable.
            if (pingProbe.Jitter.HasValue)
                _pingJitter.Record(pingProbe.Jitter.Value / 1000.0, target); // ms → s

            // Score inputs (in ms / %) — substitute threshold on failure so an outage
            // drags the score down rather than disappearing.
            lossSum += pingProbe.Loss ?? score.LossThreshold;
            var meanMs = pingProbe.Rtts.Count > 0
                             ? pingProbe.Rtts.Average()
                             : score.LatencyThreshold;
            latencySum += meanMs;
            jitterSum  += pingProbe.Jitter ?? score.JitterThreshold;
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
                                                            options.DnsTimeoutMs,
                                                            stoppingToken)));
        LogDnsProbesElapsedMs(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);

        double usersDnsServer = 0;
        foreach (var dnsProbe in dnsProbes)
        {
            var resolver = new KeyValuePair<string, object?>("resolver", dnsProbe.Resolver.Name);
            _dnsUp.Record(dnsProbe.Latency.HasValue ? 1.0 : 0.0, resolver);

            if (dnsProbe.Latency.HasValue)
                _dnsQueryDuration.Record(dnsProbe.Latency.Value / 1000.0, resolver); // ms → s

            if (string.Equals(dnsProbe.Resolver.Name, "My_DNS_Server", StringComparison.OrdinalIgnoreCase))
                usersDnsServer = dnsProbe.Latency ?? score.DnsThreshold;
        }

        _healthScore.Record(HealthScore.Compute(avgLoss, avgLatency, avgJitter, usersDnsServer, score));
        Interlocked.Exchange(ref _lastCycleTicks, DateTimeOffset.UtcNow.Ticks);
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
