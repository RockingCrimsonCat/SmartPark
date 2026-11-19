using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Server.Infrastructure.Security;
using SmartPark.Server.Modules.Pricing;

namespace SmartPark.Server.Modules.Reporting;

public record DailyRevenueRow(DateTime Day, string Parking, int Sessions, decimal Revenue, double AvgMinutes);

public record OccupancyRow(string Parking, int Total, int Occupied, int Free, double OccupancyPercent);

/// <summary>
/// Модуль Reporting: звіти через SQL-представлення reporting.* (замість ClickHouse),
/// а також журнал аудиту та журнал подій.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Roles = Roles.Staff)]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReportsController(AppDbContext db) => _db = db;

    /// <summary>Дохід, кількість сесій і середня тривалість за днями (представлення reporting.daily_revenue).</summary>
    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var f = (from ?? DateTime.UtcNow.AddDays(-30)).AsUtc().Date;
        var t = (to ?? DateTime.UtcNow).AsUtc().Date.AddDays(1);
        var rows = await _db.Database.SqlQuery<DailyRevenueRow>($@"
            SELECT day AS ""Day"", parking AS ""Parking"", sessions AS ""Sessions"",
                   revenue AS ""Revenue"", avg_minutes AS ""AvgMinutes""
            FROM reporting.daily_revenue
            WHERE day >= {f} AND day < {t}
            ORDER BY day DESC, parking").ToListAsync();
        return Ok(rows);
    }

    /// <summary>Поточна зайнятість паркінгів (представлення reporting.current_occupancy).</summary>
    [HttpGet("occupancy")]
    public async Task<IActionResult> Occupancy() =>
        Ok(await _db.Database.SqlQuery<OccupancyRow>($@"
            SELECT parking AS ""Parking"", total AS ""Total"", occupied AS ""Occupied"",
                   free AS ""Free"", occupancy_percent AS ""OccupancyPercent""
            FROM reporting.current_occupancy ORDER BY parking").ToListAsync());

    /// <summary>KPI для дашборда.</summary>
    [HttpGet("kpi")]
    public async Task<IActionResult> Kpi()
    {
        var today = DateTime.UtcNow.Date;
        return Ok(new
        {
            activeSessions = await _db.Sessions.CountAsync(s => s.Status == Data.Models.SessionStatus.Active),
            sessionsToday = await _db.Sessions.CountAsync(s => s.EntryTime >= today),
            revenueToday = await _db.Payments.Where(p => p.CreatedAt >= today && p.Status == Data.Models.PaymentStatus.Succeeded)
                .SumAsync(p => (decimal?)p.Amount) ?? 0,
            openIncidents = await _db.Incidents.CountAsync(i => i.Status != Data.Models.IncidentStatus.Closed),
            faultyDevices = await _db.Devices.CountAsync(d => d.Status == Data.Models.DeviceStatus.Faulty),
            unpaidSessions = await _db.Sessions.CountAsync(s => s.Status == Data.Models.SessionStatus.Unpaid)
        });
    }

    /// <summary>Історія змін (UC18) – незмінний журнал аудиту.</summary>
    [HttpGet("audit")]
    [Authorize(Roles = Roles.SysAdmin + "," + Roles.ParkingAdmin)]
    public async Task<IActionResult> Audit([FromQuery] string? entityName, [FromQuery] string? entityId,
        [FromQuery] string? userName, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var q = _db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityName)) q = q.Where(a => a.EntityName == entityName);
        if (!string.IsNullOrWhiteSpace(entityId)) q = q.Where(a => a.EntityId == entityId);
        if (!string.IsNullOrWhiteSpace(userName)) q = q.Where(a => a.UserName!.Contains(userName));
        if (from is not null) q = q.Where(a => a.Timestamp >= from.Value.AsUtc());
        if (to is not null) q = q.Where(a => a.Timestamp < to.Value.AsUtc());
        return Ok(await q.OrderByDescending(a => a.Timestamp).Take(300).ToListAsync());
    }

    /// <summary>Журнал подій внутрішньої шини (таблиця outbox.OutboxEvents).</summary>
    [HttpGet("events")]
    public async Task<IActionResult> Events() =>
        Ok(await _db.OutboxEvents.AsNoTracking().OrderByDescending(e => e.OccurredAt).Take(100)
            .Select(e => new { e.Id, e.Type, e.Payload, e.OccurredAt, e.ProcessedAt, e.Attempts, e.Error })
            .ToListAsync());
}
