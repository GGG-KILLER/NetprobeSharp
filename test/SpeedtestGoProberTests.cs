using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

/// <summary>
/// Unit tests for <see cref="SpeedtestGoProber.ParseOutput"/> — the pure aggregation
/// method that can be tested without spawning a process.
/// </summary>
public class SpeedtestGoProberTests
{
    // Sample JSON provided in the task (single server, sent=0 so no packet-loss data).
    private const string SampleJson = """
        {"timestamp":"2026-07-01 04:21:28.243","user_info":{"IP":"45.185.170.111","Lat":"-23.5335","Lon":"-46.6359","Isp":"COLLIS TELECOMUNICAÇÕES LTDA"},"servers":[{"url":"http://speedtestspo.telium.com.br:8080/speedtest/upload.php","lat":"-23.5000","lon":"-46.6167","name":"São Paulo","country":"Brazil","sponsor":"Telium Network","id":"43044","host":"speedtestspo.telium.com.br:8080","distance":4.212817091798316,"latency":3009191,"max_latency":3218349,"min_latency":2555523,"jitter":186878,"dl_speed":98479038.43521068,"ul_speed":98469244.02662826,"test_duration":{"ping":2239054099,"download":null,"upload":null,"total":2239054099},"packet_loss":{"sent":0,"dup":0,"max":0}}]}
        """;

    [Fact]
    public void ParseOutput_SampleJson_ParsedCorrectly()
    {
        var result = SpeedtestGoProber.ParseOutput(SampleJson);

        Assert.NotNull(result);
        // latency = 3_009_191 ns → 3.009191 ms
        Assert.Equal(TimeSpan.FromTicks(3009191 / 100), result.Latency);
        // min_latency = 2_555_523 ns
        Assert.Equal(TimeSpan.FromTicks(2555523 / 100), result.MinLatency);
        // max_latency = 3_218_349 ns
        Assert.Equal(TimeSpan.FromTicks(3218349 / 100), result.MaxLatency);
        // jitter = 186_878 ns
        Assert.Equal(TimeSpan.FromTicks(186878 / 100), result.Jitter);
        // speeds are bytes/s, raw
        Assert.Equal(98479038.43521068, result.DownloadBytesPerSecond, precision: 2);
        Assert.Equal(98469244.02662826, result.UploadBytesPerSecond, precision: 2);
        // sent=0 → no loss data
        Assert.Null(result.PacketLossRatio);
    }

    [Fact]
    public void ParseOutput_EmptyServers_ReturnsNull()
    {
        const string json = """{"timestamp":"2026-07-01","user_info":{},"servers":[]}""";
        Assert.Null(SpeedtestGoProber.ParseOutput(json));
    }

    [Fact]
    public void ParseOutput_InvalidJson_ReturnsNull()
    {
        Assert.Null(SpeedtestGoProber.ParseOutput("not json at all"));
        Assert.Null(SpeedtestGoProber.ParseOutput(""));
    }

    [Fact]
    public void ParseOutput_TwoServers_ThroughputSummed_LatencyAveraged()
    {
        // Two servers:
        //   Server A: dl=100 By/s, ul=50 By/s, latency=2ms, min=1ms, max=3ms, jitter=0.5ms
        //   Server B: dl=200 By/s, ul=80 By/s, latency=4ms, min=2ms, max=6ms, jitter=1.5ms
        // Expected:
        //   download = 300 By/s (sum)
        //   upload   = 130 By/s (sum)
        //   latency  = 3ms      (mean)
        //   minLat   = 1.5ms    (mean)
        //   maxLat   = 4.5ms    (mean)
        //   jitter   = 1ms      (mean)
        //   packetLoss: both have sent=0 → null
        const string json = """
            {"servers":[
              {"latency":2000000,"min_latency":1000000,"max_latency":3000000,"jitter":500000,"dl_speed":100,"ul_speed":50,"packet_loss":{"sent":0,"dup":0,"max":0}},
              {"latency":4000000,"min_latency":2000000,"max_latency":6000000,"jitter":1500000,"dl_speed":200,"ul_speed":80,"packet_loss":{"sent":0,"dup":0,"max":0}}
            ]}
            """;

        var result = SpeedtestGoProber.ParseOutput(json);

        Assert.NotNull(result);
        Assert.Equal(300.0, result.DownloadBytesPerSecond, precision: 0);
        Assert.Equal(130.0, result.UploadBytesPerSecond, precision: 0);
        Assert.Equal(TimeSpan.FromMilliseconds(3), result.Latency);
        Assert.Equal(TimeSpan.FromMilliseconds(1.5), result.MinLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(4.5), result.MaxLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(1), result.Jitter);
        Assert.Null(result.PacketLossRatio);
    }

    [Fact]
    public void ParseOutput_PacketLoss_SummedCounts()
    {
        // Two servers with loss data:
        //   Server A: sent=10, dup=0, max=9  → loss = 1 - (10-0)/(9+1) = 1 - 1.0 = 0
        //   Server B: sent=10, dup=0, max=14 → loss = 1 - (10-0)/(14+1) = 1 - 10/15 = 0.333…
        // Summed: sent-dup=20, max+1=25 → ratio = 1 - 20/25 = 0.2
        // Server C (sent=0): excluded from loss calculation
        const string json = """
            {"servers":[
              {"latency":1000000,"min_latency":1000000,"max_latency":1000000,"jitter":0,"dl_speed":1,"ul_speed":1,"packet_loss":{"sent":10,"dup":0,"max":9}},
              {"latency":1000000,"min_latency":1000000,"max_latency":1000000,"jitter":0,"dl_speed":1,"ul_speed":1,"packet_loss":{"sent":10,"dup":0,"max":14}},
              {"latency":1000000,"min_latency":1000000,"max_latency":1000000,"jitter":0,"dl_speed":1,"ul_speed":1,"packet_loss":{"sent":0,"dup":0,"max":0}}
            ]}
            """;

        var result = SpeedtestGoProber.ParseOutput(json);

        Assert.NotNull(result);
        Assert.NotNull(result.PacketLossRatio);
        Assert.Equal(0.2, result.PacketLossRatio.Value, precision: 10);
    }

    [Fact]
    public void ParseOutput_NullPacketLoss_TreatedAsNoData()
    {
        // packet_loss may be null in JSON when using default http ping mode
        const string json = """
            {"servers":[
              {"latency":1000000,"min_latency":1000000,"max_latency":1000000,"jitter":0,"dl_speed":50,"ul_speed":25,"packet_loss":null}
            ]}
            """;

        var result = SpeedtestGoProber.ParseOutput(json);

        Assert.NotNull(result);
        Assert.Null(result.PacketLossRatio);
    }
}
