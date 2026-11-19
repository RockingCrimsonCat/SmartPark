using Microsoft.AspNetCore.Mvc;
using SmartPark.Contracts;
using SmartPark.Server.Infrastructure.Security;
using SmartPark.Server.Modules.Session;

namespace SmartPark.Server.Modules.Device;

/// <summary>
/// REST API для edge-шлюзу паркінгу (у прототипі – симулятора).
/// Автентифікація за ключем у заголовку X-Edge-Key.
/// </summary>
[ApiController]
[Route("api/edge")]
[EdgeKey]
public class EdgeController : ControllerBase
{
    private readonly SessionService _sessions;
    private readonly DeviceService _devices;

    public EdgeController(SessionService sessions, DeviceService devices)
    {
        _sessions = sessions;
        _devices = devices;
    }

    /// <summary>Камера на в’їзді зафіксувала автомобіль.</summary>
    [HttpPost("entries")]
    public async Task<GateDecision> Entry(VehicleDetectedRequest r, CancellationToken ct) =>
        await _sessions.ProcessEntryAsync(r, ct);

    /// <summary>Камера на виїзді зафіксувала автомобіль.</summary>
    [HttpPost("exits")]
    public async Task<GateDecision> Exit(VehicleDetectedRequest r, CancellationToken ct) =>
        await _sessions.ProcessExitAsync(r, ct);

    [HttpPost("sensors")]
    public async Task<IActionResult> Sensor(SpotSensorRequest r, CancellationToken ct)
    {
        await _devices.ApplySensorAsync(r, ct);
        return NoContent();
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(HeartbeatRequest r, CancellationToken ct)
    {
        await _devices.HeartbeatAsync(r, ct);
        return NoContent();
    }

    [HttpGet("commands")]
    public Task<List<DeviceCommandDto>> Commands([FromQuery] Guid parkingId, CancellationToken ct) =>
        _devices.TakePendingCommandsAsync(parkingId, ct);

    /// <summary>Кеш для автономного режиму.</summary>
    [HttpGet("cache")]
    public Task<EdgeCache> Cache([FromQuery] Guid parkingId, CancellationToken ct) =>
        _sessions.BuildCacheAsync(parkingId, ct);

    /// <summary>Досилання подій, накопичених під час обриву зв’язку.</summary>
    [HttpPost("sync")]
    public Task<SyncBatchResult> Sync(SyncBatchRequest r, CancellationToken ct) =>
        _sessions.SyncOfflineAsync(r, ct);
}
