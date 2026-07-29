using LostAndFoundApi.Models;
using LostAndFoundApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LostAndFoundApi.Controllers
{
    // Operational status of the system. Exists so a failure of the semantic matching
    // layer is visible immediately instead of silently degrading match quality.
    [Route("api/[controller]")]
    [ApiController]
    public class HealthController : ControllerBase
    {
        private readonly AppDbContext context;
        private readonly IItemSimilarityService itemSimilarityService;

        public HealthController(AppDbContext context, IItemSimilarityService itemSimilarityService)
        {
            this.context = context;
            this.itemSimilarityService = itemSimilarityService;
        }

        [HttpGet]
        public async Task<ActionResult> Get(CancellationToken cancellationToken)
        {
            var database = await CheckDatabaseAsync(cancellationToken);
            var embedding = await itemSimilarityService.ProbeEmbeddingServiceAsync(cancellationToken);

            // "degraded" = still fully usable, just matching on rules alone.
            var status = !database.ok ? "unhealthy"
                       : embedding.Available ? "healthy"
                       : "degraded";

            return Ok(new
            {
                status,
                checkedAt = DateTime.UtcNow,
                database = new { ok = database.ok, error = database.error },
                matching = new
                {
                    mode = embedding.Available ? "hybrid (rules + embeddings)" : "rules only",
                    semanticLayerActive = embedding.Available,
                    provider = embedding.Provider,
                    endpoint = embedding.Endpoint,
                    dimensions = embedding.Dimensions,
                    cachedVectors = embedding.CachedVectors,
                    lastSuccessAt = embedding.LastSuccessAt,
                    lastAttemptAt = embedding.LastAttemptAt,
                    lastError = embedding.LastError
                }
            });
        }

        private async Task<(bool ok, string? error)> CheckDatabaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                var canConnect = await context.Database.CanConnectAsync(cancellationToken);
                return (canConnect, canConnect ? null : "Cannot connect to the database.");
            }
            catch (Exception ex)
            {
                return (false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
