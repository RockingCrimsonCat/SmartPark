namespace SmartPark.Data.Models;

/// <summary>Бронювання місця на інтервал часу (модуль Booking).</summary>
public class Booking : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ParkingId { get; set; }
    public SpotType SpotType { get; set; } = SpotType.Regular;
    public string Plate { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.PendingPayment;
    public decimal Price { get; set; }
    public Guid? PaymentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Тариф паркінгу. Ціни зберігаються у версіях (модуль Pricing).</summary>
public class Tariff : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingId { get; set; }
    public string Name { get; set; } = "";
    public bool IsArchived { get; set; }
    public List<TariffVersion> Versions { get; set; } = new();
}

/// <summary>Незмінна версія тарифу з періодом дії.</summary>
public class TariffVersion : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TariffId { get; set; }
    public DateTime ValidFrom { get; set; }
    public decimal DayRate { get; set; }
    public decimal NightRate { get; set; }
    public int NightStartHour { get; set; } = 22;
    public int NightEndHour { get; set; } = 7;
    public int FreeMinutes { get; set; } = 15;
    public decimal DailyMax { get; set; }
    public decimal SubscriptionMonthPrice { get; set; }
    public string CreatedBy { get; set; } = "";
}

/// <summary>Платіж через фейковий платіжний провайдер (модуль Payment).</summary>
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public PaymentPurpose Purpose { get; set; }
    public Guid ReferenceId { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public string? ReceiptPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Refund : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
