using Microsoft.AspNetCore.Identity;

namespace SmartPark.Data.Models;

/// <summary>Маркер сутностей, зміни яких пишуться в журнал аудиту.</summary>
public interface IAuditable
{
    Guid Id { get; }
}

/// <summary>Користувач системи (модуль Identity, таблиці ASP.NET Core Identity).</summary>
public class AppUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = "";
    public bool HasPaymentMethod { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Прив’язка працівника до паркінгів, якими він керує.</summary>
public class UserParkingScope
{
    public Guid UserId { get; set; }
    public Guid ParkingId { get; set; }
}

/// <summary>Запис журналу аудиту (лише додавання).</summary>
public class AuditLog
{
    public long Id { get; set; }
    public string EntityName { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? UserName { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>Подія в таблиці Outbox: записується в одній транзакції зі зміною даних.</summary>
public class OutboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

/// <summary>Оброблені події/запити – для ідемпотентності.</summary>
public class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = "";
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
