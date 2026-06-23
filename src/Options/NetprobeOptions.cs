using System.Net;
using Microsoft.Extensions.Options;

namespace NetprobeSharp.Options;

public sealed class NetprobeOptions
{
    /// <summary>
    /// The sites that will be probed with pings.
    /// </summary>
    public required IReadOnlyList<string> Sites { get; set; }

    /// <summary>
    /// The DNS servers that will be queried for <see cref="DnsTestSite"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, string> DnsResolvers { get; set; }

    /// <summary>
    /// The domain that will be queried on every <see cref="DnsResolvers"/>.
    /// </summary>
    public string DnsTestSite { get; set; } = "google.com";

    /// <summary>
    /// The delay between probes.
    /// </summary>
    public int ProbeIntervalSec { get; set; } = 30;

    /// <summary>
    /// The amount of pings that are sent to each <see cref="Sites"/> every <see cref="ProbeIntervalSec"/>.
    /// </summary>
    public int ProbeCountPerSite { get; set; } = 50;

    /// <summary>
    /// How long to wait for a single ping reply before declaring it lost (<c>-W</c>, in ms).
    /// </summary>
    public int PingTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Delay between consecutive pings within one probe batch (<c>-i</c>, in ms).
    /// Distinct from <see cref="ProbeIntervalSec"/>, which is the cadence between whole
    /// probe cycles.
    /// </summary>
    public int PingSpacingMs { get; set; } = 100;

    /// <summary>
    /// How long to wait for a DNS reply before declaring it a timeout (in ms).
    /// </summary>
    public int DnsTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Options for scoring the network connectivity.
    /// </summary>
    public ScoreOptions Score { get; set; } = new();
}

public sealed class NetprobeOptionsValidator : IValidateOptions<NetprobeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, NetprobeOptions options)
    {
        ValidateOptionsResultBuilder builder = new();

        if (options.Sites is null || options.Sites.Count == 0)
        {
            builder.AddError(
                $"'{nameof(NetprobeOptions.Sites)}' option must be defined and have at least one domain/IP.",
                nameof(NetprobeOptions.Sites));
        }
        else
        {
            for (var index = 0; index < options.Sites.Count; index++)
            {
                var site = options.Sites[index];
                if (Uri.CheckHostName(site) == UriHostNameType.Unknown)
                {
                    builder.AddError(
                        $"'{nameof(NetprobeOptions.Sites)}.{index}' has an invalid domain/IP.",
                        nameof(NetprobeOptions.Sites) + '.' + index);
                }
            }
        }

        if (options.DnsResolvers is null || options.DnsResolvers.Count == 0)
        {
            builder.AddError(
                $"'{nameof(NetprobeOptions.DnsResolvers)}' option must be defined and have at least the 'My_DNS_Server': ... resolver set.",
                nameof(NetprobeOptions.DnsResolvers));
        }
        else
        {
            foreach (var resolver in options.DnsResolvers)
            {
                if (!IPAddress.TryParse(resolver.Value, out _))
                {
                    builder.AddError(
                        $"'{nameof(NetprobeOptions.DnsResolvers)}.{resolver.Key}' has an invalid IP Address.",
                        nameof(NetprobeOptions.DnsResolvers) + '.' + resolver.Key);
                }
            }

            if (!options.DnsResolvers.Keys.Contains("My_DNS_Server", StringComparer.OrdinalIgnoreCase))
            {
                builder.AddError(
                    $"'{nameof(NetprobeOptions.DnsResolvers)}' does not have the 'My_DNS_Server' resolver set (case-insensitive).",
                    nameof(NetprobeOptions.DnsResolvers));
            }
        }

        if (!options.DnsTestSite.Contains('.') || Uri.CheckHostName(options.DnsTestSite) == UriHostNameType.Unknown)
        {
            builder.AddError(
                $"'{nameof(NetprobeOptions.DnsTestSite)}' is not set or contains an invalid domain.",
                nameof(NetprobeOptions.DnsTestSite));
        }

        if (options.ProbeIntervalSec < 1)
        {
            builder.AddError(
                $"'{nameof(NetprobeOptions.ProbeIntervalSec)}' must be greater than or equal to 1.",
                nameof(NetprobeOptions.ProbeIntervalSec));
        }

        if (options.ProbeCountPerSite < 1)
        {
            builder.AddError(
                $"'{nameof(NetprobeOptions.ProbeCountPerSite)}' must be greater than or equal to 1.",
                nameof(NetprobeOptions.ProbeCountPerSite));
        }

        if (options.PingTimeoutMs < 1)
        {
            builder.AddError(
                $"'{nameof(NetprobeOptions.PingTimeoutMs)}' must be greater than or equal to 1.",
                nameof(NetprobeOptions.PingTimeoutMs));
        }

        if (options.PingSpacingMs < 1)
        {
            builder.AddError(
                $"'{nameof(NetprobeOptions.PingSpacingMs)}' must be greater than or equal to 1.",
                nameof(NetprobeOptions.PingSpacingMs));
        }

        if (options.DnsTimeoutMs < 1)
        {
            builder.AddError(
                $"'{nameof(NetprobeOptions.DnsTimeoutMs)}' must be greater than or equal to 1.",
                nameof(NetprobeOptions.DnsTimeoutMs));
        }

        return builder.Build();
    }
}
