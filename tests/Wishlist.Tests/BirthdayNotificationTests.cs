using Wishlist.Server.Services;
namespace Wishlist.Tests;
public class BirthdayNotificationTests
{
    [Theory]
    [InlineData("2000-02-29", "2027-01-01", "2027-02-28")]
    [InlineData("2000-02-29", "2028-01-01", "2028-02-29")]
    [InlineData("1990-01-02", "2026-12-30", "2027-01-02")]
    [InlineData("1990-11-19", "2026-11-19", "2026-11-19")]
    public void FindsNextBirthday(string birth, string today, string expected)
        => Assert.Equal(DateOnly.Parse(expected), TelegramNotifications.NextBirthday(DateOnly.Parse(birth), DateOnly.Parse(today)));
}
