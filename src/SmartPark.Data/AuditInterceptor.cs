using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartPark.Data.Models;

namespace SmartPark.Data;

/// <summary>Повертає ім’я користувача, від імені якого виконується запит.</summary>
public interface ICurrentUser
{
    Guid? Id { get; }
    string Name { get; }
}

/// <summary>
/// Перехоплювач SaveChanges: для кожної зміненої сутності IAuditable
/// записує в audit.AuditLog дію, старі й нові значення полів та автора.
/// </summary>
public class AuditInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUser _user;

    public AuditInterceptor(ICurrentUser user) => _user = user;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is not null) AddAuditRecords(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) AddAuditRecords(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void AddAuditRecords(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        var records = new List<AuditLog>();

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var (oldValues, newValues) = CollectValues(entry);
            if (entry.State == EntityState.Modified && newValues.Count == 0) continue;

            records.Add(new AuditLog
            {
                EntityName = entry.Entity.GetType().Name,
                EntityId = entry.Entity.Id.ToString(),
                Action = entry.State switch
                {
                    EntityState.Added => "Created",
                    EntityState.Deleted => "Deleted",
                    _ => "Updated"
                },
                OldValues = oldValues.Count > 0 ? JsonSerializer.Serialize(oldValues) : null,
                NewValues = newValues.Count > 0 ? JsonSerializer.Serialize(newValues) : null,
                UserName = _user.Name,
                Timestamp = DateTime.UtcNow
            });
        }

        context.Set<AuditLog>().AddRange(records);
    }

    private static (Dictionary<string, object?> oldValues, Dictionary<string, object?> newValues) CollectValues(
        EntityEntry entry)
    {
        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();

        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            switch (entry.State)
            {
                case EntityState.Added:
                    newValues[name] = p.CurrentValue;
                    break;
                case EntityState.Deleted:
                    oldValues[name] = p.OriginalValue;
                    break;
                case EntityState.Modified when p.IsModified && !Equals(p.OriginalValue, p.CurrentValue):
                    oldValues[name] = p.OriginalValue;
                    newValues[name] = p.CurrentValue;
                    break;
            }
        }

        return (oldValues, newValues);
    }
}
