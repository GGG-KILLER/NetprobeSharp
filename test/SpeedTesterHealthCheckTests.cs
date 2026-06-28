using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NetprobeSharp.Options;
using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

public class SpeedTesterHealthCheckTests
{
    private static (SpeedTesterHealthCheck check, SpeedTester tester) Build(NetprobeOptions? opts = null)
    {
        opts ??= new NetprobeOptions
        {
            Sites        = ["google.com"],
            DnsResolvers = new Dictionary<string, string> { ["My_DNS_Server"] = "8.8.8.8" },
            Speedtest    = new SpeedtestOptions { Enable = true, TestIntervalMin = 30 },
        };
        var monitor = new Mock<IOptionsMonitor<NetprobeOptions>>();
        monitor.Setup(m => m.CurrentValue).Returns(opts);

        var tester = new SpeedTester(
            NullLogger<SpeedTester>.Instance,
            monitor.Object,
            new Mock<ISpeedtestProber>().Object);

        return (new SpeedTesterHealthCheck(tester, monitor.Object), tester);
    }

    [Fact]
    public async Task Healthy_WhenDisabled()
    {
        var opts = new NetprobeOptions
        {
            Sites        = ["google.com"],
            DnsResolvers = new Dictionary<string, string> { ["My_DNS_Server"] = "8.8.8.8" },
            Speedtest    = new SpeedtestOptions { Enable = false },
        };
        var (check, _) = Build(opts);
        var ctx    = new HealthCheckContext { Registration = new HealthCheckRegistration("speedtest", check, null, null) };
        var result = await check.CheckHealthAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Degraded_WhenEnabledButNoCycleCompleted()
    {
        var (check, _) = Build();
        var ctx    = new HealthCheckContext { Registration = new HealthCheckRegistration("speedtest", check, null, null) };
        var result = await check.CheckHealthAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Healthy_WhenLastCycleIsRecent()
    {
        var (check, tester) = Build();
        Interlocked.Exchange(ref tester._lastCycleTicks, DateTimeOffset.UtcNow.Ticks);
        var ctx    = new HealthCheckContext { Registration = new HealthCheckRegistration("speedtest", check, null, null) };
        var result = await check.CheckHealthAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Unhealthy_WhenLastCycleIsOverdue()
    {
        var opts = new NetprobeOptions
        {
            Sites        = ["google.com"],
            DnsResolvers = new Dictionary<string, string> { ["My_DNS_Server"] = "8.8.8.8" },
            Speedtest    = new SpeedtestOptions { Enable = true, TestIntervalMin = 30 },
        };
        var (check, tester) = Build(opts);
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(opts.Speedtest.TestIntervalMin * 3);
        Interlocked.Exchange(ref tester._lastCycleTicks, stale.Ticks);
        var ctx    = new HealthCheckContext { Registration = new HealthCheckRegistration("speedtest", check, null, null) };
        var result = await check.CheckHealthAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
