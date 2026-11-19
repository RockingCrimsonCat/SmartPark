using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Security;

namespace SmartPark.Server.Modules.Catalog;

public record ParkingRequest(string Name, string City, string Address, double Latitude, double Longitude, string OpeningHours);

public record LevelRequest(string Name, int Number, int RegularSpots, int ElectricSpots, int DisabledSpots);

public record SpotStatusRequest(SpotStatus Status, string? Reason);

[ApiController]
[Route("api/parkings")]
public class ParkingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICatalogService _catalog;

    public ParkingsController(AppDbContext db, ICatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    /// <summary>Список паркінгів із зайнятістю (публічний – для карти водія).</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = false)
    {
        var query = _db.Parkings.AsNoTracking();
        if (!includeInactive) query = query.Where(p => p.Status == ParkingStatus.Active);
        var parkings = await query.OrderBy(p => p.City).ThenBy(p => p.Name).ToListAsync();

        var result = new List<object>();
        foreach (var p in parkings)
        {
            var occ = await _catalog.GetOccupancyAsync(p.Id);
            result.Add(new { p.Id, p.Name, p.City, p.Address, p.Latitude, p.Longitude, p.OpeningHours, p.Status, occupancy = occ });
        }
        return Ok(result);
    }

    /// <summary>Паркінг зі структурою рівнів і місць (схема паркінгу).</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var p = await _db.Parkings.AsNoTracking()
            .Include(x => x.Levels.OrderBy(l => l.Number))
            .ThenInclude(l => l.Spots.OrderBy(s => s.Code))
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        var devices = await _db.Devices.AsNoTracking().Where(d => d.ParkingId == id && d.Type != DeviceType.Sensor)
            .OrderBy(d => d.Code).ToListAsync();
        return Ok(new
        {
            p.Id, p.Name, p.City, p.Address, p.Latitude, p.Longitude, p.OpeningHours, p.Status,
            occupancy = await _catalog.GetOccupancyAsync(id),
            levels = p.Levels.Select(l => new
            {
                l.Id, l.Name, l.Number,
                spots = l.Spots.Select(s => new { s.Id, s.Code, s.Type, s.Status, s.StatusReason })
            }),
            devices = devices.Select(d => new { d.Id, d.Code, d.Type, d.Status, d.LastHeartbeat })
        });
    }

    [HttpPost]
    [Authorize(Roles = Roles.Network)]
    public async Task<IActionResult> Create(ParkingRequest r)
    {
        var p = new Parking
        {
            Name = r.Name, City = r.City, Address = r.Address,
            Latitude = r.Latitude, Longitude = r.Longitude, OpeningHours = r.OpeningHours
        };
        _db.Parkings.Add(p);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = p.Id }, p);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Network)]
    public async Task<IActionResult> Update(Guid id, ParkingRequest r)
    {
        var p = await _db.Parkings.FindAsync(id);
        if (p is null) return NotFound();
        p.Name = r.Name;
        p.City = r.City;
        p.Address = r.Address;
        p.Latitude = r.Latitude;
        p.Longitude = r.Longitude;
        p.OpeningHours = r.OpeningHours;
        await _db.SaveChangesAsync();
        return Ok(p);
    }

    /// <summary>Активація паркінгу після перевірки конфігурації (UC14).</summary>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Roles = Roles.Network)]
    public async Task<IActionResult> Activate(Guid id)
    {
        var p = await _db.Parkings.FindAsync(id);
        if (p is null) return NotFound();
        var problems = new List<string>();
        if (!await _db.Spots.AnyAsync(s => s.ParkingId == id)) problems.Add("немає паркомісць");
        if (!await _db.Tariffs.AnyAsync(t => t.ParkingId == id && !t.IsArchived)) problems.Add("не призначено тариф");
        if (!await _db.Devices.AnyAsync(d => d.ParkingId == id && d.Type == DeviceType.Barrier))
            problems.Add("немає шлагбаума");
        if (problems.Count > 0) return BadRequest(new { error = "Конфігурація неповна: " + string.Join(", ", problems) });
        p.Status = ParkingStatus.Active;
        await _db.SaveChangesAsync();
        return Ok(p);
    }

    /// <summary>
    /// «Видалення» паркінгу – архівування (історію сесій видаляти не можна).
    /// Можливе лише без активних сесій і бронювань.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Network)]
    public async Task<IActionResult> Archive(Guid id)
    {
        var p = await _db.Parkings.FindAsync(id);
        if (p is null) return NotFound();
        var hasActive = await _db.Sessions.AnyAsync(s => s.ParkingId == id && s.Status == SessionStatus.Active)
                        || await _db.Bookings.AnyAsync(b => b.ParkingId == id &&
                            (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Activated));
        if (hasActive) return BadRequest(new { error = "Є активні сесії або бронювання" });
        p.Status = ParkingStatus.Archived;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Додавання рівня з автоматичною генерацією місць і сенсорів.</summary>
    [HttpPost("{id:guid}/levels")]
    [Authorize(Roles = Roles.Network)]
    public async Task<IActionResult> AddLevel(Guid id, LevelRequest r)
    {
        if (!await _db.Parkings.AnyAsync(p => p.Id == id)) return NotFound();
        if (await _db.Levels.AnyAsync(l => l.ParkingId == id && l.Number == r.Number))
            return Conflict(new { error = "Рівень з таким номером уже існує" });

        var level = new Level { ParkingId = id, Name = r.Name, Number = r.Number };
        var n = 1;
        void AddSpots(int count, SpotType type)
        {
            for (var i = 0; i < count; i++, n++)
            {
                var code = $"{r.Number}-{n:D2}";
                level.Spots.Add(new Spot { ParkingId = id, Code = code, Type = type });
                _db.Devices.Add(new Data.Models.Device { ParkingId = id, Code = $"S-{code}", Type = DeviceType.Sensor, SpotCode = code });
            }
        }
        AddSpots(r.RegularSpots, SpotType.Regular);
        AddSpots(r.ElectricSpots, SpotType.Electric);
        AddSpots(r.DisabledSpots, SpotType.Disabled);

        _db.Levels.Add(level);
        await _db.SaveChangesAsync();
        _catalog.InvalidateOccupancy(id);
        return Ok(new { level.Id, level.Name, spots = level.Spots.Count });
    }

    /// <summary>Зміна статусу паркомісця персоналом (UC13).</summary>
    [HttpPut("spots/{spotId:guid}/status")]
    [Authorize(Roles = Roles.Operator + "," + Roles.ParkingAdmin + "," + Roles.Technician + "," + Roles.SysAdmin)]
    public async Task<IActionResult> SetSpotStatus(Guid spotId, SpotStatusRequest r)
    {
        var spot = await _db.Spots.FindAsync(spotId);
        if (spot is null) return NotFound();
        if (r.Status is SpotStatus.Blocked or SpotStatus.Maintenance && string.IsNullOrWhiteSpace(r.Reason))
            return BadRequest(new { error = "Вкажіть причину" });
        await _catalog.SetSpotStatusAsync(spot, r.Status, r.Reason);
        await _db.SaveChangesAsync();
        return Ok(new { spot.Id, spot.Code, spot.Status, spot.StatusReason });
    }
}
