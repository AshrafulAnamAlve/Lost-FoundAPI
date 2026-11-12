using System.ComponentModel.DataAnnotations;

namespace LostAndFoundApi.Models
{
    public class Lost
    {
        [Key]
        public int id { get; set; }
        public string type { get; set; }
        public string itemName { get; set; }
        public string category { get; set; }
        public string location { get; set; }
        public string description { get; set; }
        public DateTime dateLost { get; set; }
        public string? timeLost { get; set; }
        public string? brand { get; set; }
        public string? color { get; set; }
        // Optional in the form. Non-nullable in the database, so ASP.NET Core would
        // otherwise treat it as required and reject any report sent without one with
        // a bare 400. The controller stores "" in its place.
        [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
        public string Reward { get; set; } = "";
        public string userName { get; set; }
        public string email { get; set; }
        public string phoneNumber { get; set; }
        public string? altContact { get; set; }
        public int userId { get;set; }
        public string? imageUrl { get; set; }

        // What the image classifier made of the photo, as an app category
        // ("laptop", "phone", ...). Kept separate from `category` above, which is
        // the user's own choice: that one drives a hard gate in the matching engine
        // (a cross-category pair is multiplied by 0.20), and a model that is right
        // ~88% of the time must never be able to veto a real match. Null when there
        // is no photo, the classifier was unavailable, or it was not confident.
        public string? detectedCategory { get; set; }

        // Confidence behind detectedCategory, 0..1. Stored so a low-confidence
        // guess can be told apart from a certain one after the fact.
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
