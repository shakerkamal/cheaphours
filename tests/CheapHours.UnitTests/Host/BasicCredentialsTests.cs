using System.Text;
using CheapHours.Host.Gateway;

namespace CheapHours.UnitTests.Host;

public sealed class BasicCredentialsTests
{
    private static string Basic(string raw) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));

    [Fact]
    public void Reads_username_and_password()
    {
        Assert.True(BasicCredentials.TryParse(Basic("CP001:s3cret"), out var user, out var password, out _));
        Assert.Equal("CP001", user);
        Assert.Equal("s3cret", password);
    }

    [Fact]
    public void Password_may_contain_colons()
    {
        Assert.True(BasicCredentials.TryParse(Basic("CP001:a:b:c"), out var user, out var password, out _));
        Assert.Equal("CP001", user);
        Assert.Equal("a:b:c", password);
    }

    [Fact]
    public void Scheme_is_case_insensitive()
    {
        Assert.True(BasicCredentials.TryParse("basic " + Convert.ToBase64String("CP001:x"u8.ToArray()), out _, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_header_is_missing(string? header)
    {
        Assert.False(BasicCredentials.TryParse(header, out _, out _, out var failure));
        Assert.Equal(BasicCredentials.Missing, failure);
    }

    [Theory]
    [InlineData("Bearer abc")]
    [InlineData("Basic")]
    [InlineData("Basic not*base64")]
    public void Unreadable_header_is_malformed(string header)
    {
        Assert.False(BasicCredentials.TryParse(header, out _, out _, out var failure));
        Assert.Equal(BasicCredentials.Malformed, failure);
    }

    [Theory]
    [InlineData("nocolon")]
    [InlineData("CP001:")]
    [InlineData(":password")]
    public void Missing_part_is_malformed(string raw)
    {
        Assert.False(BasicCredentials.TryParse(Basic(raw), out _, out _, out var failure));
        Assert.Equal(BasicCredentials.Malformed, failure);
    }

    [Fact]
    public void Invalid_utf8_is_malformed()
    {
        Assert.False(BasicCredentials.TryParse("Basic " + Convert.ToBase64String([0x43, 0x3A, 0xC3, 0x28]), out _, out _, out var failure));
        Assert.Equal(BasicCredentials.Malformed, failure);
    }
}
