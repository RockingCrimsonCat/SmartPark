using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Server.Infrastructure.Events;
using SmartPark.Server.Infrastructure.Realtime;
using SmartPark.Server.Infrastructure.Security;
using NotificationEntity = SmartPark.Data.Models.Notification;

namespace SmartPark.Server.Modules.Notification;

/// <summary>
/// Модуль Notification: перетворює події шини на сповіщення в особистому кабінеті
/// і транслює зміни в реальному часі через SignalR.
/// </summary>
public class NotificationHandlers :
    IEventHandler<SessionStarted>,
    IEventHandler<SessionClosed>,
    IEventHandler<BookingConfirmed>,
    IEventHandler<BookingCancelled>,
    IEventHandler<EntryNeedsManualCheck>,
    IEventHandler<IncidentCreated>,
    IEventHandler<SpotStatusChanged>,
    IEventHandler<BarrierOpened>
{
    private readonly AppDbContext _db;
    private readonly IRealtime _realtime;

    public NotificationHandlers(AppDbContext db, IRealtime realtime)
    {
        _db = db;
        _realtime = realtime;
    }

    public Task HandleAsync(SessionStarted e, CancellationToken ct) =>
        e.OwnerId is null ? Task.CompletedTask : ToUser(e.OwnerId.Value, "Паркування розпочато", $"Авто {e.Plate} заїхало на паркінг.");

    public Task HandleAsync(SessionClosed e, CancellationToken ct) =>
        e.OwnerId is null
            ? Task.CompletedTask
            : ToUser(e.OwnerId.Value, "Паркування завершено", $"Авто {e.Plate} виїхало. Сума: {e.Amount:0.00} грн.");

    public Task HandleAsync(BookingConfirmed e, CancellationToken ct) =>
        ToUser(e.UserId, "Бронювання підтверджено", $"Місце заброньовано на {e.StartTime:dd.MM HH:mm} (UTC).");

    public Task HandleAsync(BookingCancelled e, CancellationToken ct) =>
        ToUser(e.UserId, "Бронювання скасовано", e.Reason);

    public Task HandleAsync(EntryNeedsManualCheck e, CancellationToken ct) =>
        ToStaff("Потрібне ручне підтвердження",
            $"Смуга {e.LaneCode}: номер «{e.Plate ?? "—"}», впевненість {e.Confidence:P0}.");

    public Task HandleAsync(IncidentCreated e, CancellationToken ct) =>
        ToStaff($"Інцидент ({e.Priority})", e.Title);

    public Task HandleAsync(SpotStatusChanged e, CancellationToken ct) =>
        _realtime.SpotChangedAsync(e.ParkingId, new { e.SpotId, e.SpotCode, e.Status });

    public Task HandleAsync(BarrierOpened e, CancellationToken ct) =>
        _realtime.BarrierAsync(e.ParkingId, e.DeviceCode, "open");

    private async Task ToUser(Guid userId, string title, string text)
    {
        var n = new NotificationEntity { UserId = userId, Title = title, Text = text };
        _db.Notifications.Add(n);
        await _realtime.NotifyUserAsync(userId, new { n.Id, n.Title, n.Text, n.CreatedAt });
    }

    private async Task ToStaff(string title, string text)
    {
        var n = new NotificationEntity { UserId = null, Title = title, Text = text };
        _db.Notifications.Add(n);
        await _realtime.NotifyStaffAsync(new { n.Id, n.Title, n.Text, n.CreatedAt });
    }
}

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly AppDbContext _db;

    public NotificationsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> My()
    {
        var userId = User.GetUserId();
        var isStaff = Roles.Staff.Split(',').Any(User.IsInRole);
        return Ok(await _db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId || (isStaff && n.UserId == null))
            .OrderByDescending(n => n.CreatedAt).Take(50).ToListAsync());
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id)
    {
        var n = await _db.Notifications.FindAsync(id);
        if (n is null) return NotFound();
        n.IsRead = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
