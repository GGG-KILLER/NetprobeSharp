using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

public class ProberServiceHealthCheckTests
{
    private static readonly NetprobeOptions DefaultOptions = new()
    {
        ProbeIntervalSec = 60,
        Sites            = ["google.com"],
        DnsResolvers     = new Dictionary<string, string> { ["My_DNS_Server"] = "8.8.8.8" },
    };

    private static (ProberServiceHealthCheck check, ProberService prober) Build(NetprobeOptions? opts = null)
    {
        opts ??= DefaultOptions;
        var monitor = new Mock<IOptionsMonitor<NetprobeOptions>>();
        monitor.Setup(m => m.CurrentValue).Returns(opts);

        var prober = new ProberService(
            NullLogger<ProberService>.Instance,
            monitor.Object,
            new Mock<IPingProber>().Object,
            new Mock<IDnsProber>().Object,
            null!);

        return (new ProberServiceHealthCheck(prober, monitor.Object), prober);
    }

    [Fact]
    public async Task Degraded_WhenNoCycleHasCompleted()
    {
        var (check, _) = Build();
        var ctx    = new HealthCheckContext { Registration = new HealthCheckRegistration("prober", check, null, null) };
        var result = await check.CheckHealthAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Healthy_WhenLastCycleIsRecent()
    {
        var (check, prober) = Build();
        Interlocked.Exchange(ref prober._lastCycleTicks, DateTimeOffset.UtcNow.Ticks);
        var ctx    = new HealthCheckContext { Registration = new HealthCheckRegistration("prober", check, null, null) };
        var result = await check.CheckHealthAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Unhealthy_WhenLastCycleIsOverdue()
    {
        var opts = new NetprobeOptions
        {
            ProbeIntervalSec = 60,
            Sites            = ["google.com"],
            DnsResolvers     = new Dictionary<string, string> { ["My_DNS_Server"] = "8.8.8.8" },
        };
        var (check, prober) = Build(opts);
        // 3× interval ago — well past the 2× deadline
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(opts.ProbeIntervalSec * 3);
        Interlocked.Exchange(ref prober._lastCycleTicks, stale.Ticks);
        var ctx    = new HealthCheckContext { Registration = new HealthCheckRegistration("prober", check, null, null) };
        var result = await check.CheckHealthAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
