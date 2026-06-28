using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;

namespace NetprobeSharp;

public sealed partial class SpeedTester : BackgroundService
{
    public static readonly string MeterName = "NetprobeSharp.Speedtest";

    private readonly  ILogger<SpeedTester>             _logger;
    private readonly  IOptionsMonitor<NetprobeOptions> _options;
    private readonly  ISpeedtestProber                 _speedtestProber;
    internal readonly Meter                            _meter;

    // Tracks when the last successful speed-test cycle completed — read by SpeedTesterHealthCheck.
    // Zero means "never completed". Written via Interlocked to avoid torn reads on 32-bit.
    internal long _lastCycleTicks;

    internal DateTimeOffset? LastCycleCompletedAt
        => _lastCycleTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _lastCycleTicks), TimeSpan.Zero);

    // Gauges — all unlabeled so each metric is always a single time series in Prometheus.
    private readonly Gauge<double> _latency;
    private readonly Gauge<double> _uploadSpeed;
    private readonly Gauge<double> _downloadSpeed;
    private readonly Gauge<double> _speedTestUp;

    /// <inheritdoc />
    public SpeedTester(
        ILogger<SpeedTester>             logger,
        IOptionsMonitor<NetprobeOptions> options,
        ISpeedtestProber                 speedtestProber)
    {
        _logger          = logger;
        _options         = options;
        _speedtestProber = speedtestProber;
        _meter           = new Meter(MeterName);
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
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.CurrentValue.Speedtest.TestIntervalMin));

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

                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _speedTestUp.Record(0);
                _logger.LogError(ex, "Error testing speed against server.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Runs one measurement cycle (latency, download, upload) and records the results. Extracted for unit testability.
    /// </summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        LogRunningSpeedTest();

        var result = await _speedtestProber.ProbeAsync(ct);

        if (result != null)
        {
            _latency.Record(result.PingMs            / 1000.0);
            _downloadSpeed.Record(result.DownloadBps / 8.0);
            _uploadSpeed.Record(result.UploadBps     / 8.0);
            _speedTestUp.Record(1);
        }
        else
        {
            _speedTestUp.Record(0);
        }

        Interlocked.Exchange(ref _lastCycleTicks, DateTimeOffset.UtcNow.Ticks);

        LogSpeedTestFinished();
    }

    [LoggerMessage(LogLevel.Information, "Running speed test...")]
    partial void LogRunningSpeedTest();

    [LoggerMessage(LogLevel.Information, "Finished running speed test.")]
    partial void LogSpeedTestFinished();
}
