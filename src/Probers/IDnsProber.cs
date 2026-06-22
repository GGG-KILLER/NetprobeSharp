using System.Net;

namespace NetprobeSharp.Probers;

public readonly record struct DnsResolver(string Name, IPAddress Ip);

public readonly record struct DnsProbeResult(DnsResolver Resolver, double Latency);

public interface IDnsProber
{
    /// <summary>
    /// Sends one recursive A-record query for <paramref name="domain"/> directly to
    /// <paramref name="resolver"/> over UDP/53 and measures the round-trip latency.
    /// Unlike <see cref="Dns.GetHostEntry(string)"/>, which always uses the system-configured
    /// resolvers, this targets the resolver you pass in.
    /// </summary>
    /// <remarks>
    /// This only measures latency: it sends a well-formed query and confirms a matching DNS
    /// response comes back (transaction id + response bit); it does not parse the answer
    /// records, so the latency stands regardless of whether the name resolved.
    /// </remarks>
    /// <returns>
    /// The resolver together with the measured latency in milliseconds. Latency is the
    /// configured <c>DnsThreshold</c> when the resolver does not answer within the timeout
    /// or is unreachable/refused (the non-response is recorded, not thrown).
    /// </returns>
    Task<DnsProbeResult> ProbeAsync(
        DnsResolver       resolver,
        string            domain,
        int               timeoutMs         = 1000,
        CancellationToken cancellationToken = default);
}
