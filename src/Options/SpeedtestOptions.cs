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
    /// The size of the download payload (in MB). When <see langword="null"/>, the library default is used.
    /// </summary>
    public int DownloadSizeMb { get; set; } = 1024;

    /// <summary>
    /// The size of the upload payload (in MB). When <see langword="null"/>, the library default is used.
    /// </summary>
    public int UploadSizeMb { get; set; } = 256;

    /// <summary>
    /// How often to re-select the fastest SpeedTest server by latency (in minutes).
    /// When <see langword="null"/>, the server is selected once at startup and never re-selected.
    /// </summary>
    public int? ServerReselectionIntervalMin { get; set; } = null;
}
