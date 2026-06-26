using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

public class ProberServiceTests
{
    private static NetprobeOptions DefaultOptions() => new()
    {
        Sites        = ["google.com"],
        DnsResolvers = new Dictionary<string, string>
        {
            ["My_DNS_Server"] = "8.8.8.8",
            ["Other_DNS"]     = "1.1.1.1",
        },
    };

    private static (ProberService svc, Mock<IPingProber> pingMock, Mock<IDnsProber> dnsMock)
        Build(NetprobeOptions? opts = null)
    {
        opts ??= DefaultOptions();

        var monitorMock = new Mock<IOptionsMonitor<NetprobeOptions>>();
        monitorMock.Setup(m => m.CurrentValue).Returns(opts);

        var pingMock = new Mock<IPingProber>();
        var dnsMock  = new Mock<IDnsProber>();

        var svc = new ProberService(
            NullLogger<ProberService>.Instance,
            monitorMock.Object,
            pingMock.Object,
            dnsMock.Object,
            null!); // MeterProvider is not stored; null is safe in unit tests.

        return (svc, pingMock, dnsMock);
    }

    private static void SetupPing(Mock<IPingProber> pingMock, double? loss = 0, double? jitter = 1, IReadOnlyList<double>? rtts = null) =>
        pingMock.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string site, int _, int _, int _, CancellationToken _) =>
                    new PingProbeResult(site, loss, jitter, rtts ?? [10.0]));

    private static void SetupDns(Mock<IDnsProber> dnsMock, double? latency = 10) =>
        dnsMock.Setup(d => d.ProbeAsync(It.IsAny<DnsResolver>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((DnsResolver r, string _, int _, CancellationToken _) => new DnsProbeResult(r, latency));

    [Fact]
    public async Task PingUp_IsOne_WhenLossPresent()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_ping_up", null);

        SetupPing(pingMock, loss: 0, jitter: 5, rtts: [20.0]);
        SetupDns(dnsMock);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(1.0, m[0].Value);
    }

    [Fact]
    public async Task PingUp_IsZero_WhenLossIsNull()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_ping_up", null);

        SetupPing(pingMock, loss: null, jitter: null, rtts: []);
        SetupDns(dnsMock, latency: null);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(0.0, m[0].Value);
    }

    [Fact]
    public async Task PacketLossRatio_IsFractionOf100()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_ping_packet_loss", null);

        SetupPing(pingMock, loss: 20, jitter: 2, rtts: [10.0]);
        SetupDns(dnsMock);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(0.20, m[0].Value, precision: 6);
    }

    [Fact]
    public async Task PacketLossRatio_IsOne_WhenLossNull()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_ping_packet_loss", null);

        SetupPing(pingMock, loss: null, jitter: null, rtts: []);
        SetupDns(dnsMock, latency: null);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(1.0, m[0].Value);
    }

    [Fact]
    public async Task RttHistogram_ConvertsMillisecondsToSeconds()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_ping_rtt", null);

        SetupPing(pingMock, loss: 0, jitter: 1, rtts: [50.0, 100.0]);
        SetupDns(dnsMock);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Equal(2, m.Count);
        Assert.Contains(m, x => Math.Abs(x.Value - 0.05) < 1e-9);
        Assert.Contains(m, x => Math.Abs(x.Value - 0.10) < 1e-9);
    }

    [Fact]
    public async Task JitterGauge_ConvertsMillisecondsToSeconds()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_ping_jitter", null);

        SetupPing(pingMock, loss: 0, jitter: 30, rtts: [20.0]);
        SetupDns(dnsMock);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.Equal(0.030, m[0].Value, precision: 6);
    }

    [Fact]
    public async Task DnsUp_IsOne_WhenLatencyPresent()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_dns_up", null);

        SetupPing(pingMock);
        SetupDns(dnsMock, latency: 40);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.All(m, x => Assert.Equal(1.0, x.Value));
    }

    [Fact]
    public async Task DnsUp_IsZero_WhenLatencyNull()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_dns_up", null);

        SetupPing(pingMock);
        SetupDns(dnsMock, latency: null);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.All(m, x => Assert.Equal(0.0, x.Value));
    }

    [Fact]
    public async Task HealthScore_IsRecordedOncePerCycle()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_health_score", null);

        SetupPing(pingMock, loss: 0, jitter: 0, rtts: [10.0]);
        SetupDns(dnsMock, latency: 10);

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);

        var m = collector.GetMeasurementSnapshot();
        Assert.Single(m);
        Assert.InRange(m[0].Value, 0.0, 1.0);
    }

    [Fact]
    public async Task HealthScore_UsesMyDnsServer_Latency()
    {
        var (svc, pingMock, dnsMock) = Build();
        using var collector = new MetricCollector<double>(svc._meter, "netprobe_health_score", null);

        SetupPing(pingMock, loss: 0, jitter: 0, rtts: [1.0]);

        // My_DNS_Server has very high latency — should drag the score down.
        dnsMock.Setup(d => d.ProbeAsync(
                    It.Is<DnsResolver>(r => r.Name == "My_DNS_Server"),
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((DnsResolver r, string _, int _, CancellationToken _) =>
                   new DnsProbeResult(r, 10_000));

        dnsMock.Setup(d => d.ProbeAsync(
                    It.Is<DnsResolver>(r => r.Name != "My_DNS_Server"),
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((DnsResolver r, string _, int _, CancellationToken _) =>
                   new DnsProbeResult(r, 1));

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);
        var scorePoor = collector.GetMeasurementSnapshot(clear: true)[0].Value;

        // Now flip: My_DNS_Server is fast. Score should be better.
        dnsMock.Setup(d => d.ProbeAsync(
                    It.Is<DnsResolver>(r => r.Name == "My_DNS_Server"),
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((DnsResolver r, string _, int _, CancellationToken _) =>
                   new DnsProbeResult(r, 1));

        dnsMock.Setup(d => d.ProbeAsync(
                    It.Is<DnsResolver>(r => r.Name != "My_DNS_Server"),
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((DnsResolver r, string _, int _, CancellationToken _) =>
                   new DnsProbeResult(r, 10_000));

        await svc.RunProbeCycleAsync(DefaultOptions(), CancellationToken.None);
        var scoreGood = collector.GetMeasurementSnapshot()[0].Value;

        Assert.True(scoreGood > scorePoor,
            $"Expected better score when My_DNS_Server is fast ({scoreGood}) vs slow ({scorePoor}).");
    }
}
