using Microsoft.EntityFrameworkCore;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Events;
using SmartPark.Server.Modules.Catalog;

namespace SmartPark.Server.Modules.Device;

/// <summary>Модуль Device: стан пристроїв, телеметрія, команди шлагбаумам.</summary>
public class DeviceService
{
    private readonly AppDbContext _db;
    private readonly ICatalogService _catalog;
    private readonly IOutbox _outbox;

    public DeviceService(AppDbContext db, ICatalogService catalog, IOutbox outbox)
    {
        _db = db;
        _catalog = catalog;
        _outbox = outbox;
    }

    /// <summary>Подія сенсора місця: оновлює статус місця (якщо воно не заблоковане) і пише телеметрію.</summary>
    public async Task ApplySensorAsync(SpotSensorRequest r, CancellationToken ct = default)
    {
        if (await _db.ProcessedEvents.AnyAsync(p => p.EventId == r.EventId && p.Consumer == "edge-sensor", ct)) return;

        var spot = await _db.Spots.FirstOrDefaultAsync(s => s.ParkingId == r.ParkingId && s.Code == r.SpotCode, ct)
                   ?? throw new KeyNotFoundException($"Місце {r.SpotCode} не знайдено");
        var sensor = await _db.Devices.FirstOrDefaultAsync(d => d.ParkingId == r.ParkingId && d.SpotCode == r.SpotCode, ct);

        if (spot.Status is SpotStatus.Free or SpotStatus.Occupied or SpotStatus.Reserved)
        {
            var target = r.Occupied ? SpotStatus.Occupied : SpotStatus.Free;
            await _catalog.SetSpotStatusAsync(spot, target, null, ct);
        }

        if (sensor is not null)
        {
            sensor.LastHeartbeat = DateTime.UtcNow;
            _db.Telemetry.Add(new Telemetry
            {
                DeviceId = sensor.Id, Timestamp = r.MeasuredAt.ToUniversalTime(), Kind = "occupied",
                Value = r.Occupied ? "1" : "0"
            });
        }

        _db.ProcessedEvents.Add(new ProcessedEvent { EventId = r.EventId, Consumer = "edge-sensor" });
        await _db.SaveChangesAsync(ct);
    }

    public async Task HeartbeatAsync(HeartbeatRequest r, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var devices = await _db.Devices.Where(d => d.ParkingId == r.ParkingId && r.DeviceCodes.Contains(d.Code)).ToListAsync(ct);
        foreach (var d in devices)
        {
            d.LastHeartbeat = now;
            if (d.Status == DeviceStatus.Faulty) d.Status = DeviceStatus.Active; // пристрій знову на зв’язку
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Команди, які edge-шлюз ще не забрав. Після видачі позначаються як доставлені.</summary>
    public async Task<List<DeviceCommandDto>> TakePendingCommandsAsync(Guid parkingId, CancellationToken ct = default)
    {
        var commands = await _db.DeviceCommands
            .Where(c => c.ParkingId == parkingId && c.Status == CommandStatus.Pending)
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);
        commands.ForEach(c => c.Status = CommandStatus.Delivered);
        await _db.SaveChangesAsync(ct);
        return commands.Select(c => new DeviceCommandDto(c.Id, c.DeviceCode, c.Command, c.Reason)).ToList();
    }

    /// <summary>Ручне відкриття шлагбаума оператором (FR-15) – причина обов’язкова.</summary>
    public async Task OpenBarrierManuallyAsync(Guid deviceId, string reason, string operatorName, CancellationToken ct = default)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.Type == DeviceType.Barrier, ct)
                     ?? throw new KeyNotFoundException("Шлагбаум не знайдено");
        _db.DeviceCommands.Add(new DeviceCommand
        {
            ParkingId = device.ParkingId, DeviceCode = device.Code, Command = "open",
            Reason = reason, IssuedBy = operatorName
        });
        _outbox.Add(new BarrierOpened(device.ParkingId, device.Code, $"Вручну ({operatorName}): {reason}"));
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Фоновий моніторинг: пристрій без heartbeat понад 90 секунд вважається несправним,
/// публікується подія DeviceWentOffline (з неї створюється інцидент).
/// </summary>
public class DeviceMonitor : BackgroundService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DeviceMonitor> _log;

    public DeviceMonitor(IServiceScopeFactory scopes, ILogger<DeviceMonitor> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
                var limit = DateTime.UtcNow - Timeout;
                var silent = await db.Devices
                    .Where(d => d.Status == DeviceStatus.Active && d.Type != DeviceType.Sensor &&
                                d.LastHeartbeat != null && d.LastHeartbeat < limit)
                    .ToListAsync(ct);
                foreach (var d in silent)
                {
                    d.Status = DeviceStatus.Faulty;
                    outbox.Add(new DeviceWentOffline(d.Id, d.ParkingId, d.Code));
                }
                if (silent.Count > 0) await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Device monitor failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }
}
