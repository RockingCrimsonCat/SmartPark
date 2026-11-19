using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Events;
using SmartPark.Server.Infrastructure.Security;
using SmartPark.Server.Modules.Catalog;
using SmartPark.Server.Modules.Payment;
using SmartPark.Server.Modules.Pricing;
using SmartPark.Server.Modules.Session;

namespace SmartPark.Server.Modules.Booking;

public record CreateBookingRequest(Guid ParkingId, string Plate, DateTime StartTime, DateTime EndTime, SpotType SpotType);

public record ExtendBookingRequest(DateTime NewEndTime);

/// <summary>
/// Модуль Booking (UC04). Бронювання з оплатою виконується в одній транзакції БД:
/// якщо платіж відхилено, бронювання не створюється (замість Saga цільової архітектури).
/// </summary>
[ApiController]
[Route("api/bookings")]
[Authorize]
public class BookingsController : ControllerBase
{
    private const decimal ServiceFee = 10m;
    private readonly AppDbContext _db;
    private readonly IPricingService _pricing;
    private readonly IPaymentService _payments;
    private readonly ICatalogService _catalog;
    private readonly IOutbox _outbox;

    public BookingsController(AppDbContext db, IPricingService pricing, IPaymentService payments,
        ICatalogService catalog, IOutbox outbox)
    {
        _db = db;
        _pricing = pricing;
        _payments = payments;
        _catalog = catalog;
        _outbox = outbox;
    }

    [HttpGet("my")]
    public async Task<IActionResult> My()
    {
        var userId = User.GetUserId();
        return Ok(await (from b in _db.Bookings.AsNoTracking()
                         join p in _db.Parkings on b.ParkingId equals p.Id
                         where b.UserId == userId
                         orderby b.StartTime descending
                         select new { b.Id, parking = p.Name, b.ParkingId, b.Plate, b.StartTime, b.EndTime, b.Status, b.Price, b.SpotType, b.PaymentId })
            .Take(100).ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateBookingRequest r)
    {
        var userId = User.GetUserId();
        var start = r.StartTime.AsUtc();
        var end = r.EndTime.AsUtc();
        var plate = SessionService.NormalizePlate(r.Plate);

        if (start < DateTime.UtcNow.AddMinutes(-5) || end <= start || end - start > TimeSpan.FromDays(7))
            return BadRequest(new { error = "Некоректний інтервал бронювання" });
        if (!await _db.Vehicles.AnyAsync(v => v.OwnerId == userId && v.Plate == plate))
            return BadRequest(new { error = "Спершу додайте авто до профілю" });
        if (!await _catalog.IsActiveAsync(r.ParkingId))
            return BadRequest(new { error = "Паркінг недоступний" });

        // Перевірка ємності: кількість місць типу мінус бронювання, що перетинаються з інтервалом.
        var capacity = await _db.Spots.CountAsync(s => s.ParkingId == r.ParkingId && s.Type == r.SpotType &&
            s.Status != SpotStatus.Blocked && s.Status != SpotStatus.Maintenance);
        var overlapping = await _db.Bookings.CountAsync(b => b.ParkingId == r.ParkingId && b.SpotType == r.SpotType &&
            (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Activated) &&
            b.StartTime < end && b.EndTime > start);
        if (overlapping >= Math.Max(1, capacity / 2)) // під бронювання віддається не більше половини місць
            return Conflict(new { error = "Немає вільних місць для бронювання на цей час" });

        var price = await _pricing.CalculateAsync(r.ParkingId, start, end) + ServiceFee;
        var booking = new Data.Models.Booking
        {
            UserId = userId, ParkingId = r.ParkingId, Plate = plate, StartTime = start, EndTime = end,
            SpotType = r.SpotType, Price = price
        };

        await using var tx = await _db.Database.BeginTransactionAsync();
        var payment = await _payments.ChargeAsync(userId, PaymentPurpose.Booking, booking.Id, price, $"Бронювання {plate}");
        if (payment.Status != PaymentStatus.Succeeded)
        {
            await _db.SaveChangesAsync(); // зберігається лише невдалий платіж
            await tx.CommitAsync();
            return BadRequest(new { error = $"Оплата не пройшла: {payment.FailureReason}" });
        }

        booking.Status = BookingStatus.Confirmed;
        booking.PaymentId = payment.Id;
        _db.Bookings.Add(booking);
        _outbox.Add(new BookingConfirmed(booking.Id, userId, booking.ParkingId, start, end));
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok(booking);
    }

    /// <summary>Скасування: не пізніше ніж за 1 годину – повне повернення коштів.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var b = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == id && x.UserId == User.GetUserId());
        if (b is null) return NotFound();
        if (b.Status != BookingStatus.Confirmed) return BadRequest(new { error = "Бронювання не можна скасувати" });

        b.Status = BookingStatus.Cancelled;
        if (b.PaymentId is not null && b.StartTime - DateTime.UtcNow >= TimeSpan.FromHours(1))
        {
            _db.Refunds.Add(new Refund { PaymentId = b.PaymentId.Value, Amount = b.Price, Reason = "Скасування бронювання", CreatedBy = "system" });
            var payment = await _db.Payments.FindAsync(b.PaymentId.Value);
            if (payment is not null) payment.Status = PaymentStatus.Refunded;
        }
        _outbox.Add(new BookingCancelled(b.Id, b.UserId, "Скасовано користувачем"));
        await _db.SaveChangesAsync();
        return Ok(b);
    }

    /// <summary>Продовження бронювання з доплатою.</summary>
    [HttpPost("{id:guid}/extend")]
    public async Task<IActionResult> Extend(Guid id, ExtendBookingRequest r)
    {
        var userId = User.GetUserId();
        var b = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b is null) return NotFound();
        var newEnd = r.NewEndTime.AsUtc();
        if (b.Status is not (BookingStatus.Confirmed or BookingStatus.Activated) || newEnd <= b.EndTime)
            return BadRequest(new { error = "Продовження неможливе" });

        var extra = await _pricing.CalculateAsync(b.ParkingId, b.EndTime, newEnd);
        var payment = await _payments.ChargeAsync(userId, PaymentPurpose.Booking, b.Id, extra, $"Продовження бронювання {b.Plate}");
        if (payment.Status != PaymentStatus.Succeeded)
        {
            await _db.SaveChangesAsync();
            return BadRequest(new { error = $"Оплата не пройшла: {payment.FailureReason}" });
        }
        b.EndTime = newEnd;
        b.Price += extra;
        await _db.SaveChangesAsync();
        return Ok(b);
    }
}

/// <summary>
/// Фонова служба: бронювання, на яке водій не приїхав протягом 15 хв після початку,
/// анулюється (FR-11).
/// </summary>
public class BookingExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;

    public BookingExpiryService(IServiceScopeFactory scopes) => _scopes = scopes;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
                var limit = DateTime.UtcNow - SessionService.BookingEarlyArrival;
                var expired = await db.Bookings.Where(b => b.Status == BookingStatus.Confirmed && b.StartTime < limit).ToListAsync(ct);
                foreach (var b in expired)
                {
                    b.Status = BookingStatus.Expired;
                    outbox.Add(new BookingCancelled(b.Id, b.UserId, "Неявка: бронювання анульовано"));
                }
                if (expired.Count > 0) await db.SaveChangesAsync(ct);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
