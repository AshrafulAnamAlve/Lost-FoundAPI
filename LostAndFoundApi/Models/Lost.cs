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
        public string Reward { get; set; }
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
    }
}
