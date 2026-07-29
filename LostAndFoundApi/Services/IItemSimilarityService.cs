using LostAndFoundApi.Models;

namespace LostAndFoundApi.Services
{
    public interface IItemSimilarityService
    {
        // Full evaluation: similarity score, confidence label, and the explainable
        // per-field reasons behind the match.
        Task<ItemMatchResult> EvaluateLostFoundAsync(Lost lost, Found found);

        // Last known state of the embedding layer, as observed during scoring.
        // Cheap: reads cached state, makes no network call.
        EmbeddingStatus GetEmbeddingStatus();

        // Actively contacts the embedding service and refreshes the status.
        // Used by /api/health so an outage surfaces before a user notices it.
        Task<EmbeddingStatus> ProbeEmbeddingServiceAsync(CancellationToken cancellationToken = default);
    }
}
