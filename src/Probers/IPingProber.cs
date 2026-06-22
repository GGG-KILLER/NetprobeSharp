namespace NetprobeSharp.Probers;

public record PingProbeResult(string Site, double Latency, double Loss, double Jitter);

public interface IPingProber
{
    /// <summary>
    /// Pings <paramref name="site"/> <paramref name="count"/> times and returns aggregate stats.
    /// </summary>
    /// <remarks>
    /// Latency = mean RTT, Jitter = mdev (population stddev, as iputils reports it),
    /// Loss = percentage of probes with no reply. On 100% loss, Latency and Jitter are
    /// NaN and Loss is 100 (the outage is recorded, not dropped). Throws
    /// <see cref="InvalidOperationException"/> if <c>ping</c> can't be run or its output
    /// can't be parsed (e.g. unknown host).
    /// </remarks>
    Task<PingProbeResult> ProbeAsync(
        string            site,
        int               count,
        int               timeoutMs         = 1000,
        int               intervalMs        = 100,
        CancellationToken cancellationToken = default);
}
