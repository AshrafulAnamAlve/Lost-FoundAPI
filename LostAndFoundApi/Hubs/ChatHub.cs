using Microsoft.AspNetCore.SignalR;

namespace LostAndFoundApi.Hubs
{
    /// <summary>
    /// Lightweight push hub. Each connection joins a group named "user-{userId}"
    /// (userId comes from the connection query string) so the server can deliver
    /// a message to a specific user. Persistence is handled in MessagesController.
    /// </summary>
    public class ChatHub : Hub
    {
        public static string Group(int userId) => $"user-{userId}";

        public override async Task OnConnectedAsync()
        {
            var userId = Context.GetHttpContext()?.Request.Query["userId"].ToString();
            if (!string.IsNullOrEmpty(userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
            }
            await base.OnConnectedAsync();
        }

        // ── WebRTC call signaling (the hub only relays; media is peer-to-peer) ──

        public Task CallUser(int toUserId, int fromUserId, string fromName, string callType)
            => Clients.Group(Group(toUserId)).SendAsync("IncomingCall",
                   new { fromUserId, fromName, callType });

        public Task AcceptCall(int toUserId, int fromUserId)
            => Clients.Group(Group(toUserId)).SendAsync("CallAccepted", new { fromUserId });

        public Task RejectCall(int toUserId, int fromUserId)
            => Clients.Group(Group(toUserId)).SendAsync("CallRejected", new { fromUserId });

        public Task SendOffer(int toUserId, int fromUserId, string sdp)
            => Clients.Group(Group(toUserId)).SendAsync("ReceiveOffer", new { fromUserId, sdp });

        public Task SendAnswer(int toUserId, int fromUserId, string sdp)
            => Clients.Group(Group(toUserId)).SendAsync("ReceiveAnswer", new { fromUserId, sdp });

        public Task SendIce(int toUserId, int fromUserId, string candidate)
            => Clients.Group(Group(toUserId)).SendAsync("ReceiveIce", new { fromUserId, candidate });

        public Task EndCall(int toUserId, int fromUserId)
            => Clients.Group(Group(toUserId)).SendAsync("CallEnded", new { fromUserId });
    }
}
