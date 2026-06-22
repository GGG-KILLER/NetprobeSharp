namespace NetprobeSharp.Probers;

public record PingProbeResult(string Site, double Latency, double Loss, double Jitter);

public interface IPingProber
{
    /// <summary>
    /// Pings <paramref name="site"/> <paramref name="count"/> times and returns aggregate stats.
    /// </summary>
    /// <remarks>
    /// Latency = mean RTT, Jitter = mdev (population stddev, as iputils reports it),
    /// Loss = percentage of probes with no reply. When there is no usable RTT data (100%
    /// loss or unparseable output), Latency and Jitter are reported at the configured
    /// <c>LatencyThreshold</c>/<c>JitterThreshold</c> and Loss is the parsed packet-loss %
    /// (or <c>LossThreshold</c> if even that can't be parsed) -- the outage is recorded,
    /// not dropped. Throws <see cref="InvalidOperationException"/> only if <c>ping</c>
    /// cannot be started.
    /// </remarks>
    Task<PingProbeResult> ProbeAsync(
        string            site,
        int               count,
        int               timeoutMs         = 1000,
        int               intervalMs        = 100,
        CancellationToken cancellationToken = default);
}
