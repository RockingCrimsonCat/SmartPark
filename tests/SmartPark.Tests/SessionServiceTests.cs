using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Events;
using SmartPark.Server.Modules.Catalog;
using SmartPark.Server.Modules.Payment;
using SmartPark.Server.Modules.Pricing;
using SmartPark.Server.Modules.Session;
using Xunit;

namespace SmartPark.Tests;

/// <summary>Перевірка логіки в’їзду на БД у пам’яті (EF Core InMemory).</summary>
public class SessionServiceTests
{
    private readonly AppDbContext _db;
    private readonly SessionService _service;
    private readonly Guid _parkingId = Guid.NewGuid();

    public SessionServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payment:FailureRate"] = "0",
            ["Storage:ReceiptsPath"] = Path.Combine(Path.GetTempPath(), "sp-tests")
        }).Build();

        var outbox = new Outbox(_db);
        var catalog = new CatalogService(_db, new MemoryCache(new MemoryCacheOptions()), outbox);
        var pricing = new PricingService(_db);
        var payments = new PaymentService(_db, new FakePaymentProvider(config), new ReceiptService(config), outbox);
        _service = new SessionService(_db, catalog, pricing, payments, outbox, config);

        var parking = new Parking { Id = _parkingId, Name = "Тестовий", Status = ParkingStatus.Active };
        var level = new Level { ParkingId = _parkingId, Name = "1", Number = 1 };
        level.Spots.Add(new Spot { ParkingId = _parkingId, Code = "1-01" });
        level.Spots.Add(new Spot { ParkingId = _parkingId, Code = "1-02" });
        parking.Levels.Add(level);
        _db.Parkings.Add(parking);
        _db.Blacklist.Add(new BlacklistEntry { Plate = "BX6666XX", Reason = "тест" });
        _db.SaveChanges();
    }

    private VehicleDetectedRequest Request(string? plate, double confidence = 0.95, Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), _parkingId, plate, confidence, "BAR-IN-1", DateTime.UtcNow);

    [Fact]
    public async Task Guest_IsAllowed_AndSessionOpened()
    {
        var decision = await _service.ProcessEntryAsync(Request("AA0001AA"));

        Assert.Equal(GateDecisionType.Allow, decision.Decision);
        Assert.Equal(1, await _db.Sessions.CountAsync(s => s.Status == SessionStatus.Active));
        Assert.True(await _db.OutboxEvents.AnyAsync(e => e.Type == nameof(SessionStarted)));
        Assert.True(await _db.DeviceCommands.AnyAsync(c => c.Command == "open"));
    }

    [Fact]
    public async Task SameEvent_IsProcessedOnce()
    {
        var eventId = Guid.NewGuid();
        await _service.ProcessEntryAsync(Request("AA0002AA", eventId: eventId));
        var repeated = await _service.ProcessEntryAsync(Request("AA0002AA", eventId: eventId));

        Assert.Equal(GateDecisionType.Allow, repeated.Decision);
        Assert.Equal(1, await _db.Sessions.CountAsync());
    }

    [Fact]
    public async Task LowConfidence_RequiresManualCheck()
    {
        var decision = await _service.ProcessEntryAsync(Request("AA0003AA", confidence: 0.4));

        Assert.Equal(GateDecisionType.ManualCheck, decision.Decision);
        Assert.True(await _db.OutboxEvents.AnyAsync(e => e.Type == nameof(EntryNeedsManualCheck)));
        Assert.Equal(0, await _db.Sessions.CountAsync());
    }

    [Fact]
    public async Task BlacklistedPlate_IsDenied()
    {
        var decision = await _service.ProcessEntryAsync(Request("вх 6666 хх"));
        Assert.Equal(GateDecisionType.Deny, decision.Decision);
    }

    [Fact]
    public async Task FullParking_DeniesGuests()
    {
        await _service.ProcessEntryAsync(Request("AA0004AA"));
        await _service.ProcessEntryAsync(Request("AA0005AA"));
        var third = await _service.ProcessEntryAsync(Request("AA0006AA"));

        Assert.Equal(GateDecisionType.Deny, third.Decision);
    }
}
