using NetprobeSharp.Options;

namespace NetprobeSharp.Tests;

public class HealthScoreTests
{
    [Fact]
    public void Compute_AllZero_IsPerfectScore()
    {
        var score = new ScoreOptions();

        Assert.Equal(1.0, HealthScore.Compute(0, 0, 0, 0, score), 10);
    }

    [Fact]
    public void Compute_AllAtThreshold_IsWorstScore()
    {
        var score = new ScoreOptions();
        // Every term hits its cap, so the score bottoms out at 1 - (sum of weights).
        var expected = 1 - (score.LossWeight + score.LatencyWeight + score.JitterWeight + score.DnsWeight);

        var actual = HealthScore.Compute(
            score.LossThreshold,
            score.LatencyThreshold,
            score.JitterThreshold,
            score.DnsThreshold,
            score);

        Assert.Equal(expected, actual, 10);
    }

    [Fact]
    public void Compute_AboveThreshold_IsCappedAtWorst()
    {
        var score = new ScoreOptions();

        var atCap = HealthScore.Compute(
            score.LossThreshold, score.LatencyThreshold, score.JitterThreshold, score.DnsThreshold, score);
        var wayOver = HealthScore.Compute(
            score.LossThreshold * 10, score.LatencyThreshold * 10, score.JitterThreshold * 10, score.DnsThreshold * 10, score);

        Assert.Equal(atCap, wayOver, 10);
    }

    [Fact]
    public void Compute_HalfLossOnly_SubtractsHalfOfLossWeight()
    {
        var score = new ScoreOptions(); // defaults: LossThreshold 5, LossWeight 0.60

        // 2.5 / 5 = 0.5 of the loss cap, so penalty = 0.5 * 0.60 = 0.30 -> score 0.70.
        var actual = HealthScore.Compute(2.5, 0, 0, 0, score);

        Assert.Equal(0.70, actual, 10);
    }
}