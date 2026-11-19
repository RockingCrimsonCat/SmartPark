namespace SmartPark.Contracts;

// Контракти взаємодії edge-шлюзу (симулятора паркінгу) із сервером.
// Спільні для SmartPark.Server і SmartPark.Simulator.

/// <summary>Подія «автомобіль біля смуги в’їзду/виїзду», яку надсилає камера LPR.</summary>
public record VehicleDetectedRequest(
    Guid EventId,
    Guid ParkingId,
    string? Plate,
    double Confidence,
    string LaneCode,
    DateTime DetectedAt);

public enum GateDecisionType { Allow, Deny, ManualCheck, PaymentRequired }

/// <summary>Рішення сервера для шлагбаума.</summary>
public record GateDecision(
    GateDecisionType Decision,
    string Message,
    Guid? SessionId = null,
    decimal? Amount = null,
    string? PaymentUrl = null);

/// <summary>Стан сенсора паркомісця.</summary>
public record SpotSensorRequest(Guid EventId, Guid ParkingId, string SpotCode, bool Occupied, DateTime MeasuredAt);

public record HeartbeatRequest(Guid ParkingId, IReadOnlyList<string> DeviceCodes);

/// <summary>Команда для пристрою (наприклад, open-barrier).</summary>
public record DeviceCommandDto(Guid Id, string DeviceCode, string Command, string Reason);

/// <summary>Локальний кеш edge-шлюзу для автономного режиму.</summary>
public record EdgeCache(
    Guid ParkingId,
    IReadOnlyList<string> AllowedPlates,
    IReadOnlyList<string> Blacklist,
    DateTime GeneratedAt);

/// <summary>Подія, прийнята edge-шлюзом в автономному режимі.</summary>
public record OfflineEvent(Guid EventId, string Kind, string Plate, string LaneCode, DateTime OccurredAt);

public record SyncBatchRequest(Guid ParkingId, IReadOnlyList<OfflineEvent> Events);

public record SyncBatchResult(int Accepted, int Duplicates, int Failed);
