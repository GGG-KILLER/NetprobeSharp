using System.Text;
using NetprobeSharp.Probers;

namespace NetprobeSharp.Tests;

public class DnsProberTests
{
    [Fact]
    public void BuildAndWriteQuery_SimpleDomain_WritesWellFormedQuery()
    {
        var buffer = new byte[512];

        var length = DnsProber.BuildAndWriteQuery(0x1234, "example.com", buffer);

        // Header: id, RD flag, QDCOUNT = 1, everything else zero.
        Assert.Equal(0x12, buffer[0]);
        Assert.Equal(0x34, buffer[1]);
        Assert.Equal(0x01, buffer[2]); // RD
        Assert.Equal(0x00, buffer[3]);
        Assert.Equal(0x00, buffer[4]);
        Assert.Equal(0x01, buffer[5]); // QDCOUNT
        Assert.Equal(0x00, buffer[6]); // ANCOUNT
        Assert.Equal(0x00, buffer[7]);

        var offset = 12;
        Assert.Equal(7, buffer[offset]);
        Assert.Equal("example", Encoding.ASCII.GetString(buffer, offset + 1, 7));
        offset += 8;
        Assert.Equal(3, buffer[offset]);
        Assert.Equal("com", Encoding.ASCII.GetString(buffer, offset + 1, 3));
        offset += 4;

        Assert.Equal(0, buffer[offset]); // root label
        offset += 1;
        Assert.Equal(0, buffer[offset]);     // QTYPE high
        Assert.Equal(1, buffer[offset + 1]); // QTYPE = A
        Assert.Equal(0, buffer[offset + 2]); // QCLASS high
        Assert.Equal(1, buffer[offset + 3]); // QCLASS = IN
        offset += 4;

        Assert.Equal(offset, length);
    }

    [Fact]
    public void BuildAndWriteQuery_TrailingDot_SameAsWithout()
    {
        var withDot    = new byte[512];
        var withoutDot = new byte[512];

        var lenWith    = DnsProber.BuildAndWriteQuery(1, "example.com.", withDot);
        var lenWithout = DnsProber.BuildAndWriteQuery(1, "example.com", withoutDot);

        Assert.Equal(lenWithout, lenWith);
        Assert.Equal(withoutDot.AsSpan(0, lenWithout).ToArray(), withDot.AsSpan(0, lenWith).ToArray());
    }

    [Fact]
    public void BuildAndWriteQuery_NonAsciiDomain_IsPunycoded()
    {
        var buffer = new byte[512];

        DnsProber.BuildAndWriteQuery(1, "bücher.de", buffer);

        // bücher -> xn--bcher-kva (13 bytes)
        Assert.Equal(13, buffer[12]);
        Assert.Equal("xn--bcher-kva", Encoding.ASCII.GetString(buffer, 13, 13));
    }

    [Fact]
    public void BuildAndWriteQuery_LabelOver63Chars_Throws()
    {
        var tooLong = new string('a', 64) + ".com";

        Assert.Throws<ArgumentException>(() => DnsProber.BuildAndWriteQuery(1, tooLong, new byte[512]));
    }

    [Fact]
    public void IsValidAndOurReply_MatchingIdAndResponseBit_True()
    {
        var response = new byte[12];
        response[0] = 0x12;
        response[1] = 0x34;
        response[2] = 0x80; // QR bit set

        Assert.True(DnsProber.IsValidAndOurReply(response, 0x1234));
    }

    [Fact]
    public void IsValidAndOurReply_WrongId_False()
    {
        var response = new byte[12];
        response[0] = 0x12;
        response[1] = 0x34;
        response[2] = 0x80;

        Assert.False(DnsProber.IsValidAndOurReply(response, 0x9999));
    }

    [Fact]
    public void IsValidAndOurReply_QueryBitNotResponse_False()
    {
        var response = new byte[12];
        response[0] = 0x12;
        response[1] = 0x34;
        response[2] = 0x00; // QR bit clear (still a query)

        Assert.False(DnsProber.IsValidAndOurReply(response, 0x1234));
    }

    [Fact]
    public void IsValidAndOurReply_ShorterThanHeader_False()
    {
        Assert.False(DnsProber.IsValidAndOurReply(new byte[11], 0x1234));
    }
}