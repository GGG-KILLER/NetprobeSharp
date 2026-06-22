namespace NetprobeSharp.Probers;

public record PingProbeResult(string Site, double? Latency, double? Loss, double? Jitter);

public interface IPingProber
{
    /// <summary>
    /// Pings <paramref name="site"/> <paramref name="count"/> times and returns aggregate stats.
    /// </summary>
    /// <remarks>
    /// Latency = mean RTT, Jitter = mdev (population stddev, as iputils reports it),
    /// Loss = percentage of probes with no reply. A field is <see langword="null"/> when its
    /// figure isn't available: Latency/Jitter on 100% loss or unparseable RTT, and all three
    /// when <c>ping</c> produced no recognizable summary at all. The caller decides how to
    /// treat a missing value. Throws <see cref="InvalidOperationException"/> only if
    /// <c>ping</c> cannot be started.
    /// </remarks>
    Task<PingProbeResult> ProbeAsync(
        string            site,
        int               count,
        int               timeoutMs         = 1000,
        int               intervalMs        = 100,
        CancellationToken cancellationToken = default);
}
