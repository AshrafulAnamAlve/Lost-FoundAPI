using LostAndFoundApi.Models;

namespace LostAndFoundApi.Services
{
    public interface IItemClassificationService
    {
        // True when a classifier is configured at all. False means the feature is
        // switched off and callers should not bother building a request.
        bool IsConfigured { get; }

        // Which implementation is in use and whether it can answer, for /api/health.
        // Never throws. Classification degrades silently by design, so without this
        // a working classifier and a missing one are indistinguishable from outside.
        ClassifierStatus Describe();

        // Classifies one item photo. Never throws and never returns null: a failure
        // comes back as a result with Error set, so a posting flow can ignore it and
        // carry on. The stream is read from the current position.
        Task<ImageClassificationResult> ClassifyAsync(
            Stream image,
            string fileName,
            CancellationToken cancellationToken = default);
    }
}
