using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading.Channels;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.FocusSync;

[Flags]
public enum SyncChangeSections { Focus = 1, Websites = 2, All = Focus | Websites }

public sealed class SyncNotifications
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Dictionary<Guid, Subscription>> subscribers = new();
    private sealed class Subscription(Guid sessionId)
    {
        public Guid SessionId { get; } = sessionId;
        public int Pending;
        public Channel<bool> Signal { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    }

    public void Publish(Guid userId, Guid sessionId, SyncChangeSections sections)
    {
        lock (gate)
        {
            if (!subscribers.TryGetValue(userId, out var devices)) return;
            foreach (var subscriber in devices.Values)
            {
                if (subscriber.SessionId == sessionId) continue;
                Interlocked.Or(ref subscriber.Pending, (int)sections);
                subscriber.Signal.Writer.TryWrite(true);
            }
        }
    }

    public async Task WatchAsync(Guid userId, Guid sessionId, Func<CancellationToken, Task> ready,
        Func<SyncChangeSections, CancellationToken, Task> notify, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var subscriber = new Subscription(sessionId);
        lock (gate)
        {
            if (!subscribers.TryGetValue(userId, out var devices)) subscribers.Add(userId, devices = new());
            devices.Add(id, subscriber);
        }
        try
        {
            await ready(token);
            await foreach (var _ in subscriber.Signal.Reader.ReadAllAsync(token))
            {
                var pending = Interlocked.Exchange(ref subscriber.Pending, 0);
                if (pending != 0) await notify((SyncChangeSections)pending, token);
            }
        }
        finally
        {
            lock (gate)
            {
                var devices = subscribers[userId];
                devices.Remove(id);
                if (devices.Count == 0) subscribers.Remove(userId);
            }
        }
    }
}

[ApiController, Authorize]
public sealed class SyncNotificationsController(SyncNotifications notifications, IServiceScopeFactory scopes, TimeProvider clock)
    : ControllerBase
{
    [HttpGet("api/v1/sync/notifications")]
    public async Task Watch([FromQuery] bool sections = false)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var sessionId = Guid.Parse(User.FindFirstValue("sid")!);
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        // Keep idle sockets bounded. Reconnection catches up using the saved cursors.
        stop.CancelAfter(TimeSpan.FromHours(4));
        var receiving = WatchCloseAsync(socket, stop);
        try
        {
            await notifications.WatchAsync(userId, sessionId,
                token => socket.SendAsync(new ArraySegment<byte>("ready"u8.ToArray()), WebSocketMessageType.Text, true, token),
                async (changed, token) =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var now = clock.GetUtcNow();
                    var authorized = await (from session in db.LoginSessions
                        join user in db.Users on session.UserId equals user.Id
                        where session.Id == sessionId && user.Id == userId &&
                            session.RevokedAtUtc == null && session.ExpiresAtUtc > now &&
                            session.SecurityStamp == user.SecurityStamp && user.ClosedAtUtc == null &&
                            !(user.LockoutEnabled && user.LockoutEnd == DateTimeOffset.MaxValue)
                        select session.Id).AnyAsync(token);
                    if (!authorized) { await stop.CancelAsync(); return; }
                    var message = sections ? changed switch
                    {
                        SyncChangeSections.Focus => FocusChanged,
                        SyncChangeSections.Websites => WebsitesChanged,
                        _ => AllChanged
                    } : AllChanged;
                    await socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, token);
                }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            await stop.CancelAsync();
            await receiving;
        }
    }

    private static readonly byte[] FocusChanged = "focus"u8.ToArray();
    private static readonly byte[] WebsitesChanged = "websites"u8.ToArray();
    private static readonly byte[] AllChanged = "changed"u8.ToArray();

    private static async Task WatchCloseAsync(WebSocket socket, CancellationTokenSource stop)
    {
        try { await socket.ReceiveAsync(new ArraySegment<byte>(new byte[16]), stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally { await stop.CancelAsync(); }
    }
}
