namespace SmartPark.Data.Models;

/// <summary>Паркінг мережі (модуль Catalog, схема catalog).</summary>
public class Parking : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string Address { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string OpeningHours { get; set; } = "24/7";
    public ParkingStatus Status { get; set; } = ParkingStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Level> Levels { get; set; } = new();
}

public class Level : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingId { get; set; }
    public string Name { get; set; } = "";
    public int Number { get; set; }

    public Parking? Parking { get; set; }
    public List<Spot> Spots { get; set; } = new();
}

/// <summary>Паркомісце. Статус змінюють сенсори (через симулятор) або персонал.</summary>
public class Spot : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParkingId { get; set; }
    public Guid LevelId { get; set; }
    public string Code { get; set; } = "";
    public SpotType Type { get; set; } = SpotType.Regular;
    public SpotStatus Status { get; set; } = SpotStatus.Free;
    public string? StatusReason { get; set; }
    public DateTime StatusChangedAt { get; set; } = DateTime.UtcNow;

    public Level? Level { get; set; }
}
