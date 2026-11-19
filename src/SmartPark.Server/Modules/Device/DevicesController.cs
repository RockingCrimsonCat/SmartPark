using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Security;

namespace SmartPark.Server.Modules.Device;

public record DeviceRequest(Guid ParkingId, string Code, DeviceType Type, string? SpotCode);

public record DeviceStatusRequest(DeviceStatus Status);

public record OpenBarrierRequest(string Reason);

[ApiController]
[Route("api/devices")]
[Authorize(Roles = Roles.Staff)]
public class DevicesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DeviceService _devices;

    public DevicesController(AppDbContext db, DeviceService devices)
    {
        _db = db;
        _devices = devices;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? parkingId, [FromQuery] bool includeSensors = false)
    {
        var q = _db.Devices.AsNoTracking();
        if (parkingId is not null) q = q.Where(d => d.ParkingId == parkingId);
        if (!includeSensors) q = q.Where(d => d.Type != DeviceType.Sensor);
        return Ok(await q.OrderBy(d => d.Code).ToListAsync());
    }

    [HttpPost]
    [Authorize(Roles = Roles.Network)]
    public async Task<IActionResult> Create(DeviceRequest r)
    {
        var d = new Data.Models.Device { ParkingId = r.ParkingId, Code = r.Code, Type = r.Type, SpotCode = r.SpotCode };
        _db.Devices.Add(d);
        await _db.SaveChangesAsync();
        return Ok(d);
    }

    /// <summary>Зміна статусу пристрою (активний, на обслуговуванні, несправний, виведений).</summary>
    [HttpPut("{id:guid}/status")]
    [Authorize(Roles = Roles.Technician + "," + Roles.ParkingAdmin + "," + Roles.Operator + "," + Roles.SysAdmin)]
    public async Task<IActionResult> SetStatus(Guid id, DeviceStatusRequest r)
    {
        var d = await _db.Devices.FindAsync(id);
        if (d is null) return NotFound();
        d.Status = r.Status;
        await _db.SaveChangesAsync();
        return Ok(d);
    }

    /// <summary>Дистанційне відкриття шлагбаума оператором.</summary>
    [HttpPost("{id:guid}/open")]
    [Authorize(Roles = Roles.Operator + "," + Roles.ParkingAdmin + "," + Roles.SysAdmin)]
    public async Task<IActionResult> Open(Guid id, OpenBarrierRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return BadRequest(new { error = "Вкажіть причину" });
        await _devices.OpenBarrierManuallyAsync(id, r.Reason, User.Identity?.Name ?? "operator");
        return NoContent();
    }

    /// <summary>Останні записи телеметрії пристрою.</summary>
    [HttpGet("{id:guid}/telemetry")]
    public async Task<IActionResult> Telemetry(Guid id) =>
        Ok(await _db.Telemetry.AsNoTracking().Where(t => t.DeviceId == id)
            .OrderByDescending(t => t.Timestamp).Take(100).ToListAsync());
}
