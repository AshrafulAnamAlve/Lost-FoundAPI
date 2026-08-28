namespace LostAndFoundApi.Models
{
    // What the item image classifier made of one photo.
    //
    // Every field is advisory. Classification is an optional enrichment: if the ML
    // service is down, slow or missing the model, the caller carries on without it
    // and the item is posted exactly as it would have been before this existed.
    public class ImageClassificationResult
    {
        // The predicted class as the model names it ("Laptop", "Mobile Phone",
        // "Watch", "Calculator"), or null when the model was not confident enough
        // to commit. Null is a normal outcome, not an error.
        public string? Label { get; set; }

        // Probability of the top class, whatever it was - reported even when Label
        // is null, so the UI can say *how* unsure the model is.
        public double Confidence { get; set; }

        // Confidence cleared the model's threshold (0.65, from models/config.json).
        public bool Known { get; set; }

        // The app category Label maps onto ("laptop", "phone", "jewelry",
        // "electronics"), or null when unknown. This is what gets stored and
        // compared, because the rest of the engine speaks in app categories.
        public string? Category { get; set; }

        // Full distribution, for the UI to show alternatives when the model is unsure.
        public Dictionary<string, double> Scores { get; set; } = new();

        // Set when classification could not be performed at all (service down,
        // model missing, unreadable image). Distinct from "ran, but unsure".
        public string? Error { get; set; }
    }
}
