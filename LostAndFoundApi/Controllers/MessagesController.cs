using LostAndFoundApi.Hubs;
using LostAndFoundApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LostAndFoundApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MessagesController : ControllerBase
    {
        private readonly AppDbContext context;
        private readonly IHubContext<ChatHub> hub;

        public MessagesController(AppDbContext context, IHubContext<ChatHub> hub)
        {
            this.context = context;
            this.hub = hub;
        }

        public class SendDto
        {
            public int senderId { get; set; }
            public int receiverId { get; set; }
            public string content { get; set; } = "";
            public int? itemId { get; set; }
            public string? itemType { get; set; }
            public string? itemName { get; set; }
        }

        [HttpPost("Send")]
        public async Task<ActionResult> Send(SendDto dto)
        {
            if (dto == null || dto.senderId <= 0 || dto.receiverId <= 0 || string.IsNullOrWhiteSpace(dto.content))
                return BadRequest("Invalid message.");

            var msg = new Message
            {
                senderId = dto.senderId,
                receiverId = dto.receiverId,
                content = dto.content.Trim(),
                sentAt = DateTime.UtcNow,
                isRead = false,
                itemId = dto.itemId,
                itemType = dto.itemType,
                itemName = dto.itemName
            };
            context.Messages.Add(msg);
            await context.SaveChangesAsync();

            // Push to the receiver (and echo to the sender's other tabs) in real time.
            await hub.Clients.Group(ChatHub.Group(dto.receiverId)).SendAsync("ReceiveMessage", msg);
            await hub.Clients.Group(ChatHub.Group(dto.senderId)).SendAsync("ReceiveMessage", msg);

            return Ok(msg);
        }

        [HttpGet("Conversation/{userId}/{otherUserId}")]
        public async Task<ActionResult> Conversation(int userId, int otherUserId)
        {
            var messages = context.Messages
                .Where(m => (m.senderId == userId && m.receiverId == otherUserId)
                         || (m.senderId == otherUserId && m.receiverId == userId))
                .OrderBy(m => m.sentAt)
                .ToList();

            // Mark messages addressed to me as read.
            var unread = messages.Where(m => m.receiverId == userId && !m.isRead).ToList();
            if (unread.Count > 0)
            {
                unread.ForEach(m => m.isRead = true);
                await context.SaveChangesAsync();
            }

            return Ok(messages);
        }

        [HttpGet("Conversations/{userId}")]
        public ActionResult Conversations(int userId)
        {
            var mine = context.Messages
                .Where(m => m.senderId == userId || m.receiverId == userId)
                .ToList();

            var groups = mine
                .GroupBy(m => m.senderId == userId ? m.receiverId : m.senderId)
                .Select(g =>
                {
                    var last = g.OrderByDescending(m => m.sentAt).First();
                    var partner = context.Registers.FirstOrDefault(r => r.id == g.Key);
                    var name = partner == null
                        ? "User"
                        : $"{partner.FirstName} {partner.LastName}".Trim();
                    return new
                    {
                        partnerId = g.Key,
                        partnerName = name,
                        lastMessage = last.content,
                        lastAt = last.sentAt,
                        unread = g.Count(m => m.receiverId == userId && !m.isRead),
                        itemName = last.itemName
                    };
                })
                .OrderByDescending(c => c.lastAt)
                .ToList();

            return Ok(groups);
        }

        [HttpGet("UnreadCount/{userId}")]
        public ActionResult UnreadCount(int userId)
        {
            var count = context.Messages.Count(m => m.receiverId == userId && !m.isRead);
            return Ok(new { count });
        }
    }
}
