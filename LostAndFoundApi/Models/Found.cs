using System.ComponentModel.DataAnnotations;

namespace LostAndFoundApi.Models
{
    public class Found
    {
        [Key]
        public int id { get; set; }
        public string type { get; set; }
        public string itemName { get; set; }
        public string category { get; set; }
        public string location { get; set; }
        public string description { get; set; }
        public DateTime dateFound { get; set; }
        public string? timeLost { get; set; }
        public string? brand { get; set; }
        public string? color { get; set; }
        public string userName { get; set; }
        public string email { get; set; }
        public string phoneNumber { get; set; }
        public string? altContact { get; set; }
        public int userId { get;set; }
        public string? imageUrl { get; set; }

        // See the note on Lost.detectedCategory - deliberately separate from the
        // user-chosen `category`, which drives a hard gate the model must not reach.
        public string? detectedCategory { get; set; }

        public double? detectedConfidence { get; set; }

        // ── Resolution ─────────────────────────────────────────────────────────
        //
        // Null on every report filed before this shipped, which reads as "open" —
        // so nothing about existing rows changes. Set once the item is actually
        // back with its owner.
        public string? status { get; set; }

        public DateTime? resolvedAt { get; set; }

        // Who it was settled with, when the user says so. Both stay null when
        // somebody resolves an item without naming anyone, which is allowed: the
        // matching engine is not always right, and an item can come back through
        // a friend, a notice board or the front desk.
        public int? resolvedWithUserId { get; set; }
        public int? resolvedWithItemId { get; set; }

        /// <summary>Whatever the user wants on the record, e.g. "handed over at the front desk".</summary>
        public string? resolvedNote { get; set; }
    }
}
