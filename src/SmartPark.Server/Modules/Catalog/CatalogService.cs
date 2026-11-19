using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Events;

namespace SmartPark.Server.Modules.Catalog;

public record Occupancy(int Total, int Free, int Occupied, int Reserved, int Unavailable);

/// <summary>Публічний інтерфейс модуля Catalog.</summary>
public interface ICatalogService
{
    Task<Occupancy> GetOccupancyAsync(Guid parkingId, CancellationToken ct = default);
    Task<bool> IsActiveAsync(Guid parkingId, CancellationToken ct = default);
    Task SetSpotStatusAsync(Spot spot, SpotStatus status, string? reason, CancellationToken ct = default);
    void InvalidateOccupancy(Guid parkingId);
}

public class CatalogService : ICatalogService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IOutbox _outbox;

    public CatalogService(AppDbContext db, IMemoryCache cache, IOutbox outbox)
    {
        _db = db;
        _cache = cache;
        _outbox = outbox;
    }

    /// <summary>Зайнятість паркінгу. Кешується в IMemoryCache і скидається подіями зміни статусу місць.</summary>
    public async Task<Occupancy> GetOccupancyAsync(Guid parkingId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey(parkingId), out Occupancy? cached) && cached is not null) return cached;

        var counts = await _db.Spots.AsNoTracking()
            .Where(s => s.ParkingId == parkingId)
            .GroupBy(s => s.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Get(SpotStatus s) => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
        var result = new Occupancy(
            counts.Sum(c => c.Count),
            Get(SpotStatus.Free),
            Get(SpotStatus.Occupied),
            Get(SpotStatus.Reserved),
            Get(SpotStatus.Blocked) + Get(SpotStatus.Maintenance));

        _cache.Set(CacheKey(parkingId), result, TimeSpan.FromSeconds(30));
        return result;
    }

    public Task<bool> IsActiveAsync(Guid parkingId, CancellationToken ct = default) =>
        _db.Parkings.AnyAsync(p => p.Id == parkingId && p.Status == ParkingStatus.Active, ct);

    public Task SetSpotStatusAsync(Spot spot, SpotStatus status, string? reason, CancellationToken ct = default)
    {
        if (spot.Status == status) return Task.CompletedTask;
        spot.Status = status;
        spot.StatusReason = reason;
        spot.StatusChangedAt = DateTime.UtcNow;
        _outbox.Add(new SpotStatusChanged(spot.ParkingId, spot.Id, spot.Code, status.ToString()));
        InvalidateOccupancy(spot.ParkingId);
        return Task.CompletedTask;
    }

    public void InvalidateOccupancy(Guid parkingId) => _cache.Remove(CacheKey(parkingId));

    private static string CacheKey(Guid parkingId) => $"occupancy:{parkingId}";
}
