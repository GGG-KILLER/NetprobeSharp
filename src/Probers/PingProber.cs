using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetprobeSharp.Options;

namespace NetprobeSharp.Probers;

public sealed partial class PingProber(ILogger<PingProber> logger, IOptionsMonitor<NetprobeOptions> optionsMonitor)
    : IPingProber
{
    // iputils summary lines (the same format for IPv4 and IPv6):
    //   "4 packets transmitted, 4 received, 0% packet loss, time 3050ms"
    //   "rtt min/avg/max/mdev = 0.041/0.050/0.062/0.008 ms"
    [GeneratedRegex(@"(?<loss>[\d.]+)%\s+packet loss")]
    private static partial Regex LossRegex { get; }

    // Anchor on the shared "min/avg/max/..." label so it's clear which number is which,
    // while tolerating the dialects that vary around it: iputils prefixes "rtt" and ends in
    // "mdev"; BSD/macOS prefix "round-trip" and end in "stddev".
    [GeneratedRegex(@"min/avg/max/\w+\s*=\s*[\d.]+/(?<avg>[\d.]+)/[\d.]+/(?<mdev>[\d.]+)\s*ms")]
    private static partial Regex RttRegex { get; }

    /// <summary>
    /// Pings <paramref name="site"/> <paramref name="count"/> times and returns aggregate stats.
    /// Shells out to the system <c>ping</c>, which already handles IPv4/IPv6 selection,
    /// privileges, and RTT aggregation, and parses its summary block.
    /// </summary>
    /// <remarks>
    /// Latency = mean RTT, Jitter = mdev (population stddev, as iputils reports it),
    /// Loss = percentage of probes with no reply. On 100% loss, Latency and Jitter are
    /// <see cref="ScoreOptions.LatencyThreshold"/> and <see cref="ScoreOptions.JitterThreshold"/> and Loss is 100
    /// (the outage is recorded, not dropped). Throws <see cref="InvalidOperationException"/> if <c>ping</c> can't be run.
    /// If its output can't be parsed, we return a result that's all thresholds.
    /// </remarks>
    public async Task<PingProbeResult> ProbeAsync(
        string            site,
        int               count,
        int               timeoutMs         = 1000,
        int               intervalMs        = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var psi = new ProcessStartInfo("ping")
                  {
                      // Pass args individually (no shell) so the host can't inject anything.
                      // -n: numeric output, skip reverse-DNS of the responder.
                      // -W/-i take seconds (fractional); ping picks v4 vs v6 from the resolved address.
                      ArgumentList =
                      {
                          "-n",
                          "-c",
                          count.ToString(CultureInfo.InvariantCulture),
                          "-W",
                          (timeoutMs / 1000.0).ToString(CultureInfo.InvariantCulture),
                          "-i",
                          (intervalMs / 1000.0).ToString(CultureInfo.InvariantCulture),
                          site,
                      },
                      RedirectStandardOutput = true,
                      RedirectStandardError  = true,
                      UseShellExecute        = false,
                  };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Cannot run 'ping'. Ensure it is installed and on PATH.", ex);
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

        // Exit code 1 just means "no replies" (total loss), which is a valid result, not an
        // error. We key off the statistics block instead: if there's no loss line, ping
        // failed for real (unknown host, bad option, ...).
        Match lossMatch = LossRegex.Match(stdout);
        if (!lossMatch.Success)
        {
            logger.LogError(
                "Could not parse 'ping' output for '{Site}'. Exit code {ExitCode}.\n"
              + "stdout: {Stdout}\nstderr: {Stderr}",
                site,
                process.ExitCode,
                stdout.Trim(),
                stderr.Trim());
            return new PingProbeResult(
                site,
                optionsMonitor.CurrentValue.Score.LatencyThreshold,
                optionsMonitor.CurrentValue.Score.LossThreshold,
                optionsMonitor.CurrentValue.Score.JitterThreshold);
        }
        var loss = double.Parse(lossMatch.Groups["loss"].Value, CultureInfo.InvariantCulture);

        Match rtt = RttRegex.Match(stdout);
        if (!rtt.Success)
        {
            return new PingProbeResult(
                site,
                optionsMonitor.CurrentValue.Score.LatencyThreshold,
                loss,
                optionsMonitor.CurrentValue.Score.JitterThreshold);
        }

        var avg  = double.Parse(rtt.Groups["avg"].Value,  CultureInfo.InvariantCulture);
        var mdev = double.Parse(rtt.Groups["mdev"].Value, CultureInfo.InvariantCulture);

        return new PingProbeResult(site, avg, loss, mdev);
    }
}
