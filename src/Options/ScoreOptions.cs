namespace NetprobeSharp.Options;

public sealed class ScoreOptions
{
    public double LossThreshold { get; set; } = 5;   // 5% loss threshold as max
    public double LossWeight    { get; set; } = .60; // Loss is 60% of score

    public double LatencyThreshold { get; set; } = 100; // 100ms latency threshold as max
    public double LatencyWeight    { get; set; } = .15; // Latency is 15% of score

    public double JitterThreshold { get; set; } = 30;  // 30ms jitter threshold as max
    public double JitterWeight    { get; set; } = .20; // Jitter is 20% of score

    public double DnsThreshold { get; set; } = 100; // 100ms dns latency threshold as max
    public double DnsWeight    { get; set; } = .05; // DNS latency is 0.05 of score
}
