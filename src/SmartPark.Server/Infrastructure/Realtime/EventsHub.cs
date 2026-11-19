using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SmartPark.Server.Infrastructure.Security;

namespace SmartPark.Server.Infrastructure.Realtime;

/// <summary>
/// SignalR-хаб подій у реальному часі. Групи: staff (персонал),
/// user:{id} (особисті сповіщення), parking:{id} (схема паркінгу).
/// </summary>
public class EventsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{user.GetUserId()}");
            if (Roles.Staff.Split(',').Any(user.IsInRole))
                await Groups.AddToGroupAsync(Context.ConnectionId, "staff");
        }
        await base.OnConnectedAsync();
    }

    [AllowAnonymous]
    public Task WatchParking(Guid parkingId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"parking:{parkingId}");

    [AllowAnonymous]
    public Task UnwatchParking(Guid parkingId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"parking:{parkingId}");
}

/// <summary>Відправлення повідомлень у SignalR.</summary>
public interface IRealtime
{
    Task EventLogAsync(string type, string payload, DateTime occurredAt);
    Task SpotChangedAsync(Guid parkingId, object spot);
    Task BarrierAsync(Guid parkingId, string deviceCode, string state);
    Task NotifyUserAsync(Guid userId, object notification);
    Task NotifyStaffAsync(object notification);
}

public class SignalRRealtime : IRealtime
{
    private readonly IHubContext<EventsHub> _hub;

    public SignalRRealtime(IHubContext<EventsHub> hub) => _hub = hub;

    public Task EventLogAsync(string type, string payload, DateTime occurredAt) =>
        _hub.Clients.Group("staff").SendAsync("event", new { type, payload, occurredAt });

    public Task SpotChangedAsync(Guid parkingId, object spot) =>
        _hub.Clients.Group($"parking:{parkingId}").SendAsync("spot", spot);

    public Task BarrierAsync(Guid parkingId, string deviceCode, string state) =>
        _hub.Clients.Groups($"parking:{parkingId}", "staff").SendAsync("barrier", new { parkingId, deviceCode, state });

    public Task NotifyUserAsync(Guid userId, object notification) =>
        _hub.Clients.Group($"user:{userId}").SendAsync("notification", notification);

    public Task NotifyStaffAsync(object notification) =>
        _hub.Clients.Group("staff").SendAsync("notification", notification);
}
