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

        // Embeds everything a scoring pass will need, in as few round trips as possible.
        // Call before evaluating one item against a list of candidates: without it each
        // pair fetches its own embeddings and the pass costs one network round trip per
        // candidate, in series.
        Task PrimeAsync(Lost lost, IEnumerable<Found> candidates, CancellationToken cancellationToken = default);

        Task PrimeAsync(Found found, IEnumerable<Lost> candidates, CancellationToken cancellationToken = default);
    }
}
