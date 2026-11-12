using LostAndFoundApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LostAndFoundApi.Hubs
{
    /// <summary>
    /// Lightweight push hub. Each connection joins a group named "user-{userId}" so
    /// the server can deliver a message to a specific user. The user id comes from
    /// the sign-in token (sent as access_token), never from the client, so nobody can
    /// listen in on another person's messages or calls. Persistence is handled in
    /// MessagesController.
    /// </summary>
    [Authorize]
    public class ChatHub : Hub
    {
        public static string Group(int userId) => $"user-{userId}";

        public override async Task OnConnectedAsync()
        {
            var userId = Context.User?.UserId() ?? 0;
            if (userId > 0)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, Group(userId));
            }
            await base.OnConnectedAsync();
        }

        // ── WebRTC call signaling (the hub only relays; media is peer-to-peer) ──

        // fromUserId is kept in the signatures so existing clients keep working, but
        // it is always replaced with the caller's real id.
        private int Me => Context.User?.UserId() ?? 0;

        public Task CallUser(int toUserId, int fromUserId, string fromName, string callType)
            => Clients.Group(Group(toUserId)).SendAsync("IncomingCall",
                   new { fromUserId = Me, fromName = Context.User?.Identity?.Name ?? fromName, callType });

        public Task AcceptCall(int toUserId, int fromUserId)
            => Clients.Group(Group(toUserId)).SendAsync("CallAccepted", new { fromUserId = Me });

        public Task RejectCall(int toUserId, int fromUserId)
            => Clients.Group(Group(toUserId)).SendAsync("CallRejected", new { fromUserId = Me });

        public Task SendOffer(int toUserId, int fromUserId, string sdp)
            => Clients.Group(Group(toUserId)).SendAsync("ReceiveOffer", new { fromUserId = Me, sdp });

        public Task SendAnswer(int toUserId, int fromUserId, string sdp)
            => Clients.Group(Group(toUserId)).SendAsync("ReceiveAnswer", new { fromUserId = Me, sdp });

        public Task SendIce(int toUserId, int fromUserId, string candidate)
            => Clients.Group(Group(toUserId)).SendAsync("ReceiveIce", new { fromUserId = Me, candidate });

        public Task EndCall(int toUserId, int fromUserId)
            => Clients.Group(Group(toUserId)).SendAsync("CallEnded", new { fromUserId = Me });
    }
}
