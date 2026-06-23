using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

public class PingProberTests
{
    // ── ParseSummary ────────────────────────────────────────────────────────────

    [Fact]
    public void ParseSummary_IputilsFullOutput_ReturnsLossMdev()
    {
        const string output =
            """
            PING google.com (142.250.0.0) 56(84) bytes of data.

            --- google.com ping statistics ---
            4 packets transmitted, 4 received, 0% packet loss, time 3050ms
            rtt min/avg/max/mdev = 10.041/12.050/15.062/1.008 ms
            """;

        var summary = PingProber.ParseSummary(output);

        Assert.True(summary.HasValue);
        Assert.Equal(0, summary!.Value.Loss);
        Assert.Equal(1.008, summary.Value.MdevMs!.Value, 3);
    }

    [Fact]
    public void ParseSummary_BsdMacosOutput_ParsesMdev()
    {
        const string output =
            """
            PING example.com (93.184.216.34): 56 data bytes

            --- example.com ping statistics ---
            5 packets transmitted, 5 packets received, 0.0% packet loss
            round-trip min/avg/max/stddev = 20.1/25.5/30.9/3.7 ms
            """;

        var summary = PingProber.ParseSummary(output);

        Assert.True(summary.HasValue);
        Assert.Equal(0, summary!.Value.Loss);
        Assert.Equal(3.7, summary.Value.MdevMs!.Value, 3);
    }

    [Fact]
    public void ParseSummary_TotalLoss_HasLossButNullMdev()
    {
        const string output =
            """
            PING 10.255.255.1 (10.255.255.1) 56(84) bytes of data.

            --- 10.255.255.1 ping statistics ---
            4 packets transmitted, 0 received, 100% packet loss, time 3070ms
            """;

        var summary = PingProber.ParseSummary(output);

        Assert.True(summary.HasValue);
        Assert.Equal(100, summary!.Value.Loss);
        Assert.Null(summary.Value.MdevMs);
    }

    [Fact]
    public void ParseSummary_PartialLossWithDecimal_ParsesLoss()
    {
        const string output =
            """
            --- host ping statistics ---
            4 packets transmitted, 3 received, 12.5% packet loss, time 3001ms
            rtt min/avg/max/mdev = 5.0/6.0/7.0/0.5 ms
            """;

        var summary = PingProber.ParseSummary(output);

        Assert.True(summary.HasValue);
        Assert.Equal(12.5, summary!.Value.Loss);
        Assert.Equal(0.5, summary.Value.MdevMs!.Value, 3);
    }

    [Fact]
    public void ParseSummary_NoLossLine_ReturnsNull()
    {
        const string output = "ping: cannot resolve nonexistent.invalid: Unknown host";

        Assert.Null(PingProber.ParseSummary(output));
    }

    // ── ParseRtts ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParseRtts_IputilsTypicalOutput_ReturnsAllReplies()
    {
        // iputils emits "time=X ms" (with equals sign)
        const string output =
            """
            PING google.com (142.250.0.0) 56(84) bytes of data.
            64 bytes from 142.250.0.0: icmp_seq=1 ttl=118 time=8.12 ms
            64 bytes from 142.250.0.0: icmp_seq=2 ttl=118 time=9.44 ms
            64 bytes from 142.250.0.0: icmp_seq=3 ttl=118 time=7.88 ms

            --- google.com ping statistics ---
            3 packets transmitted, 3 received, 0% packet loss, time 2003ms
            rtt min/avg/max/mdev = 7.88/8.48/9.44/0.65 ms
            """;

        var rtts = PingProber.ParseRtts(output);

        Assert.Equal(3, rtts.Count);
        Assert.Equal(8.12, rtts[0], 3);
        Assert.Equal(9.44, rtts[1], 3);
        Assert.Equal(7.88, rtts[2], 3);
    }

    [Fact]
    public void ParseRtts_BsdOutput_UsesEqualsOrLessThan()
    {
        // Some BSD platforms emit "time<1 ms" for sub-ms replies
        const string output =
            """
            64 bytes from 127.0.0.1: icmp_seq=0 ttl=64 time<0.1 ms
            64 bytes from 127.0.0.1: icmp_seq=1 ttl=64 time=0.5 ms
            """;

        var rtts = PingProber.ParseRtts(output);

        Assert.Equal(2, rtts.Count);
        Assert.Equal(0.1, rtts[0], 3);
        Assert.Equal(0.5, rtts[1], 3);
    }

    [Fact]
    public void ParseRtts_TotalLoss_ReturnsEmptyList()
    {
        const string output =
            """
            PING 10.255.255.1 (10.255.255.1) 56(84) bytes of data.

            --- 10.255.255.1 ping statistics ---
            4 packets transmitted, 0 received, 100% packet loss, time 3070ms
            """;

        var rtts = PingProber.ParseRtts(output);

        Assert.Empty(rtts);
    }

    [Fact]
    public void ParseRtts_EmptyString_ReturnsEmptyList()
    {
        Assert.Empty(PingProber.ParseRtts(string.Empty));
    }
}
