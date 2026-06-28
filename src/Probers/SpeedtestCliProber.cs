using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetprobeSharp.Probers;

/// <summary>
/// Runs a speedtest probe by shelling out to the CLI tool speedtest-cli.
/// </summary>
/// <param name="logger"></param>
public sealed partial class SpeedtestCliProber(ILogger<SpeedtestCliProber> logger) : ISpeedtestProber
{
    /// <inheritdoc />
    public async Task<SpeedtestProbeResult?> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("speedtest", [ "--json" ])
                            {
                                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                            };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Cannot run 'speedtest'. Ensure it is installed and on PATH.", ex);
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

        SpeedtestStdout parsedStdout;
        try
        {
            parsedStdout = JsonSerializer.Deserialize(stdout, JsonContext.Default.SpeedtestStdout!);
        }
        catch (Exception ex)
        {
            LogCouldNotParseSpeedtestOutput(ex, process.ExitCode, stdout.Trim(), stderr.Trim());
            return null;
        }

        return new SpeedtestProbeResult(parsedStdout.Ping, parsedStdout.Download, parsedStdout.Upload);
    }

    private readonly record struct SpeedtestStdout(
        [property: JsonRequired] double Download,
        [property: JsonRequired] double Upload,
        [property: JsonRequired] double Ping);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(SpeedtestStdout))]
    private sealed partial class JsonContext : JsonSerializerContext;

    [LoggerMessage(
        LogLevel.Error,
        "Could not parse 'speedtest' output. Exit code {exitCode}.\nstdout: {Stdout}\nstderr: {Stderr}")]
    partial void LogCouldNotParseSpeedtestOutput(Exception exception, int exitCode, string stdout, string stderr);
}
