using System.ComponentModel.DataAnnotations;

namespace LostAndFoundApi.Models
{
    public class Message
    {
        [Key]
        public int id { get; set; }
        public int senderId { get; set; }
        public int receiverId { get; set; }
        public string content { get; set; }
        public DateTime sentAt { get; set; }
        public bool isRead { get; set; }

        // Optional context: which matched item the conversation started from
        public int? itemId { get; set; }
        public string? itemType { get; set; }
        public string? itemName { get; set; }
    }
}
