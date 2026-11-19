using SmartPark.Data.Models;

namespace SmartPark.Server.Modules.Pricing;

/// <summary>
/// Розрахунок вартості паркування за версією тарифу: денна й нічна ставки за годину,
/// безкоштовні хвилини та денний максимум (на кожну повну добу перебування).
/// Чистий клас без залежностей – покривається модульними тестами.
/// </summary>
public static class PriceCalculator
{
    public static decimal Calculate(TariffVersion t, DateTime from, DateTime to)
    {
        if (to <= from) return 0m;
        var total = to - from;
        if (total.TotalMinutes <= t.FreeMinutes) return 0m;

        decimal sum = 0m;
        var dayStart = from;
        while (dayStart < to)
        {
            var dayEnd = dayStart.AddHours(24) < to ? dayStart.AddHours(24) : to;
            var daySum = SumByMinutes(t, dayStart, dayEnd);
            sum += t.DailyMax > 0 ? Math.Min(daySum, t.DailyMax) : daySum;
            dayStart = dayEnd;
        }

        return Math.Round(sum, 2);
    }

    /// <summary>Погодинна тарифікація з округленням кожної розпочатої години вгору.</summary>
    private static decimal SumByMinutes(TariffVersion t, DateTime from, DateTime to)
    {
        decimal sum = 0m;
        var cursor = from;
        while (cursor < to)
        {
            sum += IsNight(t, cursor) ? t.NightRate : t.DayRate;
            cursor = cursor.AddHours(1);
        }
        return sum;
    }

    public static bool IsNight(TariffVersion t, DateTime time)
    {
        var hour = time.Hour;
        return t.NightStartHour > t.NightEndHour
            ? hour >= t.NightStartHour || hour < t.NightEndHour
            : hour >= t.NightStartHour && hour < t.NightEndHour;
    }
}
