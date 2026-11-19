using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Data.Models;

namespace SmartPark.Server.Modules.Pricing;

/// <summary>Публічний інтерфейс модуля Pricing для інших модулів.</summary>
public interface IPricingService
{
    Task<TariffVersion?> GetActiveVersionAsync(Guid parkingId, DateTime at, CancellationToken ct = default);
    Task<decimal> CalculateAsync(Guid parkingId, DateTime from, DateTime to, CancellationToken ct = default);
}

public class PricingService : IPricingService
{
    private readonly AppDbContext _db;

    public PricingService(AppDbContext db) => _db = db;

    public async Task<TariffVersion?> GetActiveVersionAsync(Guid parkingId, DateTime at, CancellationToken ct = default)
    {
        var tariffIds = _db.Tariffs.Where(t => t.ParkingId == parkingId && !t.IsArchived).Select(t => t.Id);
        return await _db.TariffVersions
            .Where(v => tariffIds.Contains(v.TariffId) && v.ValidFrom <= at)
            .OrderByDescending(v => v.ValidFrom)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<decimal> CalculateAsync(Guid parkingId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        // Версія тарифу береться на момент в’їзду: зміна тарифу не впливає на відкриті сесії.
        var version = await GetActiveVersionAsync(parkingId, from, ct)
                      ?? throw new InvalidOperationException("Для паркінгу не налаштовано тариф");
        // У БД час зберігається в UTC, а денний/нічний тариф визначається за київським часом.
        return PriceCalculator.Calculate(version, LocalTime.FromUtc(from), LocalTime.FromUtc(to));
    }
}

public static class LocalTime
{
    private static readonly TimeZoneInfo Zone = Find();

    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>Гарантує DateTimeKind.Utc (вимога Npgsql для timestamptz).</summary>
    public static DateTime AsUtc(this DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Europe/Kyiv", "Europe/Kiev", "FLE Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.Utc;
    }
}
