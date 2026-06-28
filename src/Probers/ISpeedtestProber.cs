namespace NetprobeSharp.Probers;

/// <summary>
/// Results of the speed test.
/// </summary>
/// <param name="PingMs">Ping latency in milliseconds.</param>
/// <param name="DownloadBps">Download speed in bits per second.</param>
/// <param name="UploadBps">Upload speed in bits per second.</param>
public record SpeedtestProbeResult(
    double PingMs,
    double DownloadBps,
    double UploadBps);

public interface ISpeedtestProber
{
    Task<SpeedtestProbeResult?> ProbeAsync(CancellationToken cancellationToken = default);
}
