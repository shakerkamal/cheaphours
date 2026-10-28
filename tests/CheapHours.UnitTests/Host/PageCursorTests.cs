using System.Buffers.Text;
using System.Text;
using CheapHours.Core.Ids;
using CheapHours.Host.Api;
using FsCheck.Xunit;

namespace CheapHours.UnitTests.Host;

public sealed class PageCursorTests
{
    private static readonly DateTimeOffset Ten = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private static string Raw(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Device_cursor_round_trips()
    {
        var id = DeviceId.Parse("dev_01JB7Z8K2QABCDEFGHJKMNPQRS");

        Assert.True(PageCursor.TryReadDevice(PageCursor.ForDevice(id), out var after));
        Assert.Equal(id, after);
    }

    [Fact]
    public void Bucket_cursor_round_trips()
    {
        Assert.True(PageCursor.TryReadBucket(PageCursor.ForBucket(Ten.AddMinutes(45)), out var lastStart));
        Assert.Equal(Ten.AddMinutes(45), lastStart);
    }

    [Fact]
    public void Cursors_are_opaque_url_safe_strings()
    {
        var cursor = PageCursor.ForDevice(DeviceId.Parse("dev_1"));

        Assert.True(Base64Url.IsValid(cursor));
        Assert.DoesNotContain("dev_1", cursor, StringComparison.Ordinal);
    }

    [Fact]
    public void Kinds_do_not_mix()
    {
        Assert.False(PageCursor.TryReadBucket(PageCursor.ForDevice(DeviceId.Parse("dev_1")), out _));
        Assert.False(PageCursor.TryReadDevice(PageCursor.ForBucket(Ten), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a cursor")]
    [InlineData("%%%")]
    public void Garbage_is_refused(string cursor)
    {
        Assert.False(PageCursor.TryReadDevice(cursor, out _));
        Assert.False(PageCursor.TryReadBucket(cursor, out _));
    }

    [Fact]
    public void Bucket_cursor_off_the_grid_is_refused()
    {
        Assert.False(PageCursor.TryReadBucket(Raw($"t1:{Ten.AddMinutes(7).ToUnixTimeSeconds()}"), out _));
    }

    [Theory]
    [InlineData("t1:-900")]
    [InlineData("t1:99999999999999999999")]
    [InlineData("t1:")]
    [InlineData("d1:plan_1")]
    [InlineData("d1:")]
    public void Well_encoded_but_invalid_values_are_refused(string text)
    {
        Assert.False(PageCursor.TryReadDevice(Raw(text), out _));
        Assert.False(PageCursor.TryReadBucket(Raw(text), out _));
    }

    [Fact]
    public void Overlong_cursor_is_refused()
    {
        Assert.False(PageCursor.TryReadDevice(Raw("d1:dev_" + new string('A', 300)), out _));
    }

    // Whatever a client sends, reading a cursor answers yes or no and never throws.
    [Property(MaxTest = 500)]
    public bool Reading_any_string_never_throws(string? cursor)
    {
        _ = PageCursor.TryReadDevice(cursor, out _);
        _ = PageCursor.TryReadBucket(cursor, out _);
        return true;
    }

    [Property(MaxTest = 500)]
    public bool Reading_any_encoded_text_never_throws(string? text)
    {
        var cursor = Raw(text ?? string.Empty);
        _ = PageCursor.TryReadDevice(cursor, out _);
        _ = PageCursor.TryReadBucket(cursor, out _);
        return true;
    }
}
