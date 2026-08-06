using LostAndFoundApi.Models;
using LostAndFoundApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

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
        private readonly IConfiguration configuration;
        private readonly IHostEnvironment hostEnvironment;

        public HealthController(
            AppDbContext context,
            IItemSimilarityService itemSimilarityService,
            IConfiguration configuration,
            IHostEnvironment hostEnvironment)
        {
            this.context = context;
            this.itemSimilarityService = itemSimilarityService;
            this.configuration = configuration;
            this.hostEnvironment = hostEnvironment;
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
                build = BuildInfo(),
                config = ConfigInfo(),
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

        // Whether configuration arrived, never what it contains. "The key is missing"
        // and "the key is present but rejected" need different fixes, and on a host
        // where you cannot read the process environment there is otherwise no way to
        // tell them apart. environmentName matters because appsettings.{Environment}.json
        // is only loaded for the matching name.
        private object ConfigInfo()
        {
            var key = configuration["HuggingFace:ApiKey"];

            return new
            {
                environmentName = hostEnvironment.EnvironmentName,
                huggingFaceKeyConfigured = !string.IsNullOrWhiteSpace(key),
                huggingFaceKeyLength = key?.Length ?? 0,
                embeddingServiceUrl = configuration["Embedding:ServiceUrl"],
                itemNameSemanticFloor = configuration["Matching:ItemNameSemanticFloor"] ?? "(default)",
            };
        }

        // Which build is actually serving. Without this there is no way to tell a
        // deployment that did not happen from one that happened but is misconfigured -
        // the rest of this response looks identical in both cases.
        private static object BuildInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            DateTime? builtAt = null;

            try
            {
                if (!string.IsNullOrEmpty(assembly.Location) && System.IO.File.Exists(assembly.Location))
                {
                    builtAt = System.IO.File.GetLastWriteTimeUtc(assembly.Location);
                }
            }
            catch
            {
                // Single-file or restricted hosting: version alone still identifies the build.
            }

            return new
            {
                version = assembly.GetName().Version?.ToString(),
                informationalVersion = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                builtAt,
                startedAt = Process.StartTime.ToUniversalTime(),
            };
        }

        private static readonly System.Diagnostics.Process Process = System.Diagnostics.Process.GetCurrentProcess();

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
