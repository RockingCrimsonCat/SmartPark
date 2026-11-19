using SmartPark.Data.Models;
using SmartPark.Server.Modules.Pricing;
using SmartPark.Server.Modules.Session;
using Xunit;

namespace SmartPark.Tests;

public class PriceCalculatorTests
{
    private static readonly TariffVersion Tariff = new()
    {
        DayRate = 40, NightRate = 20, NightStartHour = 22, NightEndHour = 7, FreeMinutes = 15, DailyMax = 400
    };

    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    [Fact]
    public void FreeMinutes_AreNotCharged() =>
        Assert.Equal(0m, PriceCalculator.Calculate(Tariff, At(2, 10), At(2, 10, 15)));

    [Fact]
    public void StartedHour_IsRoundedUp() =>
        Assert.Equal(80m, PriceCalculator.Calculate(Tariff, At(2, 10), At(2, 11, 20)));

    [Fact]
    public void NightHours_UseNightRate() =>
        // 21:00–22:00 денна (40) + 22:00–00:00 нічна (2 × 20)
        Assert.Equal(80m, PriceCalculator.Calculate(Tariff, At(2, 21), At(3, 0)));

    [Fact]
    public void DailyMaximum_IsApplied() =>
        // 24 години: без обмеження вийшло б 15 × 40 + 9 × 20 = 780 грн
        Assert.Equal(400m, PriceCalculator.Calculate(Tariff, At(2, 8), At(3, 8)));

    [Fact]
    public void SeveralDays_EachDayCappedSeparately() =>
        Assert.Equal(800m + 80m, PriceCalculator.Calculate(Tariff, At(2, 8), At(4, 10)));

    [Theory]
    [InlineData("аа 1234 вв", "AA1234BB")]
    [InlineData("ka-0001-ev", "KA0001EV")]
    [InlineData("ВХ6666ХХ", "BX6666XX")]
    public void Plate_IsNormalized(string input, string expected) =>
        Assert.Equal(expected, SessionService.NormalizePlate(input));
}
