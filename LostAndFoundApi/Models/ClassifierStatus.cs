namespace LostAndFoundApi.Models
{
    // Which image classifier the API is using and whether it can answer, for
    // /api/health.
    //
    // This exists because classification fails silently by design: an item posts
    // exactly the same way whether the model ran or not, so from the outside a
    // working classifier and a missing one look identical. On a host where you
    // cannot read the logs, this is the only way to tell them apart.
    public class ClassifierStatus
    {
        // "onnx (in-process)", "ml_service (http)", or "disabled".
        public string Provider { get; set; } = "disabled";

        // The feature is switched on at all.
        public bool Configured { get; set; }

        // Null when readiness cannot be known without making a call - which is the
        // case for the HTTP provider. The in-process one knows for certain.
        public bool? Ready { get; set; }

        // File name or URL, never a full local path.
        public string? Source { get; set; }

        public string[] Classes { get; set; } = [];

        public double Threshold { get; set; }

        // Why the model could not be loaded, when it could not be.
        public string? Error { get; set; }
    }
}
