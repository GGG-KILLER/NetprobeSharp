namespace NetprobeSharp.Options;

public sealed class SpeedtestOptions
{
    /// <summary>
    /// Whether the speed testing module is enabled.
    /// </summary>
    public bool Enable { get; set; } = false;

    /// <summary>
    /// The interval between speed tests (in minutes).
    /// </summary>
    public int TestIntervalMin { get; set; } = 10;
}
