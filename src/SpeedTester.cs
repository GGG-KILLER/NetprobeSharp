using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using NetPace.Core;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;

namespace NetprobeSharp;

public sealed partial class SpeedTester : BackgroundService
{
    public static readonly string MeterName = "NetprobeSharp.Speedtest";

    private readonly  ILogger<SpeedTester>             _logger;
    private readonly  IOptionsMonitor<NetprobeOptions> _options;
    private readonly  ISpeedTestService                _speedTestService;
    internal readonly Meter                            _meter;

    // Tracks when the last successful speed-test cycle completed — read by SpeedTesterHealthCheck.
    // Zero means "never completed". Written via Interlocked to avoid torn reads on 32-bit.
    internal long _lastCycleTicks;
    internal DateTimeOffset? LastCycleCompletedAt =>
        _lastCycleTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _lastCycleTicks), TimeSpan.Zero);

    // Gauges — all unlabeled so each metric is always a single time series in Prometheus.
    // Server identity is exposed separately via _serverInfo (info-metric pattern).
    private readonly Gauge<double> _latency;
    private readonly Gauge<double> _uploadSpeed;
    private readonly Gauge<double> _downloadSpeed;
    private readonly Gauge<double> _speedTestUp;

    // Info metric: value always 1, server identity in labels. One active series at a time;
    // old servers go stale after reselection rather than accumulating on the numeric metrics.
    private readonly Gauge<double> _serverInfo;

    // Tracks the previously-recorded server so RecordServerInfo can zero its labels
    // before recording the new server, preventing stale series at value 1.
    private IServer? _lastInfoServer;

    /// <inheritdoc />
    public SpeedTester(
        ILogger<SpeedTester>             logger,
        IOptionsMonitor<NetprobeOptions> options,
        ISpeedTestService                speedTestService)
    {
        _logger           = logger;
        _options          = options;
        _speedTestService = speedTestService;
        _meter            = new Meter(MeterName);
        _latency = _meter.CreateGauge<double>(
            "netprobe_speedtest_latency",
            unit: "s",
            description: "Latency to the SpeedTest server (millisecond resolution).");
        _uploadSpeed = _meter.CreateGauge<double>(
            "netprobe_speedtest_upload_speed",
            unit: "By/s",
            description: "The upload speed to the SpeedTest server in bytes per second.");
        _downloadSpeed = _meter.CreateGauge<double>(
            "netprobe_speedtest_download_speed",
            unit: "By/s",
            description: "The download speed from the SpeedTest server in bytes per second.");
        _speedTestUp = _meter.CreateGauge<double>(
            "netprobe_speedtest_up",
            description: "1 if the last SpeedTest ran successfully, else 0.");
        _serverInfo = _meter.CreateGauge<double>(
            "netprobe_speedtest_server_info",
            description: "Always 1. Labels identify the currently selected SpeedTest server.");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.CurrentValue.Speedtest.TestIntervalMin));

        // Server discovery is lazy — no network I/O until the first enabled cycle.
        IServer?       server          = null;
        DateTimeOffset lastReselection = DateTimeOffset.MinValue;

        do
        {
            try
            {
                // Re-read configuration every cycle so edits to netprobe.jsonc (reloadOnChange)
                // take effect without a restart, and update the tick interval to match. Reading
                // CurrentValue re-runs validation, so an invalid live edit throws here -- caught
                // below so the service keeps running on the last good cadence instead of crashing.
                var options = _options.CurrentValue;
                timer.Period = TimeSpan.FromMinutes(options.Speedtest.TestIntervalMin);

                // Record up=0 when disabled so dashboards show a clear "off" state.
                if (!options.Speedtest.Enable)
                {
                    _speedTestUp.Record(0);
                    continue;
                }

                // Select a server on the first enabled cycle, or after re-selection interval.
                if (server is null)
                {
                    LogFetchingServers();
                    var servers = await _speedTestService.GetServersAsync(stoppingToken);
                    LogPickingServer();
                    server = (await _speedTestService.GetFastestServerByLatencyAsync(servers, stoppingToken)).Server;
                    lastReselection = DateTimeOffset.UtcNow;
                    RecordServerInfo(server);
                }
                else if (options.Speedtest.ServerReselectionIntervalMin.HasValue
                      && DateTimeOffset.UtcNow - lastReselection
                      >= TimeSpan.FromMinutes(options.Speedtest.ServerReselectionIntervalMin.Value))
                {
                    LogReselectingServer();
                    var servers = await _speedTestService.GetServersAsync(stoppingToken);
                    server = (await _speedTestService.GetFastestServerByLatencyAsync(servers, stoppingToken)).Server;
                    lastReselection = DateTimeOffset.UtcNow;
                    RecordServerInfo(server);
                }

                await RunCycleAsync(server, options.Speedtest, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _speedTestUp.Record(0);
                _logger.LogError(ex, "Error testing speed against server.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Runs one measurement cycle (latency, download, upload) against <paramref name="server"/>
    /// and records the results. Extracted for unit testability.
    /// </summary>
    internal async Task RunCycleAsync(IServer server, SpeedtestOptions options, CancellationToken ct)
    {
        LogRunningSpeedTest();

        var latency = await _speedTestService.GetServerLatencyAsync(server, ct);
        _latency.Record(latency.LatencyMilliseconds / 1000.0);

        var download = await _speedTestService.GetDownloadSpeedAsync(server, options.DownloadSizeMb, ct);
        _downloadSpeed.Record(BytesPerSecond(download, "download"));

        var upload = await _speedTestService.GetUploadSpeedAsync(server, options.UploadSizeMb, ct);
        _uploadSpeed.Record(BytesPerSecond(upload, "upload"));

        _speedTestUp.Record(1);
        Interlocked.Exchange(ref _lastCycleTicks, DateTimeOffset.UtcNow.Ticks);

        LogSpeedTestFinished();
    }

    internal void RecordServerInfo(IServer server)
    {
        // Zero out the previous server's series so Prometheus doesn't keep reporting it at 1.
        // The 0-valued series vanishes naturally after the staleness window.
        if (_lastInfoServer is { } old && (old.Sponsor != server.Sponsor || old.Location != server.Location))
            _serverInfo.Record(0,
                new KeyValuePair<string, object?>("sponsor",  old.Sponsor),
                new KeyValuePair<string, object?>("location", old.Location));

        _lastInfoServer = server;
        _serverInfo.Record(1,
            new KeyValuePair<string, object?>("sponsor",  server.Sponsor),
            new KeyValuePair<string, object?>("location", server.Location));
    }

    /// <summary>
    /// Converts a <see cref="SpeedTestResult"/> to bytes/second.
    /// If the library reports zero elapsed time (which would be a library bug), logs an error
    /// and returns 0 rather than producing +Infinity.
    /// </summary>
    private double BytesPerSecond(SpeedTestResult result, string leg)
    {
        if (result.ElapsedMilliseconds <= 0)
        {
            LogZeroElapsedMs(leg);
            return 0;
        }

        return result.BytesProcessed / (result.ElapsedMilliseconds / 1000.0);
    }

    [LoggerMessage(LogLevel.Information, "Fetching SpeedTest servers...")]
    partial void LogFetchingServers();

    [LoggerMessage(LogLevel.Information, "Picking SpeedTest server...")]
    partial void LogPickingServer();

    [LoggerMessage(LogLevel.Information, "Re-selecting SpeedTest server...")]
    partial void LogReselectingServer();

    [LoggerMessage(LogLevel.Information, "Running speed test...")]
    partial void LogRunningSpeedTest();

    [LoggerMessage(LogLevel.Information, "Finished running speed test.")]
    partial void LogSpeedTestFinished();

    [LoggerMessage(
        LogLevel.Error,
        "SpeedTest library returned ElapsedMilliseconds <= 0 for the {Leg} leg — "
      + "this is unexpected; recording 0 to avoid +Infinity in metrics.")]
    partial void LogZeroElapsedMs(string leg);
}
