using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

public class PingProberTests
{
    [Fact]
    public void ParseSummary_IputilsFullOutput_ReturnsLossAvgMdev()
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
        Assert.Equal(12.05, summary.Value.AvgRttMs!.Value, 3);
        Assert.Equal(1.008, summary.Value.MdevMs!.Value, 3);
    }

    [Fact]
    public void ParseSummary_BsdMacosOutput_ParsesRoundTripAvgStddev()
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
        Assert.Equal(25.5, summary.Value.AvgRttMs!.Value, 3);
        Assert.Equal(3.7, summary.Value.MdevMs!.Value, 3);
    }

    [Fact]
    public void ParseSummary_TotalLoss_HasLossButNullRtt()
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
        Assert.Null(summary.Value.AvgRttMs);
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
        Assert.Equal(6.0, summary.Value.AvgRttMs!.Value, 3);
    }

    [Fact]
    public void ParseSummary_NoLossLine_ReturnsNull()
    {
        const string output = "ping: cannot resolve nonexistent.invalid: Unknown host";

        Assert.Null(PingProber.ParseSummary(output));
    }
}