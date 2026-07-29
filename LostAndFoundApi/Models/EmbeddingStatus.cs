namespace LostAndFoundApi.Models
{
    // Live state of the semantic (embedding) layer of the matching engine.
    //
    // This type exists because the embedding call used to fail silently: any error
    // was swallowed and the score quietly fell back to rule-based only, so a broken
    // AI layer was indistinguishable from a working one. Surfacing this on
    // /api/health means that can never happen unnoticed again.
    public class EmbeddingStatus
    {
        // False => matching is currently running on rules alone.
        public bool Available { get; set; }

        // "local" | "huggingface" | "none"
        public string Provider { get; set; } = "none";

        public string? Endpoint { get; set; }

        // Vector length of the last successful embedding (e.g. 384 for MiniLM-L6).
        public int Dimensions { get; set; }

        public string? LastError { get; set; }
        public DateTime? LastSuccessAt { get; set; }
        public DateTime? LastAttemptAt { get; set; }

        // Number of texts held in the in-process embedding cache.
        public int CachedVectors { get; set; }
    }
}
