namespace NetprobeSharp.Probers;

/// <summary>
/// Aggregate statistics from a single probe run to one site.
/// </summary>
/// <param name="Site">The hostname or IP that was probed.</param>
/// <param name="Loss">Packet loss percentage (0–100), or <see langword="null"/> when
/// <c>ping</c> produced no recognizable summary.</param>
/// <param name="Jitter">Population stddev of RTT in ms (mdev), or <see langword="null"/>
/// on 100 % loss or when the RTT summary line is absent.</param>
/// <param name="Rtts">Per-reply round-trip times in ms, in arrival order. Empty when every
/// packet was lost or <c>ping</c> produced no output at all.</param>
public record PingProbeResult(
    string                 Site,
    double?                Loss,
    double?                Jitter,
    IReadOnlyList<double>  Rtts);

public interface IPingProber
{
    /// <summary>
    /// Pings <paramref name="site"/> <paramref name="count"/> times and returns aggregate stats.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jitter = mdev (population stddev, as iputils reports it), Loss = percentage of probes
    /// with no reply, Rtts = per-reply round-trip times in ms.
    /// </para>
    /// <para>
    /// <see cref="PingProbeResult.Loss"/> is <see langword="null"/> only when <c>ping</c>
    /// produced no recognizable summary (unknown host, bad option, etc.).
    /// <see cref="PingProbeResult.Jitter"/> is <see langword="null"/> on 100 % loss.
    /// <see cref="PingProbeResult.Rtts"/> is empty on total loss or parse failure.
    /// The caller decides how to treat missing values.
    /// </para>
    /// <para>
    /// Throws <see cref="InvalidOperationException"/> only if <c>ping</c> cannot be started.
    /// </para>
    /// </remarks>
    Task<PingProbeResult> ProbeAsync(
        string            site,
        int               count,
        int               timeoutMs         = 1000,
        int               spacingMs         = 100,
        CancellationToken cancellationToken = default);
}
