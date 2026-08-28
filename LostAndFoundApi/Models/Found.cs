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
    }
}
