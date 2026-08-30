using LostAndFoundApi.Models;
using System.Text.Json;

namespace LostAndFoundApi.Services
{
    // Calls the item image classifier hosted by /ml_service (POST /classify/image).
    //
    // Everything here is best-effort by design. Reporting a lost item is the user's
    // actual goal; knowing what the photo shows is a bonus. So every failure path -
    // service down, model not loaded, timeout, malformed response, unreadable image -
    // resolves to a result with Error set and no exception, and the caller stores
    // nothing extra. Posting an item behaves exactly as it did before this existed.
    public class ItemClassificationService : IItemClassificationService
    {
        private readonly IHttpClientFactory httpClientFactory;
        private readonly IConfiguration configuration;
        private readonly ILogger<ItemClassificationService> logger;

        public ItemClassificationService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<ItemClassificationService> logger)
        {
            this.httpClientFactory = httpClientFactory;
            this.configuration = configuration;
            this.logger = logger;
        }

        // The classifier lives in the same FastAPI app as the embedding model, so
        // Embedding:ServiceUrl is the sensible default - one service, one URL to
        // change. The separate key exists so the two can be split later (e.g. the
        // classifier hosted somewhere with more memory) without touching code.
        private string? ServiceUrl =>
            (configuration["ImageClassification:ServiceUrl"]
             ?? configuration["Embedding:ServiceUrl"])?.TrimEnd('/');

        // Kept short on purpose. A user is waiting on the response, and the local
        // model answers in well under a second; anything longer means the service is
        // in trouble and the right move is to give up and post the item without it.
        private TimeSpan Timeout =>
            TimeSpan.FromSeconds(
                double.TryParse(configuration["ImageClassification:TimeoutSeconds"], out var v) && v > 0
                    ? v
                    : 10);

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ServiceUrl);

        // Readiness is deliberately left null: the model lives in another process, so
        // the only way to know is to call it, and /api/health should not pay for a
        // round trip - nor make one to a host that may not be there.
        public ClassifierStatus Describe() =>
            new()
            {
                Provider = "ml_service (http)",
                Configured = IsConfigured,
                Ready = null,
                Source = ServiceUrl,
                Threshold = 0,
                Error = IsConfigured ? null : "ImageClassification:ServiceUrl is not configured.",
            };

        // The model's class names mapped onto the category values the report forms
        // actually use (see the <select> in lost.html / found.html).
        //
        // Read the model's side of this from models/class_names.json - never retype
        // it - and keep the app's side matching the dropdown. Every class now has
        // its own option, so each maps one-to-one; "Watch" and "Calculator" used to
        // be folded into "jewelry" and "electronics" because the form offered
        // nothing better.
        //
        // Older items still carry those folded values, which is fine:
        // ItemSimilarityService treats "watch" and "jewelry" as the same category
        // (CategorySynonyms) and "calculator" as related to "electronics"
        // (RelatedCategoryGroups), so a post from before this change still matches
        // one from after it.
        private static readonly Dictionary<string, string> LabelToCategory =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Laptop"] = "laptop",
                ["Mobile Phone"] = "phone",
                ["Watch"] = "watch",
                ["Calculator"] = "calculator",
            };

        // The app categories the model can actually speak about.
        //
        // The classifier only knows four kinds of object, while the form offers ten
        // categories. A photo of a wallet still returns one of the four, often
        // confidently, because the model has no way to say "none of these". So its
        // opinion is only allowed to influence matching when the item is in a
        // category the model was trained on; elsewhere the prediction is recorded
        // but ignored by the scoring engine.
        //
        // Public because ItemSimilarityService gates on it, and duplicating the set
        // there would let the two drift apart.
        // Compared against NormalizeCategory output, not raw form values - which is
        // why "watch" is absent and "jewelry" stands in for it: CategorySynonyms
        // folds the former into the latter. "electronics" stays because that is
        // where calculators were filed before they had their own option.
        public static readonly HashSet<string> ModelCoveredCategories =
            new(StringComparer.OrdinalIgnoreCase)
            { "laptop", "phone", "jewelry", "calculator", "electronics" };

        // The form categories that do NOT contradict a detection, so a report form can
        // tell "you picked something else" from "you picked a broader bucket".
        //
        // Deliberately stricter than the scoring engine's RelatedCategoryGroups, which
        // treats phone, laptop, electronics and calculator as all related. That
        // leniency exists so one wrong category cannot destroy a real match - it is the
        // right call there and the wrong one here, because "the photo is a laptop and
        // you chose Mobile Phone" is exactly the mistake worth pointing out.
        //
        // What is allowed instead is a genuinely broader bucket: "Other Electronics"
        // over a laptop is a reasonable filing choice, not an error, and so is
        // "Jewelry" over a watch - CategorySynonyms folds watch into jewelry, so the
        // matching engine already reads them as the same thing.
        private static readonly Dictionary<string, string[]> AcceptableCategories =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["laptop"] = ["laptop", "electronics"],
                ["phone"] = ["phone", "electronics"],
                ["calculator"] = ["calculator", "electronics"],
                ["watch"] = ["watch", "jewelry"],
            };

        // Empty when the detection maps to nothing known, which the caller must read as
        // "no opinion" - never as "everything conflicts".
        public static string[] AcceptableCategoriesFor(string? category) =>
            !string.IsNullOrWhiteSpace(category)
            && AcceptableCategories.TryGetValue(category, out var acceptable)
                ? acceptable
                : [];

        public static string? CategoryForLabel(string? label) =>
            !string.IsNullOrWhiteSpace(label) && LabelToCategory.TryGetValue(label, out var category)
                ? category
                : null;

        public async Task<ImageClassificationResult> ClassifyAsync(
            Stream image,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            var baseUrl = ServiceUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Failed("ImageClassification:ServiceUrl is not configured.");
            }

            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = Timeout;

                using var form = new MultipartFormDataContent();
                using var content = new StreamContent(image);
                // The service sniffs the actual format itself; this header only has
                // to be present and plausible for the multipart part to be well formed.
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(content, "file", string.IsNullOrWhiteSpace(fileName) ? "upload" : fileName);

                using var response = await client.PostAsync($"{baseUrl}/classify/image", form, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    // 503 here is the ordinary "no model file present" case, so it is
                    // logged at Information rather than as a fault.
                    logger.LogInformation(
                        "Image classification unavailable: {Status} from {Url}/classify/image.",
                        (int)response.StatusCode, baseUrl);
                    return Failed($"Classifier returned {(int)response.StatusCode}.");
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var label = root.TryGetProperty("label", out var labelEl) && labelEl.ValueKind == JsonValueKind.String
                    ? labelEl.GetString()
                    : null;

                var confidence = root.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out var c)
                    ? c
                    : 0;

                var known = root.TryGetProperty("known", out var knownEl)
                    && knownEl.ValueKind == JsonValueKind.True;

                var scores = new Dictionary<string, double>();
                if (root.TryGetProperty("scores", out var scoresEl) && scoresEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in scoresEl.EnumerateObject())
                    {
                        if (property.Value.TryGetDouble(out var value))
                        {
                            scores[property.Name] = value;
                        }
                    }
                }

                var category = CategoryForLabel(label);

                // A label the mapping does not recognise means class_names.json and
                // LabelToCategory have drifted apart - worth saying out loud, because
                // the prediction is otherwise silently discarded.
                if (known && label is not null && category is null)
                {
                    logger.LogWarning(
                        "Classifier returned label '{Label}', which maps to no app category. "
                        + "Update LabelToCategory in ItemClassificationService.", label);
                }

                logger.LogDebug(
                    "Classified {File}: label={Label} confidence={Confidence:F4} known={Known} category={Category}",
                    fileName, label ?? "(none)", confidence, known, category ?? "(none)");

                return new ImageClassificationResult
                {
                    Label = label,
                    Confidence = confidence,
                    Known = known,
                    Category = category,
                    Scores = scores,
                };
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out rather than being cancelled by the caller.
                logger.LogInformation("Image classification timed out after {Seconds}s.", Timeout.TotalSeconds);
                return Failed($"Classifier did not respond within {Timeout.TotalSeconds:F0}s.");
            }
            catch (Exception ex)
            {
                logger.LogInformation(
                    "Image classification failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
                return Failed($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static ImageClassificationResult Failed(string error) =>
            new() { Known = false, Error = error };
    }
}
