using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NetprobeSharp.Probers;

public sealed partial class PingProber(ILogger<PingProber> logger)
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
    [GeneratedRegex(@"min/avg/max/\w+\s*=\s*[\d.]+/[\d.]+/[\d.]+/(?<mdev>[\d.]+)\s*ms")]
    private static partial Regex RttSummaryRegex { get; }

    // Per-reply lines emitted by both iputils and BSD ping:
    //   iputils: "64 bytes from 8.8.8.8: icmp_seq=1 ttl=118 time=8.12 ms"
    //   BSD:     "64 bytes from 8.8.8.8: icmp_seq=1 ttl=118 time=8.123 ms"
    //   Both also use "<" instead of "=" for sub-ms times on some platforms.
    [GeneratedRegex(@"\btime[=<](?<ms>[\d.]+)\s*ms")]
    private static partial Regex PerReplyRegex { get; }

    /// <inheritdoc />
    public async Task<PingProbeResult> ProbeAsync(
        string            site,
        int               count,
        int               timeoutMs         = 1000,
        int               spacingMs         = 100,
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
                          (spacingMs / 1000.0).ToString(CultureInfo.InvariantCulture),
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

        PingSummary? summary = ParseSummary(stdout);
        if (summary is null)
        {
            // No loss line at all: ping failed for real (unknown host, bad option, ...).
            // Exit code 1 alone just means total loss, which still produces a summary.
            logger.LogError(
                "Could not parse 'ping' output for '{Site}'. Exit code {ExitCode}.\n"
              + "stdout: {Stdout}\nstderr: {Stderr}",
                site,
                process.ExitCode,
                stdout.Trim(),
                stderr.Trim());
        }

        IReadOnlyList<double> rtts = ParseRtts(stdout);
        return new PingProbeResult(site, summary?.Loss, summary?.MdevMs, rtts);
    }

    internal readonly record struct PingSummary(double Loss, double? MdevMs);

    /// <summary>
    /// Parses a <c>ping</c> statistics block. Returns <see langword="null"/> when there is no
    /// loss line (ping failed for real); within a valid summary, <see cref="PingSummary.MdevMs"/>
    /// is <see langword="null"/> when there is no rtt line (e.g. 100% loss). Handles both the
    /// iputils and BSD/macOS dialects.
    /// </summary>
    internal static PingSummary? ParseSummary(string output)
    {
        Match lossMatch = LossRegex.Match(output);
        if (!lossMatch.Success)
            return null;
        var loss = double.Parse(lossMatch.Groups["loss"].Value, CultureInfo.InvariantCulture);

        Match rtt = RttSummaryRegex.Match(output);
        if (!rtt.Success)
            return new PingSummary(loss, null);

        var mdev = double.Parse(rtt.Groups["mdev"].Value, CultureInfo.InvariantCulture);
        return new PingSummary(loss, mdev);
    }

    /// <summary>
    /// Collects all per-reply RTT values (ms) from a <c>ping</c> output in arrival order.
    /// Returns an empty list when there are no replies (total loss or no output).
    /// </summary>
    internal static IReadOnlyList<double> ParseRtts(string output)
    {
        var matches = PerReplyRegex.Matches(output);
        if (matches.Count == 0)
            return ReadOnlyCollection<double>.Empty;

        var list = new List<double>(matches.Count);
        foreach (Match m in matches)
            list.Add(double.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture));
        return list;
    }
}
