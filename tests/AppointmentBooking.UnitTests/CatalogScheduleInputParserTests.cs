using AppointmentBooking.Api.Telegram.Handlers;

namespace AppointmentBooking.UnitTests;

public sealed class CatalogScheduleInputParserTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("5000", "5000")]
    [InlineData(" 5000.25 ", "5000.25")]
    [InlineData("0005.2500", "5.25")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void PricesPreserveExactValue(string text, string expected)
    {
        Assert.True(CatalogScheduleInputParser.TryParsePrice(text, out var price));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), price);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("5,000")]
    [InlineData("5 000")]
    [InlineData("5000,25")]
    [InlineData("1e3")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("5.2.3")]
    [InlineData("NaN")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1.12345678901234567890123456789")]
    public void InvalidOrRoundedPricesFailWithoutAValue(string? text)
    {
        Assert.False(CatalogScheduleInputParser.TryParsePrice(text, out var price));
        Assert.Equal(0m, price);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 40 ", 40)]
    [InlineData("2147483647", int.MaxValue)]
    public void PositiveWholeMinutesAreAccepted(string text, int expected)
    {
        Assert.True(CatalogScheduleInputParser.TryParseDuration(text, out var duration));
        Assert.Equal(expected, duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("1.5")]
    [InlineData("30 minutes")]
    [InlineData("2147483648")]
    [InlineData("+30")]
    public void InvalidDurationsFailWithoutAValue(string? text)
    {
        Assert.False(CatalogScheduleInputParser.TryParseDuration(text, out var duration));
        Assert.Equal(0, duration);
    }

    [Theory]
    [InlineData("2026-11-12", 2026, 11, 12)]
    [InlineData(" 2028-02-29 ", 2028, 2, 29)]
    public void CalendarDatesUseExplicitIsoFormat(string text, int year, int month, int day)
    {
        Assert.True(CatalogScheduleInputParser.TryParseDate(text, out var date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-02-29")]
    [InlineData("12/11/2026")]
    [InlineData("2026-1-02")]
    [InlineData("2026-11-12T00:00:00Z")]
    [InlineData("2026-13-01")]
    public void InvalidDatesAreRejected(string? text) =>
        Assert.False(CatalogScheduleInputParser.TryParseDate(text, out _));

    [Theory]
    [InlineData("00:00", 0, 0)]
    [InlineData(" 09:05 ", 9, 5)]
    [InlineData("23:59", 23, 59)]
    public void TimesUseTwentyFourHourMinuteFormat(string text, int hour, int minute)
    {
        Assert.True(CatalogScheduleInputParser.TryParseTime(text, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("24:00")]
    [InlineData("09:60")]
    [InlineData("9:05")]
    [InlineData("09:05:00")]
    [InlineData("9 PM")]
    public void InvalidTimesAreRejected(string? text) =>
        Assert.False(CatalogScheduleInputParser.TryParseTime(text, out _));
}
