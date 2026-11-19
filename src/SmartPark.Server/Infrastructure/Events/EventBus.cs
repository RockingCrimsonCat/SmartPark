using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Realtime;

namespace SmartPark.Server.Infrastructure.Events;

/// <summary>Обробник події внутрішньої шини.</summary>
public interface IEventHandler<in T> where T : IntegrationEvent
{
    Task HandleAsync(T e, CancellationToken ct);
}

/// <summary>Внутрішня шина подій на основі System.Threading.Channels.</summary>
public class InProcessEventBus
{
    private readonly Channel<OutboxEvent> _channel = Channel.CreateBounded<OutboxEvent>(1000);

    public ChannelReader<OutboxEvent> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(OutboxEvent e, CancellationToken ct) => _channel.Writer.WriteAsync(e, ct);
}

/// <summary>
/// Фонова служба Outbox: читає неопубліковані події з таблиці outbox.OutboxEvents
/// і передає їх у внутрішню шину. Після перезапуску сервера події дочитуються з БД.
/// </summary>
public class OutboxProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly InProcessEventBus _bus;
    private readonly ILogger<OutboxProcessor> _log;
    private readonly HashSet<Guid> _inFlight = new();
    private readonly object _lock = new();

    public OutboxProcessor(IServiceScopeFactory scopes, InProcessEventBus bus, ILogger<OutboxProcessor> log)
    {
        _scopes = scopes;
        _bus = bus;
        _log = log;
    }

    public void MarkDone(Guid id)
    {
        lock (_lock) _inFlight.Remove(id);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pending = await db.OutboxEvents.AsNoTracking()
                    .Where(x => x.ProcessedAt == null && x.Attempts < 5)
                    .OrderBy(x => x.OccurredAt)
                    .Take(100)
                    .ToListAsync(ct);

                foreach (var e in pending)
                {
                    lock (_lock)
                    {
                        if (!_inFlight.Add(e.Id)) continue;
                    }
                    await _bus.EnqueueAsync(e, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Outbox polling failed");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }
}

/// <summary>
/// Диспетчер подій: викликає всі обробники IEventHandler&lt;T&gt;, перевіряючи ідемпотентність
/// за таблицею outbox.ProcessedEvents, транслює подію в SignalR і позначає її обробленою.
/// </summary>
public class EventDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly InProcessEventBus _bus;
    private readonly OutboxProcessor _outbox;
    private readonly ILogger<EventDispatcher> _log;

    public EventDispatcher(IServiceScopeFactory scopes, InProcessEventBus bus, OutboxProcessor outbox,
        ILogger<EventDispatcher> log)
    {
        _scopes = scopes;
        _bus = bus;
        _outbox = outbox;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var record in _bus.Reader.ReadAllAsync(ct))
        {
            try
            {
                await DispatchAsync(record, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Event {Type} {Id} failed", record.Type, record.Id);
                await MarkFailedAsync(record.Id, ex.Message, ct);
            }
            finally
            {
                _outbox.MarkDone(record.Id);
            }
        }
    }

    private async Task DispatchAsync(OutboxEvent record, CancellationToken ct)
    {
        var e = EventSerializer.Deserialize(record.Type, record.Payload)
                ?? throw new InvalidOperationException($"Unknown event type {record.Type}");

        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var handlerType = typeof(IEventHandler<>).MakeGenericType(e.GetType());

        foreach (var handler in sp.GetServices(handlerType))
        {
            if (handler is null) continue;
            var consumer = handler.GetType().Name;
            var done = await db.ProcessedEvents.AnyAsync(x => x.EventId == e.EventId && x.Consumer == consumer, ct);
            if (done) continue; // повторна доставка – ігноруємо

            var task = (Task)handlerType.GetMethod("HandleAsync")!.Invoke(handler, new object[] { e, ct })!;
            await task;

            db.ProcessedEvents.Add(new ProcessedEvent { EventId = e.EventId, Consumer = consumer });
            await db.SaveChangesAsync(ct);
        }

        var stored = await db.OutboxEvents.FirstAsync(x => x.Id == record.Id, ct);
        stored.ProcessedAt = DateTime.UtcNow;
        stored.Attempts++;
        await db.SaveChangesAsync(ct);

        await sp.GetRequiredService<IRealtime>().EventLogAsync(record.Type, record.Payload, record.OccurredAt);
        _log.LogInformation("Event {Type} {Id} dispatched", record.Type, record.Id);
    }

    private async Task MarkFailedAsync(Guid id, string error, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.OutboxEvents.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (stored is null) return;
        stored.Attempts++;
        stored.Error = error;
        await db.SaveChangesAsync(ct);
    }
}
