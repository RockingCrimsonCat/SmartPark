using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Security;
using SmartPark.Server.Modules.Payment;
using SmartPark.Server.Modules.Pricing;

namespace SmartPark.Server.Modules.Session;

public record ManualEntryRequest(Guid ParkingId, string Plate, string LaneCode, string Reason);

public record VehicleRequest(string Plate, string Brand, bool IsElectric);

public record BuySubscriptionRequest(Guid ParkingId, string Plate, int Months);

[ApiController]
[Route("api/sessions")]
[Authorize]
public class SessionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly SessionService _sessions;

    public SessionsController(AppDbContext db, SessionService sessions)
    {
        _db = db;
        _sessions = sessions;
    }

    /// <summary>Історія паркувань водія (UC09).</summary>
    [HttpGet("my")]
    public async Task<IActionResult> My()
    {
        var userId = User.GetUserId();
        var items = await (from s in _db.Sessions.AsNoTracking()
                           join p in _db.Parkings on s.ParkingId equals p.Id
                           where s.OwnerId == userId
                           orderby s.EntryTime descending
                           select new { s.Id, s.Plate, parking = p.Name, s.EntryTime, s.ExitTime, s.Status, s.Mode, s.Amount, s.PaymentId })
            .Take(200).ToListAsync();
        return Ok(items);
    }

    /// <summary>Сесії для персоналу (фільтр за паркінгом і статусом).</summary>
    [HttpGet]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> List([FromQuery] Guid? parkingId, [FromQuery] SessionStatus? status)
    {
        var q = _db.Sessions.AsNoTracking();
        if (parkingId is not null) q = q.Where(s => s.ParkingId == parkingId);
        if (status is not null) q = q.Where(s => s.Status == status);
        return Ok(await q.OrderByDescending(s => s.EntryTime).Take(300).ToListAsync());
    }

    /// <summary>Ручне підтвердження в’їзду оператором (номер не розпізнано).</summary>
    [HttpPost("manual-entry")]
    [Authorize(Roles = Roles.Operator + "," + Roles.ParkingAdmin + "," + Roles.SysAdmin)]
    public async Task<IActionResult> ManualEntry(ManualEntryRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return BadRequest(new { error = "Вкажіть причину" });
        var decision = await _sessions.ManualEntryAsync(r.ParkingId, r.Plate, r.LaneCode, r.Reason, User.Identity?.Name ?? "");
        return Ok(decision);
    }
}

/// <summary>Транспортні засоби водія – додавання, редагування, видалення (UC02).</summary>
[ApiController]
[Route("api/vehicles")]
[Authorize]
public class VehiclesController : ControllerBase
{
    private const int MaxVehicles = 5;
    private readonly AppDbContext _db;

    public VehiclesController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> My() =>
        Ok(await _db.Vehicles.AsNoTracking().Where(v => v.OwnerId == User.GetUserId()).OrderBy(v => v.CreatedAt).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Add(VehicleRequest r)
    {
        var userId = User.GetUserId();
        var plate = SessionService.NormalizePlate(r.Plate);
        if (plate.Length is < 4 or > 10) return BadRequest(new { error = "Некоректний номер" });
        if (await _db.Vehicles.CountAsync(v => v.OwnerId == userId) >= MaxVehicles)
            return BadRequest(new { error = $"Можна додати не більше {MaxVehicles} авто" });
        if (await _db.Vehicles.AnyAsync(v => v.Plate == plate))
            return Conflict(new { error = "Номер уже прив’язаний до іншого облікового запису" });

        var v = new Vehicle { OwnerId = userId, Plate = plate, Brand = r.Brand, IsElectric = r.IsElectric };
        _db.Vehicles.Add(v);
        await _db.SaveChangesAsync();
        return Ok(v);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, VehicleRequest r)
    {
        var v = await _db.Vehicles.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == User.GetUserId());
        if (v is null) return NotFound();
        v.Brand = r.Brand;
        v.IsElectric = r.IsElectric;
        await _db.SaveChangesAsync();
        return Ok(v);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var v = await _db.Vehicles.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == User.GetUserId());
        if (v is null) return NotFound();
        var busy = await _db.Sessions.AnyAsync(s => s.Plate == v.Plate && s.Status == SessionStatus.Active)
                   || await _db.Bookings.AnyAsync(b => b.Plate == v.Plate &&
                       (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Activated));
        if (busy) return BadRequest(new { error = "Авто має активну сесію або бронювання" });
        _db.Vehicles.Remove(v);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

/// <summary>Абонементи (FR-23).</summary>
[ApiController]
[Route("api/subscriptions")]
[Authorize]
public class SubscriptionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPricingService _pricing;
    private readonly IPaymentService _payments;

    public SubscriptionsController(AppDbContext db, IPricingService pricing, IPaymentService payments)
    {
        _db = db;
        _pricing = pricing;
        _payments = payments;
    }

    [HttpGet("my")]
    public async Task<IActionResult> My() =>
        Ok(await _db.Subscriptions.AsNoTracking().Where(s => s.OwnerId == User.GetUserId())
            .OrderByDescending(s => s.ValidTo).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Buy(BuySubscriptionRequest r)
    {
        var userId = User.GetUserId();
        var plate = SessionService.NormalizePlate(r.Plate);
        if (!await _db.Vehicles.AnyAsync(v => v.OwnerId == userId && v.Plate == plate))
            return BadRequest(new { error = "Спершу додайте авто до профілю" });
        if (r.Months is < 1 or > 12) return BadRequest(new { error = "Термін від 1 до 12 місяців" });

        var version = await _pricing.GetActiveVersionAsync(r.ParkingId, DateTime.UtcNow);
        if (version is null || version.SubscriptionMonthPrice <= 0)
            return BadRequest(new { error = "На цьому паркінгу абонементи не продаються" });

        var from = DateTime.UtcNow;
        var sub = new Subscription
        {
            OwnerId = userId, ParkingId = r.ParkingId, Plate = plate, ValidFrom = from,
            ValidTo = from.AddMonths(r.Months), Price = version.SubscriptionMonthPrice * r.Months
        };
        var payment = await _payments.ChargeAsync(userId, PaymentPurpose.Subscription, sub.Id, sub.Price,
            $"Абонемент {plate}, {r.Months} міс.");
        if (payment.Status != PaymentStatus.Succeeded)
        {
            await _db.SaveChangesAsync(); // зберігаємо невдалий платіж для історії
            return BadRequest(new { error = $"Оплата не пройшла: {payment.FailureReason}" });
        }
        sub.PaymentId = payment.Id;
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();
        return Ok(sub);
    }
}
