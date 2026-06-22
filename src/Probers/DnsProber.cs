using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetprobeSharp.Probers;

public sealed class DnsProber(ILogger<DnsProber> logger)
    : IDnsProber
{
    // A DNS query never exceeds header(12) + QNAME(<=255) + QTYPE/QCLASS(4); 512 is the
    // classic UDP message ceiling and a safe size for both the query and the reply we read.
    private const int MaxMessageSize = 512;

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
    /// The resolver together with the measured latency in milliseconds, or
    /// <see langword="null"/> latency when the resolver does not answer within the timeout
    /// or is unreachable/refused (the non-response is recorded, not thrown). The caller
    /// decides how to treat a missing value.
    /// </returns>
    public async Task<DnsProbeResult> ProbeAsync(
        DnsResolver       resolver,
        string            domain,
        int               timeoutMs         = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        ushort id = (ushort)Random.Shared.Next(ushort.MaxValue + 1);

        // Rent both buffers from the shared pool so a steady cadence of probes stays
        // allocation-free on the hot path.
        byte[] requestBuffer  = ArrayPool<byte>.Shared.Rent(MaxMessageSize);
        byte[] responseBuffer = ArrayPool<byte>.Shared.Rent(MaxMessageSize);
        try
        {
            int queryLen = BuildAndWriteQuery(id, domain, requestBuffer);

            // AddressFamily follows the resolver address, so v4 and v6 resolvers both work.
            using var dnsSocket = new Socket(resolver.Ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            try
            {
                // Connecting both scopes replies to this resolver and lets us use Send/Receive.
                // For UDP, it sends nothing, but it can still fail right away when there's no route
                // to the resolver (e.g. a v6 address on a v4-only host) -- treat that as no answer.
                await dnsSocket.ConnectAsync(new IPEndPoint(resolver.Ip, 53), cts.Token).ConfigureAwait(false);

                long t0 = Stopwatch.GetTimestamp();
                await dnsSocket.SendAsync(requestBuffer.AsMemory(0, queryLen), SocketFlags.None, cts.Token)
                               .ConfigureAwait(false);

                // Read until our reply arrives, or we time out, skipping any stray datagram.
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    int n = await dnsSocket.ReceiveAsync(responseBuffer.AsMemory(), SocketFlags.None, cts.Token)
                                           .ConfigureAwait(false);

                    if (IsValidAndOurReply(responseBuffer.AsSpan(0, n), id))
                        return new DnsProbeResult(resolver, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Timed out trying to resolve using {Resolver}", resolver);
                return new DnsProbeResult(resolver, null); // timed out (caller cancellation propagates)
            }
            catch (SocketException ex)
            {
                logger.LogError(ex, "Error trying to resolve using {Resolver}", resolver);
                return new DnsProbeResult(resolver, null); // refused / unreachable -> no answer
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(requestBuffer);
            ArrayPool<byte>.Shared.Return(responseBuffer);
        }
    }

    // We don't parse answers. A matching header is enough to call it a valid response.
    internal static bool IsValidAndOurReply(ReadOnlySpan<byte> response, ushort id)
    {
        if (response.Length < 12) return false; // smaller than a DNS header
        var rid = (ushort)((response[0] << 8) | response[1]);
        if (rid                     != id) return false; // answer to a different query
        return (response[2] & 0x80) != 0;                // QR bit must be set: it's a response
    }

    /// <summary>
    /// Writes a recursive A/IN query (header + QNAME + QTYPE + QCLASS) into <paramref name="buffer"/> and returns its
    /// length.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="domain"></param>
    /// <param name="buffer"></param>
    /// <returns>Amount of bytes that were written to the destination.</returns>
    /// <exception cref="ArgumentException">Thrown when any part of the name has more than 63 characters.</exception>
    internal static int BuildAndWriteQuery(ushort id, string domain, Span<byte> buffer)
    {
        var trimmed = domain.Trim().TrimEnd('.');
        // Punycode only when a label is actually non-ASCII; the common ASCII path allocates nothing.
        var name = Ascii.IsValid(trimmed) ? trimmed : new IdnMapping().GetAscii(trimmed);

        // Header: id, RD flag, QDCOUNT = 1, everything else zero.
        buffer[..12].Clear();
        buffer[0] = (byte)(id >> 8);
        buffer[1] = (byte)id;
        buffer[2] = 0x01; // RD (recursion desired)
        buffer[5] = 0x01; // QDCOUNT = 1

        var offset = 12;
        // QNAME: each dot-separated label written as <len><bytes>, ended by a root (0) label.
        var start = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && name[i] != '.') continue;

            int length = i - start;
            if (length == 0)
            {
                start = i + 1;
                continue;
            } // skip empty label left by a stray dot
            if (length > 63)
                throw new ArgumentException($"DNS label exceeds 63 bytes: '{name[start..i]}'.", nameof(domain));

            buffer[offset++] =  (byte)length;
            offset           += Encoding.ASCII.GetBytes(name.AsSpan(start, length), buffer[offset..]);
            start            =  i + 1;
        }

        buffer[offset++] = 0; // root label terminates QNAME
        buffer[offset++] = 0;
        buffer[offset++] = 1; // QTYPE  = A
        buffer[offset++] = 0;
        buffer[offset++] = 1; // QCLASS = IN
        return offset;
    }
}
