using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

public class SpeedTesterTests
{
    private static (SpeedTester tester, Mock<ISpeedtestProber> proberMock) Build()
    {
        var opts = new NetprobeOptions
                   {
                       Sites        = [ "google.com" ],
                       DnsResolvers = new Dictionary<string, string> { ["My_DNS_Server"] = "8.8.8.8" },
                       Speedtest    = new SpeedtestOptions { Enable = true },
                   };

        var monitorMock = new Mock<IOptionsMonitor<NetprobeOptions>>();
        monitorMock.Setup(m => m.CurrentValue).Returns(opts);

        var proberMock = new Mock<ISpeedtestProber>();
        var tester     = new SpeedTester(NullLogger<SpeedTester>.Instance, monitorMock.Object, proberMock.Object);

        return (tester, proberMock);
    }

    [Fact]
    public async Task ProberReturnsNull_RecordsUpZero()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_up", null);

        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync((SpeedtestProbeResult?)null);

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(0.0, m[0].Value);
    }

    [Fact]
    public async Task ProberReturnsResult_RecordsUpOne()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_up", null);

        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new SpeedtestProbeResult(PingMs: 20, DownloadBps: 100_000_000, UploadBps: 50_000_000));

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(1.0, m[0].Value);
    }

    [Fact]
    public async Task LatencyConversion_MillisecondsToSeconds()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_latency", null);

        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new SpeedtestProbeResult(PingMs: 1234, DownloadBps: 1_000_000, UploadBps: 500_000));

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(1.234, m[0].Value, precision: 6);
    }

    [Fact]
    public async Task DownloadConversion_BitsPerSecondToBytesPerSecond()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_download_speed", null);

        // speedtest-cli reports in bits/s; metric is in bytes/s → divide by 8
        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new SpeedtestProbeResult(PingMs: 10, DownloadBps: 80_000_000, UploadBps: 1));

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(10_000_000.0, m[0].Value, precision: 0);
    }

    [Fact]
    public async Task UploadConversion_BitsPerSecondToBytesPerSecond()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_upload_speed", null);

        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new SpeedtestProbeResult(PingMs: 10, DownloadBps: 1, UploadBps: 40_000_000));

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(5_000_000.0, m[0].Value, precision: 0);
    }
}
