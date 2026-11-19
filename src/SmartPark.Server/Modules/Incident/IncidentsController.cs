using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Events;
using SmartPark.Server.Infrastructure.Security;

namespace SmartPark.Server.Modules.Incident;

public record CreateIncidentRequest(Guid ParkingId, Guid? DeviceId, string Title, string Description, IncidentPriority Priority);

public record IncidentStatusRequest(IncidentStatus Status, string? Comment);

public record AssignRequest(Guid? AssigneeId);

public record CommentRequest(string Text);

/// <summary>Модуль Incident: інциденти та заявки технікам (UC12).</summary>
[ApiController]
[Route("api/incidents")]
[Authorize(Roles = Roles.Staff)]
public class IncidentsController : ControllerBase
{
    private static readonly Dictionary<IncidentStatus, IncidentStatus[]> Transitions = new()
    {
        [IncidentStatus.New] = new[] { IncidentStatus.InProgress, IncidentStatus.Closed },
        [IncidentStatus.InProgress] = new[] { IncidentStatus.Resolved },
        [IncidentStatus.Resolved] = new[] { IncidentStatus.Closed, IncidentStatus.InProgress },
        [IncidentStatus.Closed] = Array.Empty<IncidentStatus>()
    };

    private readonly AppDbContext _db;
    private readonly IOutbox _outbox;

    public IncidentsController(AppDbContext db, IOutbox outbox)
    {
        _db = db;
        _outbox = outbox;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] IncidentStatus? status, [FromQuery] Guid? parkingId)
    {
        var q = _db.Incidents.AsNoTracking();
        if (status is not null) q = q.Where(i => i.Status == status);
        if (parkingId is not null) q = q.Where(i => i.ParkingId == parkingId);
        return Ok(await q.OrderBy(i => i.Status).ThenByDescending(i => i.Priority).ThenByDescending(i => i.CreatedAt)
            .Take(200).ToListAsync());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var i = await _db.Incidents.AsNoTracking().Include(x => x.Comments.OrderBy(c => c.CreatedAt))
            .FirstOrDefaultAsync(x => x.Id == id);
        return i is null ? NotFound() : Ok(i);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateIncidentRequest r)
    {
        var i = new Data.Models.Incident
        {
            ParkingId = r.ParkingId, DeviceId = r.DeviceId, Title = r.Title, Description = r.Description, Priority = r.Priority
        };
        _db.Incidents.Add(i);
        _outbox.Add(new IncidentCreated(i.Id, i.ParkingId, i.Title, i.Priority.ToString()));
        await _db.SaveChangesAsync();
        return Ok(i);
    }

    /// <summary>Зміна статусу з перевіркою допустимих переходів: новий → у роботі → вирішено → закрито.</summary>
    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, IncidentStatusRequest r)
    {
        var i = await _db.Incidents.FindAsync(id);
        if (i is null) return NotFound();
        if (!Transitions[i.Status].Contains(r.Status))
            return BadRequest(new { error = $"Перехід {i.Status} → {r.Status} неможливий" });

        i.Status = r.Status;
        if (r.Status == IncidentStatus.Resolved) i.ResolvedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(r.Comment))
            _db.IncidentComments.Add(new IncidentComment { IncidentId = id, Author = User.Identity?.Name ?? "", Text = r.Comment });
        await _db.SaveChangesAsync();
        return Ok(i);
    }

    [HttpPut("{id:guid}/assignee")]
    [Authorize(Roles = Roles.Operator + "," + Roles.ParkingAdmin + "," + Roles.SysAdmin)]
    public async Task<IActionResult> Assign(Guid id, AssignRequest r)
    {
        var i = await _db.Incidents.FindAsync(id);
        if (i is null) return NotFound();
        i.AssigneeId = r.AssigneeId;
        if (r.AssigneeId is not null && i.Status == IncidentStatus.New) i.Status = IncidentStatus.InProgress;
        if (r.AssigneeId is not null)
            _db.Notifications.Add(new Data.Models.Notification { UserId = r.AssigneeId, Title = "Нова заявка", Text = i.Title });
        await _db.SaveChangesAsync();
        return Ok(i);
    }

    [HttpPost("{id:guid}/comments")]
    public async Task<IActionResult> Comment(Guid id, CommentRequest r)
    {
        if (!await _db.Incidents.AnyAsync(i => i.Id == id)) return NotFound();
        var c = new IncidentComment { IncidentId = id, Author = User.Identity?.Name ?? "", Text = r.Text };
        _db.IncidentComments.Add(c);
        await _db.SaveChangesAsync();
        return Ok(c);
    }
}

/// <summary>Обробник події DeviceWentOffline: автоматично створює інцидент, якщо відкритого ще немає.</summary>
public class DeviceOfflineHandler : IEventHandler<DeviceWentOffline>
{
    private readonly AppDbContext _db;
    private readonly IOutbox _outbox;

    public DeviceOfflineHandler(AppDbContext db, IOutbox outbox)
    {
        _db = db;
        _outbox = outbox;
    }

    public async Task HandleAsync(DeviceWentOffline e, CancellationToken ct)
    {
        var open = await _db.Incidents.AnyAsync(i => i.DeviceId == e.DeviceId && i.Status != IncidentStatus.Closed, ct);
        if (open) return; // повторна тривога об’єднується з відкритим інцидентом

        var incident = new Data.Models.Incident
        {
            ParkingId = e.ParkingId, DeviceId = e.DeviceId, Priority = IncidentPriority.High,
            Title = $"Пристрій {e.DeviceCode} не на зв’язку",
            Description = "Створено автоматично: немає heartbeat понад 90 секунд."
        };
        _db.Incidents.Add(incident);
        _outbox.Add(new IncidentCreated(incident.Id, incident.ParkingId, incident.Title, incident.Priority.ToString()));
    }
}
