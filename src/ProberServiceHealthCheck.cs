using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NetprobeSharp.Options;

namespace NetprobeSharp;

internal sealed class ProberServiceHealthCheck(
    ProberService                    prober,
    IOptionsMonitor<NetprobeOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var last = prober.LastCycleCompletedAt;
        if (last is null)
            return Task.FromResult(HealthCheckResult.Degraded("No probe cycle has completed yet."));

        var deadline = TimeSpan.FromSeconds(options.CurrentValue.ProbeIntervalSec * 2);
        var elapsed  = DateTimeOffset.UtcNow - last.Value;
        return elapsed > deadline
            ? Task.FromResult(HealthCheckResult.Unhealthy(
                $"No probe cycle completed in the last {elapsed.TotalSeconds:F0}s (threshold: {deadline.TotalSeconds:F0}s)."))
            : Task.FromResult(HealthCheckResult.Healthy($"Last cycle: {last.Value:O}"));
    }
}
