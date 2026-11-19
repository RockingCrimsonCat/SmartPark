using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Security;

namespace SmartPark.Server.Modules.Pricing;

public record TariffVersionRequest(DateTime ValidFrom, decimal DayRate, decimal NightRate, int NightStartHour,
    int NightEndHour, int FreeMinutes, decimal DailyMax, decimal SubscriptionMonthPrice);

public record CreateTariffRequest(Guid ParkingId, string Name, TariffVersionRequest FirstVersion);

public record PriceQuoteRequest(Guid ParkingId, DateTime From, DateTime To);

[ApiController]
[Route("api/tariffs")]
public class TariffsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPricingService _pricing;

    public TariffsController(AppDbContext db, IPricingService pricing)
    {
        _db = db;
        _pricing = pricing;
    }

    /// <summary>Тарифи паркінгу з усіма версіями.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] Guid parkingId) =>
        Ok(await _db.Tariffs.AsNoTracking()
            .Where(t => t.ParkingId == parkingId)
            .Include(t => t.Versions.OrderByDescending(v => v.ValidFrom))
            .ToListAsync());

    [HttpPost]
    [Authorize(Roles = Roles.TariffManager + "," + Roles.SysAdmin)]
    public async Task<IActionResult> Create(CreateTariffRequest r)
    {
        if (!await _db.Parkings.AnyAsync(p => p.Id == r.ParkingId)) return NotFound();
        var tariff = new Tariff { ParkingId = r.ParkingId, Name = r.Name };
        tariff.Versions.Add(ToVersion(r.FirstVersion));
        _db.Tariffs.Add(tariff);
        await _db.SaveChangesAsync();
        return Ok(tariff);
    }

    /// <summary>
    /// Зміна тарифу створює нову незмінну версію з датою набрання чинності
    /// (сценарій UC15). Попередні версії залишаються для розрахунку відкритих сесій.
    /// </summary>
    [HttpPost("{id:guid}/versions")]
    [Authorize(Roles = Roles.TariffManager + "," + Roles.SysAdmin)]
    public async Task<IActionResult> AddVersion(Guid id, TariffVersionRequest r)
    {
        var tariff = await _db.Tariffs.FindAsync(id);
        if (tariff is null) return NotFound();
        if (await _db.TariffVersions.AnyAsync(v => v.TariffId == id && v.ValidFrom == r.ValidFrom))
            return Conflict(new { error = "Версія з такою датою вже існує" });

        var version = ToVersion(r);
        version.TariffId = id;
        _db.TariffVersions.Add(version);
        await _db.SaveChangesAsync();
        return Ok(version);
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize(Roles = Roles.TariffManager + "," + Roles.SysAdmin)]
    public async Task<IActionResult> Archive(Guid id)
    {
        var tariff = await _db.Tariffs.FindAsync(id);
        if (tariff is null) return NotFound();
        var others = await _db.Tariffs.CountAsync(t => t.ParkingId == tariff.ParkingId && !t.IsArchived && t.Id != id);
        if (others == 0) return BadRequest(new { error = "Спершу призначте паркінгу інший тариф" });
        tariff.IsArchived = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Попередній розрахунок вартості (симуляція для менеджера та водія).</summary>
    [HttpPost("quote")]
    public async Task<IActionResult> Quote(PriceQuoteRequest r) =>
        Ok(new { amount = await _pricing.CalculateAsync(r.ParkingId, r.From, r.To) });

    private TariffVersion ToVersion(TariffVersionRequest r) => new()
    {
        ValidFrom = r.ValidFrom,
        DayRate = r.DayRate,
        NightRate = r.NightRate,
        NightStartHour = r.NightStartHour,
        NightEndHour = r.NightEndHour,
        FreeMinutes = r.FreeMinutes,
        DailyMax = r.DailyMax,
        SubscriptionMonthPrice = r.SubscriptionMonthPrice,
        CreatedBy = User.Identity?.Name ?? ""
    };
}
