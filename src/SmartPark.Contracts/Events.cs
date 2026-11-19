namespace SmartPark.Contracts;

/// <summary>Базовий тип інтеграційної події внутрішньої шини.</summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public record SessionStarted(Guid SessionId, Guid ParkingId, string Plate, Guid? OwnerId, string Mode) : IntegrationEvent;

public record SessionClosed(Guid SessionId, Guid ParkingId, string Plate, Guid? OwnerId, decimal Amount, string Status) : IntegrationEvent;

public record EntryNeedsManualCheck(Guid ParkingId, string? Plate, double Confidence, string LaneCode) : IntegrationEvent;

public record SpotStatusChanged(Guid ParkingId, Guid SpotId, string SpotCode, string Status) : IntegrationEvent;

public record BarrierOpened(Guid ParkingId, string DeviceCode, string Reason) : IntegrationEvent;

public record BookingConfirmed(Guid BookingId, Guid UserId, Guid ParkingId, DateTime StartTime, DateTime EndTime) : IntegrationEvent;

public record BookingCancelled(Guid BookingId, Guid UserId, string Reason) : IntegrationEvent;

public record PaymentCompleted(Guid PaymentId, Guid? UserId, string Purpose, Guid ReferenceId, decimal Amount, bool Success) : IntegrationEvent;

public record DeviceWentOffline(Guid DeviceId, Guid ParkingId, string DeviceCode) : IntegrationEvent;

public record IncidentCreated(Guid IncidentId, Guid ParkingId, string Title, string Priority) : IntegrationEvent;
