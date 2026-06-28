using Microsoft.Extensions.Options;
using NetprobeSharp.Options;

namespace NetprobeSharp.Tests;

public class NetprobeOptionsValidatorTests
{
    private static NetprobeOptions ValidOptions()
        => new()
           {
               Sites        = [ "google.com" ],
               DnsResolvers = new Dictionary<string, string> { ["My_DNS_Server"] = "8.8.8.8" },
           };

    private static ValidateOptionsResult Validate(NetprobeOptions opts)
        => new NetprobeOptionsValidator().Validate(null, opts);

    [Fact]
    public void ValidOptions_Passes()
    {
        Assert.True(Validate(ValidOptions()).Succeeded);
    }

    [Fact]
    public void EmptySites_Fails()
    {
        var opts = ValidOptions();
        opts.Sites = [ ];
        var result = Validate(opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(NetprobeOptions.Sites)));
    }

    [Fact]
    public void InvalidSiteHostname_Fails()
    {
        var opts = ValidOptions();
        opts.Sites = [ "not a valid host!" ];
        var result = Validate(opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(NetprobeOptions.Sites)));
    }

    [Fact]
    public void EmptyDnsResolvers_Fails()
    {
        var opts = ValidOptions();
        opts.DnsResolvers = new Dictionary<string, string>();
        var result = Validate(opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(NetprobeOptions.DnsResolvers)));
    }

    [Fact]
    public void InvalidResolverIp_Fails()
    {
        var opts = ValidOptions();
        opts.DnsResolvers = new Dictionary<string, string> { ["My_DNS_Server"] = "not-an-ip" };
        var result = Validate(opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("My_DNS_Server"));
    }

    [Fact]
    public void MissingMyDnsServer_Fails()
    {
        var opts = ValidOptions();
        opts.DnsResolvers = new Dictionary<string, string> { ["Other"] = "1.1.1.1" };
        var result = Validate(opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("My_DNS_Server"));
    }

    [Theory]
    [InlineData(nameof(NetprobeOptions.ProbeIntervalSec))]
    [InlineData(nameof(NetprobeOptions.ProbeCountPerSite))]
    [InlineData(nameof(NetprobeOptions.PingTimeoutMs))]
    [InlineData(nameof(NetprobeOptions.PingSpacingMs))]
    [InlineData(nameof(NetprobeOptions.DnsTimeoutMs))]
    public void SubOneIntOption_Fails(string optionName)
    {
        var opts = ValidOptions();
        switch (optionName)
        {
            case nameof(NetprobeOptions.ProbeIntervalSec):  opts.ProbeIntervalSec  = 0; break;
            case nameof(NetprobeOptions.ProbeCountPerSite): opts.ProbeCountPerSite = 0; break;
            case nameof(NetprobeOptions.PingTimeoutMs):     opts.PingTimeoutMs     = 0; break;
            case nameof(NetprobeOptions.PingSpacingMs):     opts.PingSpacingMs     = 0; break;
            case nameof(NetprobeOptions.DnsTimeoutMs):      opts.DnsTimeoutMs      = 0; break;
        }
        var result = Validate(opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(optionName));
    }

    [Fact]
    public void Speedtest_TestIntervalMin_BelowMinimum_Fails()
    {
        var opts = ValidOptions();
        opts.Speedtest.TestIntervalMin = 4;
        var result = Validate(opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(SpeedtestOptions.TestIntervalMin)));
    }

}
