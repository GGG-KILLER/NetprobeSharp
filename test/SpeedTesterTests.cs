using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NetPace.Core;
using NetprobeSharp.Options;

namespace NetprobeSharp.Tests;

public class SpeedTesterTests
{
    private static IServer MakeServer(string sponsor = "Acme ISP", string location = "London")
    {
        var mock = new Mock<IServer>();
        mock.Setup(s => s.Sponsor).Returns(sponsor);
        mock.Setup(s => s.Location).Returns(location);
        return mock.Object;
    }

    private static SpeedTestResult MakeSpeed(long bytes, long elapsedMs) =>
        new() { BytesProcessed = bytes, ElapsedMilliseconds = elapsedMs };

    private static LatencyTestResult MakeLatency(long ms, IServer server) =>
        new() { LatencyMilliseconds = ms, Server = server };

    private static (SpeedTester tester, Mock<ISpeedTestService> svcMock) Build()
    {
        var opts = new NetprobeOptions
        {
            Sites        = ["google.com"],
            DnsResolvers = new Dictionary<string, string> { ["My_DNS_Server"] = "8.8.8.8" },
            Speedtest    = new SpeedtestOptions { Enable = true },
        };

        var monitorMock = new Mock<IOptionsMonitor<NetprobeOptions>>();
        monitorMock.Setup(m => m.CurrentValue).Returns(opts);

        var svcMock = new Mock<ISpeedTestService>();
        var tester  = new SpeedTester(NullLogger<SpeedTester>.Instance, monitorMock.Object, svcMock.Object);

        return (tester, svcMock);
    }

    [Fact]
    public async Task LatencyConversion_MillisecondsToSeconds()
    {
        var server = MakeServer();
        var (tester, svcMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_latency", null);

        svcMock.Setup(s => s.GetServerLatencyAsync(server, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeLatency(1234, server));
        svcMock.Setup(s => s.GetDownloadSpeedAsync(server, It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeSpeed(1_000_000, 1000));
        svcMock.Setup(s => s.GetUploadSpeedAsync(server, It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeSpeed(500_000, 1000));

        await tester.RunCycleAsync(server, new SpeedtestOptions { Enable = true }, CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(1.234, m[0].Value, precision: 6);
    }

    [Fact]
    public async Task DivideByZero_ElapsedZero_RecordsZeroForDownload()
    {
        var server = MakeServer();
        var (tester, svcMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_download_speed", null);

        svcMock.Setup(s => s.GetServerLatencyAsync(server, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeLatency(100, server));
        svcMock.Setup(s => s.GetDownloadSpeedAsync(server, It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeSpeed(1_000_000, 0)); // zero elapsed — the bug case
        svcMock.Setup(s => s.GetUploadSpeedAsync(server, It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeSpeed(500_000, 1000));

        await tester.RunCycleAsync(server, new SpeedtestOptions { Enable = true }, CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(0.0, m[0].Value);
    }

    [Fact]
    public async Task ServiceThrows_NoUpOneRecorded()
    {
        var server = MakeServer();
        var (tester, svcMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_up", null);

        svcMock.Setup(s => s.GetServerLatencyAsync(server, It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("network failure"));

        // RunCycleAsync propagates the exception; ExecuteAsync catches it and records up=0.
        // Assert that no up=1 is emitted when the cycle throws.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tester.RunCycleAsync(server, new SpeedtestOptions { Enable = true }, CancellationToken.None));

        var m = collector.GetMeasurementSnapshot();
        Assert.DoesNotContain(m, x => x.Value == 1);
    }

    [Fact]
    public async Task ServerInfo_RecordsCorrectSponsorAndLocationLabels()
    {
        // The numeric gauges are unlabeled; server identity lives in netprobe_speedtest_server_info.
        var server = MakeServer("My ISP", "Paris");
        var (tester, svcMock) = Build();
        using var infoCollector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_server_info", null);
        using var upCollector   = new MetricCollector<double>(tester._meter, "netprobe_speedtest_up", null);

        svcMock.Setup(s => s.GetServerLatencyAsync(server, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeLatency(50, server));
        svcMock.Setup(s => s.GetDownloadSpeedAsync(server, It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeSpeed(1_000_000, 1000));
        svcMock.Setup(s => s.GetUploadSpeedAsync(server, It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeSpeed(500_000, 1000));

        // Simulate server selection (normally done in ExecuteAsync before RunCycleAsync).
        tester.RecordServerInfo(server);
        await tester.RunCycleAsync(server, new SpeedtestOptions { Enable = true }, CancellationToken.None);

        var info = infoCollector.GetMeasurementSnapshot();
        Assert.Single(info);
        Assert.Equal(1.0, info[0].Value);
        Assert.True(info[0].ContainsTags(
            new KeyValuePair<string, object?>("sponsor",  "My ISP"),
            new KeyValuePair<string, object?>("location", "Paris")));

        // Numeric metric has no server label.
        var up = upCollector.GetMeasurementSnapshot();
        Assert.Single(up);
        Assert.False(up[0].ContainsTags("sponsor", "location"));
    }

    [Fact]
    public async Task UploadConversion_BytesPerSecond()
    {
        var server = MakeServer();
        var (tester, svcMock) = Build();
        using var collector = new MetricCollector<double>(tester._meter, "netprobe_speedtest_upload_speed", null);

        svcMock.Setup(s => s.GetServerLatencyAsync(server, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeLatency(50, server));
        svcMock.Setup(s => s.GetDownloadSpeedAsync(server, It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeSpeed(2_000_000, 2000));
        svcMock.Setup(s => s.GetUploadSpeedAsync(server, It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeSpeed(500_000, 500)); // 1 000 000 B/s

        await tester.RunCycleAsync(server, new SpeedtestOptions { Enable = true }, CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(1_000_000.0, m[0].Value, precision: 0);
    }
}
