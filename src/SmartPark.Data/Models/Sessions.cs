namespace SmartPark.Data.Models;

/// <summary>Транспортний засіб водія (модуль Session).</summary>
public class Vehicle : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string Plate { get; set; } = "";
    public string Brand { get; set; } = "";
    public bool IsElectric { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Сесія паркування: від в’їзду до виїзду.</summary>
public class ParkingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingId { get; set; }
    public string Plate { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public EntryMode Mode { get; set; }
    public Guid? BookingId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public decimal Amount { get; set; }
    public Guid? PaymentId { get; set; }
    public DateTime? PaidAt { get; set; }

    /// <summary>Ідентифікатор події в’їзду від edge-шлюзу – гарантує ідемпотентність.</summary>
    public Guid EntryEventId { get; set; }
    public Guid? ExitEventId { get; set; }
}

/// <summary>Абонемент на паркінг на період.</summary>
public class Subscription : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid ParkingId { get; set; }
    public string Plate { get; set; } = "";
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public decimal Price { get; set; }
    public Guid? PaymentId { get; set; }
}

public class BlacklistEntry : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Plate { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
