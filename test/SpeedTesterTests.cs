using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

public class SpeedTesterTests
{
    private static SpeedtestProbeResult DefaultResult(
        TimeSpan?  latency                 = null,
        TimeSpan?  minLatency              = null,
        TimeSpan?  maxLatency              = null,
        TimeSpan?  jitter                  = null,
        double     downloadBytesPerSecond  = 100_000_000,
        double     uploadBytesPerSecond    = 50_000_000,
        double?    packetLossRatio         = null)
        => new(
            Latency:               latency    ?? TimeSpan.FromMilliseconds(20),
            MinLatency:            minLatency ?? TimeSpan.FromMilliseconds(18),
            MaxLatency:            maxLatency ?? TimeSpan.FromMilliseconds(25),
            Jitter:                jitter     ?? TimeSpan.FromMilliseconds(2),
            DownloadBytesPerSecond: downloadBytesPerSecond,
            UploadBytesPerSecond:   uploadBytesPerSecond,
            PacketLossRatio:        packetLossRatio);

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
                  .ReturnsAsync(DefaultResult());

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(1.0, m[0].Value);
    }

    [Fact]
    public async Task LatencyRecordedAsTotalSeconds()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_latency", null);

        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(DefaultResult(latency: TimeSpan.FromMilliseconds(1234)));

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(1.234, m[0].Value, precision: 6);
    }

    [Fact]
    public async Task DownloadSpeed_RecordedDirectly_NoDivisionByEight()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_download_speed", null);

        // speedtest-go reports in bytes/s; metric is in bytes/s — recorded as-is (no /8)
        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(DefaultResult(downloadBytesPerSecond: 80_000_000));

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(80_000_000.0, m[0].Value, precision: 0);
    }

    [Fact]
    public async Task UploadSpeed_RecordedDirectly_NoDivisionByEight()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_upload_speed", null);

        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(DefaultResult(uploadBytesPerSecond: 40_000_000));

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(40_000_000.0, m[0].Value, precision: 0);
    }

    [Fact]
    public async Task PacketLoss_NullRatio_NotRecorded()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_packet_loss_ratio", null);

        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(DefaultResult(packetLossRatio: null));

        await tester.RunCycleAsync(CancellationToken.None);

        Assert.Empty(collector.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task PacketLoss_SetRatio_Recorded()
    {
        var (tester, proberMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_packet_loss_ratio", null);

        proberMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(DefaultResult(packetLossRatio: 0.05));

        await tester.RunCycleAsync(CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(0.05, m[0].Value, precision: 6);
    }
}
