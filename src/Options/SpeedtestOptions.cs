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

    /// <summary>
    /// Extra arguments appended to <c>speedtest-go --json --multi</c> on every invocation.
    /// Use this to pin a server (<c>"--server", "12345"</c>), change ping mode, etc.
    /// </summary>
    public string[] ExtraArgs { get; set; } = [];
}
