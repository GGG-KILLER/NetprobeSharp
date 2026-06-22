using NetprobeSharp.Options;

namespace NetprobeSharp;

/// <summary>Computes the internet health score (0..1) from probe aggregates.</summary>
internal static class HealthScore
{
    /// <summary>
    /// 1.0 minus the weighted, threshold-capped penalty for loss, latency, jitter and DNS
    /// latency. Each term is normalized against its threshold and capped at its weight, so
    /// the worst possible score is <c>1 - (LossWeight + LatencyWeight + JitterWeight + DnsWeight)</c>.
    /// </summary>
    public static double Compute(
        double       avgLoss,
        double       avgLatency,
        double       avgJitter,
        double       dnsLatency,
        ScoreOptions score)
    {
        var loss    = score.LossWeight    * Math.Min(1.0, avgLoss    / score.LossThreshold);
        var latency = score.LatencyWeight * Math.Min(1.0, avgLatency / score.LatencyThreshold);
        var jitter  = score.JitterWeight  * Math.Min(1.0, avgJitter  / score.JitterThreshold);
        var dns     = score.DnsWeight     * Math.Min(1.0, dnsLatency / score.DnsThreshold);
        return 1 - loss - latency - jitter - dns;
    }
}
