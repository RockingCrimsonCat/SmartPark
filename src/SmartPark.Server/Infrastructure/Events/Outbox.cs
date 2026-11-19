using System.Text.Json;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;

namespace SmartPark.Server.Infrastructure.Events;

/// <summary>
/// Публікація подій через Transactional Outbox: подія додається до того ж
/// DbContext, що й зміна даних, і зберігається тим самим SaveChanges (одна транзакція).
/// </summary>
public interface IOutbox
{
    void Add(IntegrationEvent e);
}

public class Outbox : IOutbox
{
    private readonly AppDbContext _db;

    public Outbox(AppDbContext db) => _db = db;

    public void Add(IntegrationEvent e) =>
        _db.OutboxEvents.Add(new OutboxEvent
        {
            Id = e.EventId,
            Type = e.GetType().Name,
            Payload = JsonSerializer.Serialize(e, e.GetType(), EventSerializer.Options),
            OccurredAt = e.OccurredAt
        });
}

/// <summary>Серіалізація подій і пошук типу події за назвою.</summary>
public static class EventSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, Type> Types = typeof(IntegrationEvent).Assembly
        .GetTypes()
        .Where(t => t.IsSubclassOf(typeof(IntegrationEvent)) && !t.IsAbstract)
        .ToDictionary(t => t.Name);

    public static IntegrationEvent? Deserialize(string type, string payload) =>
        Types.TryGetValue(type, out var t)
            ? (IntegrationEvent?)JsonSerializer.Deserialize(payload, t, Options)
            : null;
}
