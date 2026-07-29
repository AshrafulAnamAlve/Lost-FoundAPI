namespace LostAndFoundApi.Models
{
    // The full outcome of comparing a lost item with a found item: a score, a
    // human-friendly confidence label, and the per-field reasons behind it so the
    // UI can explain *why* the two items were matched.
    public class ItemMatchResult
    {
        public double Score { get; set; }
        public string Confidence { get; set; } = "Weak";
        public List<MatchReason> Reasons { get; set; } = new();
    }

    // A single explainable signal, e.g. "Brand matches: Apple" (match) or
    // "Colour differs" (mismatch).
    public class MatchReason
    {
        public string Field { get; set; } = "";
        public string Label { get; set; } = "";

        // "match" | "partial" | "mismatch" — drives the icon/colour in the UI.
        public string Status { get; set; } = "match";

        // Optional concrete value to show, e.g. the brand or colour name.
        public string? Detail { get; set; }
    }
}
