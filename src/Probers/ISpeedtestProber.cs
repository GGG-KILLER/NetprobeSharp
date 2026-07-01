namespace NetprobeSharp.Probers;

/// <summary>
/// Aggregated results of the speed test (averaged/summed across all <c>--multi</c> servers).
/// </summary>
/// <param name="Latency">Average latency across all servers.</param>
/// <param name="MinLatency">Average minimum latency across all servers.</param>
/// <param name="MaxLatency">Average maximum latency across all servers.</param>
/// <param name="Jitter">Average jitter across all servers.</param>
/// <param name="DownloadBytesPerSecond">Total download throughput in bytes per second (summed across servers).</param>
/// <param name="UploadBytesPerSecond">Total upload throughput in bytes per second (summed across servers).</param>
/// <param name="PacketLossRatio">
/// Packet loss ratio (0–1), computed from the summed sent/dup/max counts across all servers that reported
/// loss data (<c>sent &gt; 0</c>). <see langword="null"/> when all servers returned <c>sent = 0</c>
/// (speedtest-go's sentinel for "no measurement").
/// </param>
public record SpeedtestProbeResult(
    TimeSpan  Latency,
    TimeSpan  MinLatency,
    TimeSpan  MaxLatency,
    TimeSpan  Jitter,
    double    DownloadBytesPerSecond,
    double    UploadBytesPerSecond,
    double?   PacketLossRatio);

public interface ISpeedtestProber
{
    Task<SpeedtestProbeResult?> ProbeAsync(CancellationToken cancellationToken = default);
}
