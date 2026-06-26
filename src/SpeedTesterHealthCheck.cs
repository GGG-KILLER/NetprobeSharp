using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NetprobeSharp.Options;

namespace NetprobeSharp;

internal sealed class SpeedTesterHealthCheck(
    SpeedTester                      speedTester,
    IOptionsMonitor<NetprobeOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var opts = options.CurrentValue;

        // When disabled, the SpeedTester deliberately skips cycles — not a fault.
        if (!opts.Speedtest.Enable)
            return Task.FromResult(HealthCheckResult.Healthy("SpeedTest is disabled."));

        var last = speedTester.LastCycleCompletedAt;
        if (last is null)
            return Task.FromResult(HealthCheckResult.Degraded("No SpeedTest cycle has completed yet."));

        var deadline = TimeSpan.FromMinutes(opts.Speedtest.TestIntervalMin * 2);
        var elapsed  = DateTimeOffset.UtcNow - last.Value;
        return elapsed > deadline
            ? Task.FromResult(HealthCheckResult.Unhealthy(
                $"No SpeedTest cycle completed in the last {elapsed.TotalMinutes:F0}m (threshold: {deadline.TotalMinutes:F0}m)."))
            : Task.FromResult(HealthCheckResult.Healthy($"Last cycle: {last.Value:O}"));
    }
}
