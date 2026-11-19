namespace SmartPark.Data.Models;

/// <summary>Пристрій паркінгу (модуль Device).</summary>
public class Device : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingId { get; set; }
    public string Code { get; set; } = "";
    public DeviceType Type { get; set; }
    public string? SpotCode { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Active;
    public DateTime? LastHeartbeat { get; set; }
}

/// <summary>Запис телеметрії. Таблиця з індексом за часом замість TimescaleDB.</summary>
public class Telemetry
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Команда пристрою (наприклад, відкрити шлагбаум), яку забирає edge-шлюз.</summary>
public class DeviceCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingId { get; set; }
    public string DeviceCode { get; set; } = "";
    public string Command { get; set; } = "";
    public string Reason { get; set; } = "";
    public string IssuedBy { get; set; } = "";
    public CommandStatus Status { get; set; } = CommandStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Інцидент або заявка технікам (модуль Incident).</summary>
public class Incident : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public IncidentPriority Priority { get; set; } = IncidentPriority.Medium;
    public IncidentStatus Status { get; set; } = IncidentStatus.New;
    public Guid? AssigneeId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public List<IncidentComment> Comments { get; set; } = new();
}

public class IncidentComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Сповіщення в особистому кабінеті. UserId = null – для всього персоналу.</summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
