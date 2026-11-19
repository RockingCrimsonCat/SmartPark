using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Security;
using SmartPark.Server.Modules.Pricing;

namespace SmartPark.Server.Modules.Payment;

public record RefundRequest(decimal Amount, string Reason);

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPaymentService _payments;
    private readonly IPricingService _pricing;

    public PaymentsController(AppDbContext db, IPaymentService payments, IPricingService pricing)
    {
        _db = db;
        _payments = payments;
        _pricing = pricing;
    }

    /// <summary>Інформація для сторінки оплати гостя за QR-кодом (без реєстрації).</summary>
    [HttpGet("sessions/{sessionId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> SessionToPay(Guid sessionId)
    {
        var s = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId);
        if (s is null) return NotFound();
        var parking = await _db.Parkings.AsNoTracking().FirstAsync(p => p.Id == s.ParkingId);
        var amount = s.Status == SessionStatus.Active || s.Status == SessionStatus.AwaitingPayment
            ? await _pricing.CalculateAsync(s.ParkingId, s.EntryTime, DateTime.UtcNow)
            : s.Amount;
        return Ok(new { s.Id, s.Plate, s.EntryTime, s.Status, s.PaidAt, parking = parking.Name, amount });
    }

    /// <summary>
    /// Оплата паркування гостем (кнопка «Оплатити» – емуляція паркомата / оплати за QR).
    /// Після оплати є 15 хвилин на виїзд.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> PaySession(Guid sessionId)
    {
        var s = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId);
        if (s is null) return NotFound();
        if (s.Status is not (SessionStatus.Active or SessionStatus.AwaitingPayment or SessionStatus.Unpaid))
            return BadRequest(new { error = "Сесію вже закрито" });

        var to = s.Status == SessionStatus.Unpaid && s.ExitTime is not null ? s.ExitTime.Value : DateTime.UtcNow;
        var amount = await _pricing.CalculateAsync(s.ParkingId, s.EntryTime, to);
        var payment = await _payments.ChargeAsync(null, PaymentPurpose.Session, s.Id, amount, $"Паркування {s.Plate}");
        if (payment.Status == PaymentStatus.Succeeded)
        {
            s.PaidAt = DateTime.UtcNow;
            s.PaymentId = payment.Id;
            s.Amount = amount;
            if (s.Status == SessionStatus.Unpaid) s.Status = SessionStatus.Closed;
        }
        await _db.SaveChangesAsync();
        return Ok(new { payment.Id, payment.Status, payment.Amount, payment.FailureReason });
    }

    [HttpGet("my")]
    [Authorize]
    public async Task<IActionResult> My() =>
        Ok(await _db.Payments.AsNoTracking().Where(p => p.UserId == User.GetUserId())
            .OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync());

    [HttpGet]
    [Authorize(Roles = Roles.Finance + "," + Roles.SysAdmin)]
    public async Task<IActionResult> All([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var q = _db.Payments.AsNoTracking();
        if (from is not null) q = q.Where(p => p.CreatedAt >= from.Value.AsUtc());
        if (to is not null) q = q.Where(p => p.CreatedAt < to.Value.AsUtc());
        return Ok(await q.OrderByDescending(p => p.CreatedAt).Take(500).ToListAsync());
    }

    /// <summary>Чек у форматі PDF.</summary>
    [HttpGet("{id:guid}/receipt")]
    [AllowAnonymous]
    public async Task<IActionResult> Receipt(Guid id)
    {
        var p = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p?.ReceiptPath is null || !System.IO.File.Exists(p.ReceiptPath)) return NotFound();
        return PhysicalFile(p.ReceiptPath, "application/pdf", $"receipt-{id}.pdf");
    }

    /// <summary>Повне або часткове повернення коштів з обґрунтуванням (FR-22).</summary>
    [HttpPost("{id:guid}/refund")]
    [Authorize(Roles = Roles.Finance + "," + Roles.SysAdmin)]
    public async Task<IActionResult> Refund(Guid id, RefundRequest r)
    {
        var p = await _db.Payments.FindAsync(id);
        if (p is null) return NotFound();
        if (p.Status != PaymentStatus.Succeeded) return BadRequest(new { error = "Платіж не можна повернути" });
        if (string.IsNullOrWhiteSpace(r.Reason)) return BadRequest(new { error = "Вкажіть обґрунтування" });
        var refunded = await _db.Refunds.Where(x => x.PaymentId == id).SumAsync(x => x.Amount);
        if (r.Amount <= 0 || refunded + r.Amount > p.Amount) return BadRequest(new { error = "Некоректна сума" });

        _db.Refunds.Add(new Refund { PaymentId = id, Amount = r.Amount, Reason = r.Reason, CreatedBy = User.Identity?.Name ?? "" });
        if (refunded + r.Amount == p.Amount) p.Status = PaymentStatus.Refunded;
        await _db.SaveChangesAsync();
        return Ok();
    }
}
