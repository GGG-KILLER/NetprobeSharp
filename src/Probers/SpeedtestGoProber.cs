using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NetprobeSharp.Options;

namespace NetprobeSharp.Probers;

/// <summary>
/// Runs a speedtest probe by shelling out to <c>speedtest-go --json --multi</c> and aggregating
/// the per-server results into a single <see cref="SpeedtestProbeResult"/>.
/// </summary>
public sealed partial class SpeedtestGoProber(
    ILogger<SpeedtestGoProber>       logger,
    IOptionsMonitor<NetprobeOptions> options)
    : ISpeedtestProber
{
    /// <inheritdoc />
    public async Task<SpeedtestProbeResult?> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var extraArgs = options.CurrentValue.Speedtest.ExtraArgs;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(
                                "speedtest-go",
                                [ "--json", "--multi", ..extraArgs ])
                            {
                                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                            };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Cannot run 'speedtest-go'. Ensure it is installed and on PATH.", ex);
        }

        // Read both streams concurrently to avoid deadlocking on a full pipe buffer.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                /* already gone */
            }
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        var result = ParseOutput(stdout);
        if (result is null)
            LogCouldNotParseSpeedtestOutput(process.ExitCode, stdout.Trim(), stderr.Trim());

        return result;
    }

    /// <summary>
    /// Parses <c>speedtest-go --json</c> output and aggregates the <c>servers</c> array into a single result.
    /// Exposed as <see langword="internal"/> for unit testing without spawning a process.
    /// </summary>
    internal static SpeedtestProbeResult? ParseOutput(string json)
    {
        SpeedtestOutput output;
        try
        {
            output = JsonSerializer.Deserialize(json, JsonContext.Default.SpeedtestOutput!);
        }
        catch
        {
            return null;
        }

        if (output.Servers is not { Length: > 0 } servers)
            return null;

        // Aggregate per-server values:
        //   • throughput  = Σ (concurrent multi-connection total ≈ line capacity)
        //   • latency family = mean across servers
        //   • packet loss = single ratio from summed underlying counts (more accurate than mean-of-ratios)
        var totalDownload   = 0.0;
        var totalUpload     = 0.0;
        var totalLatencyNs  = 0L;
        var totalMinNs      = 0L;
        var totalMaxNs      = 0L;
        var totalJitterNs   = 0L;
        var lossNumerator   = 0L; // Σ (sent - dup)
        var lossDenominator = 0L; // Σ (max + 1)

        foreach (var s in servers)
        {
            totalDownload  += s.DlSpeed;
            totalUpload    += s.UlSpeed;
            totalLatencyNs += s.Latency;
            totalMinNs     += s.MinLatency;
            totalMaxNs     += s.MaxLatency;
            totalJitterNs  += s.Jitter;

            // speedtest-go uses sent=0 as a sentinel for "no measurement data".
            if (s.PacketLoss is { Sent: > 0 } pl)
            {
                lossNumerator   += pl.Sent - pl.Dup;
                lossDenominator += pl.Max  + 1;
            }
        }

        int count = servers.Length;
        double? packetLossRatio = lossDenominator > 0
                                      ? 1.0 - (double)lossNumerator / lossDenominator
                                      : null;

        return new SpeedtestProbeResult(
            Latency: NsToTimeSpan(totalLatencyNs / count),
            MinLatency: NsToTimeSpan(totalMinNs  / count),
            MaxLatency: NsToTimeSpan(totalMaxNs  / count),
            Jitter: NsToTimeSpan(totalJitterNs   / count),
            DownloadBytesPerSecond: totalDownload,
            UploadBytesPerSecond: totalUpload,
            PacketLossRatio: packetLossRatio);
    }

    /// <summary>Converts Go <c>time.Duration</c> nanoseconds (int64) to a <see cref="TimeSpan"/>.</summary>
    private static TimeSpan NsToTimeSpan(long nanoseconds)
        => TimeSpan.FromMicroseconds(nanoseconds / 1000.0); // 1ns = 0.0001us

    // ── JSON DTOs ─────────────────────────────────────────────────────────────────────────────

    private record struct SpeedtestOutput(ServerDto[]? Servers);

    private record struct ServerDto(
        long           Latency,
        long           MinLatency,
        long           MaxLatency,
        long           Jitter,
        double         DlSpeed,
        double         UlSpeed,
        PacketLossDto? PacketLoss);

    private record struct PacketLossDto(int Sent, int Dup, int Max);

    // JsonSerializerDefaults.Web adds PropertyNameCaseInsensitive + NumberHandling.AllowReadingFromString.
    // SnakeCaseLower maps C# PascalCase to the snake_case JSON keys (dl_speed, min_latency, …).
    // top-level fields user_info and timestamp are ignored automatically.
    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Web,
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
    [JsonSerializable(typeof(SpeedtestOutput))]
    private sealed partial class JsonContext : JsonSerializerContext;

    [LoggerMessage(
        LogLevel.Error,
        "Could not parse 'speedtest-go' output. Exit code {exitCode}.\nstdout: {Stdout}\nstderr: {Stderr}")]
    partial void LogCouldNotParseSpeedtestOutput(int exitCode, string stdout, string stderr);
}
