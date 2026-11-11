using System.Text;
using AppointmentBooking.Api.Telegram;

namespace AppointmentBooking.UnitTests;

public sealed class CallbackDataParserTests
{
    [Theory]
    [InlineData("book")]
    [InlineData("schedule.hours")]
    public void SupportedRouteRoundTripsWithinTelegramByteLimit(string route)
    {
        var revision = Guid.NewGuid();
        var data = CallbackDataParser.Format(route, revision);
        Assert.InRange(Encoding.UTF8.GetByteCount(data), 1, 64);
        Assert.True(CallbackDataParser.TryParse(data, out var parsed));
        Assert.Equal(new TelegramCallback(route, revision), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2:11111111111111111111111111111111:book")]
    [InlineData("1:11111111111111111111111111111111:book:extra")]
    [InlineData("1:00000000000000000000000000000000:book")]
    [InlineData("1:bad:book")]
    [InlineData("1:11111111111111111111111111111111:BOOK")]
    public void MalformedOrUnsupportedDataIsRejected(string? data)
    {
        Assert.False(CallbackDataParser.TryParse(data, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void OversizedUtf8CallbackIsRejected()
    {
        Assert.False(CallbackDataParser.TryParse(new string('ա', 40), out _));
    }

    [Fact]
    public void ItemCallbackRoundTripsAtExactly64Bytes()
    {
        var revision = Guid.NewGuid();
        var data = CallbackDataParser.FormatItem("abcdefghijklmnopqrst", revision, "aB12_-34");
        Assert.Equal(64, Encoding.UTF8.GetByteCount(data));
        Assert.True(CallbackDataParser.TryParse(data, out var parsed));
        Assert.Equal(new TelegramCallback("abcdefghijklmnopqrst", revision, "aB12_-34"), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789")]
    [InlineData("../id")]
    [InlineData("item:id")]
    public void InvalidItemKeysAreRejected(string key)
    {
        var revision = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => CallbackDataParser.FormatItem("staff", revision, key));
        Assert.False(CallbackDataParser.TryParse($"2:{revision:N}:staff:{key}", out _));
    }
}
