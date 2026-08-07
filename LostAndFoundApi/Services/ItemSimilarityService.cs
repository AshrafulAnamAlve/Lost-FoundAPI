using LostAndFoundApi.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LostAndFoundApi.Services
{
    public class ItemSimilarityService : IItemSimilarityService
    {
        private readonly IHttpClientFactory httpClientFactory;
        private readonly IConfiguration configuration;
        private readonly ILogger<ItemSimilarityService> logger;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<double>> embeddingCache = new(StringComparer.Ordinal);

        // Observed state of the semantic layer. Mutated during scoring, read by /api/health.
        private readonly EmbeddingStatus status = new();

        // Once the embedding provider has failed we stop hammering it on every single
        // pair comparison; a scoring pass over N candidates would otherwise produce N
        // identical timeouts. Retried after this cooldown.
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(1);
        private DateTime nextRetryAt = DateTime.MinValue;

        public ItemSimilarityService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<ItemSimilarityService> logger)
        {
            this.httpClientFactory = httpClientFactory;
            this.configuration = configuration;
            this.logger = logger;
        }

        // Base URL of the self-hosted ML service (see /ml_service). Preferred over the
        // hosted HuggingFace API: no key, no rate limit, works offline.
        private string? LocalServiceUrl => configuration["Embedding:ServiceUrl"]?.TrimEnd('/');

        public EmbeddingStatus GetEmbeddingStatus()
        {
            status.CachedVectors = embeddingCache.Count;
            return status;
        }

        // Reports what scoring would actually use right now. Probes the providers in the
        // same order the scoring path resolves them, so /api/health cannot claim "rules
        // only" while matches are in fact being scored with HuggingFace embeddings.
        public async Task<EmbeddingStatus> ProbeEmbeddingServiceAsync(CancellationToken cancellationToken = default)
        {
            var wasAvailable = status.Available;
            status.LastAttemptAt = DateTime.UtcNow;

            if (await ProbeLocalServiceAsync(cancellationToken) || await ProbeHuggingFaceAsync(cancellationToken))
            {
                nextRetryAt = DateTime.MinValue;
                LogAvailabilityTransition(wasAvailable);
                return GetEmbeddingStatus();
            }

            status.Available = false;
            status.Provider = "none";
            LogAvailabilityTransition(wasAvailable);
            return GetEmbeddingStatus();
        }

        private async Task<bool> ProbeLocalServiceAsync(CancellationToken cancellationToken)
        {
            var baseUrl = LocalServiceUrl;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                status.LastError = "Embedding:ServiceUrl is not configured.";
                return false;
            }

            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                using var response = await client.GetAsync($"{baseUrl}/health", cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    status.Endpoint = baseUrl;
                    status.LastError = $"ML service returned {(int)response.StatusCode}.";
                    return false;
                }

                using var doc = JsonDocument.Parse(body);
                var ready = doc.RootElement.TryGetProperty("textReady", out var readyEl) && readyEl.GetBoolean();

                status.Available = ready;
                status.Provider = ready ? "local" : "none";
                status.Endpoint = baseUrl;
                status.LastError = ready
                    ? null
                    : doc.RootElement.TryGetProperty("textError", out var errEl) && errEl.ValueKind != JsonValueKind.Null
                        ? errEl.GetString()
                        : "ML service reachable but the text model is not loaded.";

                if (ready && doc.RootElement.TryGetProperty("textDim", out var dimEl) && dimEl.TryGetInt32(out var dim))
                {
                    status.Dimensions = dim;
                }

                return ready;
            }
            catch (Exception ex)
            {
                status.Endpoint = baseUrl;
                status.LastError = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        // Costs one metered call, so the result is cached: once HuggingFace has answered
        // successfully, later health checks reuse that within the cooldown window.
        private async Task<bool> ProbeHuggingFaceAsync(CancellationToken cancellationToken)
        {
            var apiKey = configuration["HuggingFace:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return false;
            }

            var model = configuration["HuggingFace:Model"] ?? "sentence-transformers/all-MiniLM-L6-v2";

            if (status.Provider == "huggingface" && status.LastSuccessAt is { } last
                && DateTime.UtcNow - last < FailureCooldown)
            {
                status.Available = true;
                // The local probe that ran first left its own failure reason behind;
                // reporting it next to a healthy status would just be confusing.
                status.LastError = null;
                return true;
            }

            var vectors = await GetHuggingFaceEmbeddingsAsync(model, apiKey, new[] { "health probe" }, cancellationToken);
            if (vectors is not { Count: 1 } || vectors[0].Count == 0)
            {
                return false;
            }

            RecordSuccess("huggingface", HuggingFaceEndpoint(model), vectors[0].Count);
            return true;
        }

        // Logs only on a change of state, so a health-check poll doesn't spam the log.
        // Lives here (rather than inline) because both the probe and the scoring path
        // can flip availability, and a transition must be reported whichever got there first.
        private void LogAvailabilityTransition(bool wasAvailable)
        {
            if (wasAvailable == status.Available) return;

            if (status.Available)
            {
                logger.LogInformation(
                    "Semantic matching ACTIVE via {Provider} ({Endpoint}), {Dimensions}-dim embeddings.",
                    status.Provider, status.Endpoint, status.Dimensions);
            }
            else
            {
                logger.LogWarning(
                    "Semantic matching UNAVAILABLE - scoring has fallen back to rules only. Reason: {Error}",
                    status.LastError);
            }
        }

        private static readonly Dictionary<string, string> SynonymMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cellphone"] = "phone",
            ["mobile"] = "phone",
            ["smartphone"] = "phone",
            ["dark"] = "black",
            ["grey"] = "gray",
            ["earbuds"] = "earphone",
            ["headset"] = "headphone",
            ["bagpack"] = "backpack",
            ["wallets"] = "wallet",
            ["keys"] = "key",
            ["spectacles"] = "glasses",
            ["specs"] = "glasses"
        };

        // Canonical category buckets. The form uses a fixed dropdown, but we normalise
        // free-text / legacy values so the gate stays reliable.
        private static readonly Dictionary<string, string> CategorySynonyms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mobile"] = "phone",
            ["mobile phone"] = "phone",
            ["cellphone"] = "phone",
            ["smartphone"] = "phone",
            ["cell phone"] = "phone",
            ["tablet"] = "laptop",
            ["notebook"] = "laptop",
            ["computer"] = "laptop",
            ["purse"] = "wallet",
            ["backpack"] = "bag",
            ["handbag"] = "bag",
            ["watch"] = "jewelry",
            ["id"] = "documents",
            ["passport"] = "documents",
            ["document"] = "documents",
            ["shoes"] = "clothing",
            ["clothes"] = "clothing"
        };

        // Categories that are close enough that one user might pick either for the same
        // real-world item (e.g. a phone filed under "electronics"). Members of the same
        // group are treated as "related" rather than a hard mismatch.
        private static readonly List<HashSet<string>> RelatedCategoryGroups = new()
        {
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "phone", "laptop", "electronics" }
        };

        // A match must clear this combined score to be surfaced. Kept here so the controller
        // and the service share one source of truth.
        public const double MatchThreshold = 0.45;

        // Score bands used for the human-friendly confidence label.
        private const double StrongThreshold = 0.72;
        private const double PossibleThreshold = 0.55;

        // Item-name cosine at or above which two differently-spelled names are treated as
        // the same kind of object, exempting the pair from the item-name floor gate.
        // A hyperparameter, so it is configurable (Matching:ItemNameSemanticFloor) rather
        // than compiled in - it should be set by a sweep over labelled pairs, and that
        // sweep should not require a redeploy to act on.
        //
        // 0.35 comes from a sweep over nine real lost/found pairs (three laptops, three
        // phones, three watches, each posted twice in different wording): 0.40 and above
        // scored 6/9, while everything in [0.25, 0.35] scored 9/9, with no false
        // positives at any setting. The gap is clean - on that set the best-scoring
        // non-matching pair reached 0.292 and the worst true pair 0.352 - but nine pairs
        // is not a benchmark, so this wants re-sweeping on the labelled set.
        private const double DefaultItemNameSemanticFloor = 0.35;

        // BuildReasons is static and only needs the default to word a chip; the
        // configurable value drives scoring, where it actually changes outcomes.
        private const double ItemNameSemanticFloorStatic = DefaultItemNameSemanticFloor;

        private double ItemNameSemanticFloor =>
            double.TryParse(configuration["Matching:ItemNameSemanticFloor"], out var v)
                ? v
                : DefaultItemNameSemanticFloor;

        public async Task<ItemMatchResult> EvaluateLostFoundAsync(Lost lost, Found found)
        {
            // The item-name cosine is computed once and shared: the score uses it to
            // decide the gate, and the reasons use it so the explanation cannot claim
            // the names differ on a pair the semantic layer just matched.
            var nameSemantic = await ItemNameSemanticAsync(lost, found);
            var score = await ComputeScoreAsync(lost, found, nameSemantic);

            return new ItemMatchResult
            {
                Score = score,
                Confidence = ConfidenceOf(score),
                Reasons = BuildReasons(lost, found, nameSemantic)
            };
        }

        private async Task<double> ComputeScoreAsync(Lost lost, Found found, double nameSemantic)
        {
            // Priority-weighted similarity across the structured fields.
            var fieldScore = CalculateFieldScore(lost, found);

            // Optional semantic signal from embeddings (calibrated, 0..1) or -1 when unavailable.
            var aiScore = await CalculateAiScoreAsync(lost, found);

            var baseScore = aiScore < 0
                ? fieldScore
                : (fieldScore * 0.70) + (aiScore * 0.30);

            // Hard gates that veto cross-item false positives (different category, brand, etc.).
            var gate = GateMultiplier(lost, found, nameSemantic);
            baseScore *= gate;

            // One line per comparison, at Debug, so a surprising score can be taken apart
            // without redeploying: it shows which term actually moved it.
            logger.LogDebug(
                "Score {Lost} <-> {Found}: field={Field:F3} ai={Ai:F3} nameSemantic={Name:F3} gate={Gate:F3} final={Final:F3}",
                lost.itemName, found.itemName, fieldScore, aiScore, nameSemantic, gate,
                Math.Clamp(baseScore, 0, 1));

            return Math.Clamp(baseScore, 0, 1);
        }

        private static string ConfidenceOf(double score)
            => score >= StrongThreshold ? "Strong"
             : score >= PossibleThreshold ? "Possible"
             : "Weak";

        // Re-derives the per-field signals as short, human-readable reasons.
        //
        // These must call exactly the comparisons CalculateFieldScore and GateMultiplier
        // call. When they drifted apart the UI contradicted the engine - a pair could be
        // matched on an item name the semantic layer recognised while the chip beside it
        // still read "Different item name", which reads as the AI not working at all.
        //
        // nameSemantic is the item-name cosine, or -1 without embeddings.
        private static List<MatchReason> BuildReasons(Lost lost, Found found, double nameSemantic = -1)
        {
            var reasons = new List<MatchReason>();

            void Reason(string field, string label, string status, string? detail = null)
                => reasons.Add(new MatchReason { Field = field, Label = label, Status = status, Detail = detail });

            // What the item is — the headline signal. Spelling first, then the semantic
            // layer, which is what lets "Laptop" and "Notebook computer" agree.
            if (BothHaveText(lost.itemName, found.itemName))
            {
                var s = HybridTextScore(lost.itemName, found.itemName);
                if (s >= 0.70) Reason("itemName", "Same item", "match", found.itemName);
                else if (nameSemantic >= 0.60) Reason("itemName", "Same kind of item", "match", found.itemName);
                else if (s >= 0.35 || nameSemantic >= ItemNameSemanticFloorStatic) Reason("itemName", "Similar item name", "partial");
                else Reason("itemName", "Different item name", "mismatch");
            }

            // Category.
            if (BothHaveText(lost.category, found.category))
            {
                switch (CategoryRelationOf(lost.category, found.category))
                {
                    case CategoryRelation.Same: Reason("category", "Same category", "match", found.category); break;
                    case CategoryRelation.Related: Reason("category", "Related category", "partial", found.category); break;
                    default: Reason("category", "Different category", "mismatch"); break;
                }
            }

            // Brand / model. BrandScore treats "Samsung" and "Samsung Tab" as the same
            // maker, so the chip has to agree rather than call it a mismatch.
            if (BothHaveText(lost.brand, found.brand))
            {
                var s = BrandScore(lost.brand, found.brand);
                if (s >= 0.99) Reason("brand", "Brand matches", "match", found.brand);
                else if (s >= 0.70) Reason("brand", "Same brand, different model", "partial", found.brand);
                else Reason("brand", "Different brand", "mismatch");
            }

            // Colour.
            if (BothHaveText(lost.color, found.color))
            {
                var s = ColourScore(lost.color, found.color);
                if (s >= 0.99) Reason("color", "Same colour", "match", found.color);
                else if (s >= 0.45) Reason("color", "Similar colour", "partial", found.color);
                else Reason("color", "Colour differs", "mismatch");
            }

            // Location.
            if (BothHaveText(lost.location, found.location))
            {
                var s = JaccardSimilarity(Tokenize(lost.location), Tokenize(found.location));
                if (s >= 0.40) Reason("location", "Same area", "match", found.location);
                else if (s > 0) Reason("location", "Nearby area", "partial");
            }

            // Date proximity.
            var dateScore = DateScore(lost.dateLost, found.dateFound);
            if (dateScore >= 0.80) Reason("date", "Around the same time", "match");
            else if (dateScore >= 0.50) Reason("date", "Within a week", "partial");

            // Description overlap — only surfaced when it actually adds confidence.
            if (BothHaveText(lost.description, found.description)
                && HybridTextScore(lost.description, found.description) >= 0.45)
            {
                Reason("description", "Description matches", "match");
            }

            return reasons;
        }

        // ---------------------------------------------------------------------
        // Rule-based field scoring
        // ---------------------------------------------------------------------

        // Weighted average over the fields that are actually comparable on both sides.
        // Optional/empty fields are skipped (they don't dilute the score) so a missing
        // brand never silently lowers a genuine match.
        private static double CalculateFieldScore(Lost lost, Found found)
        {
            double weightedSum = 0;
            double weightTotal = 0;

            void Add(double weight, double score, bool comparable)
            {
                if (!comparable) return;
                weightedSum += weight * score;
                weightTotal += weight;
            }

            // itemName carries the most signal about *what* the item is.
            Add(0.35, HybridTextScore(lost.itemName, found.itemName), BothHaveText(lost.itemName, found.itemName));

            // category — strong, structured discriminator (also enforced by the gate below).
            Add(0.15, CategoryScore(lost.category, found.category), BothHaveText(lost.category, found.category));

            // brand/model is decisive for electronics; only counted when both sides provide it.
            Add(0.15, BrandScore(lost.brand, found.brand), BothHaveText(lost.brand, found.brand));

            // free-text description.
            Add(0.12, HybridTextScore(lost.description, found.description), BothHaveText(lost.description, found.description));

            // colour.
            Add(0.10, ColourScore(lost.color, found.color), BothHaveText(lost.color, found.color));

            // location overlap.
            Add(0.08, JaccardSimilarity(Tokenize(lost.location), Tokenize(found.location)), BothHaveText(lost.location, found.location));

            // date proximity (lost is always on/before found for a real match).
            Add(0.05, DateScore(lost.dateLost, found.dateFound), true);

            return weightTotal == 0 ? 0 : weightedSum / weightTotal;
        }

        private static double CategoryScore(string? left, string? right)
        {
            return CategoryRelationOf(left, right) switch
            {
                CategoryRelation.Same => 1.0,
                CategoryRelation.Related => 0.6,
                _ => 0.0
            };
        }

        private static double DateScore(DateTime lostDate, DateTime foundDate)
        {
            // An item can only be found on/after it was lost; a found-before-lost gap is suspicious.
            var signedDays = (foundDate.Date - lostDate.Date).TotalDays;
            if (signedDays < -2) return 0.0;

            var dayDiff = Math.Abs(signedDays);
            return dayDiff switch
            {
                <= 1 => 1.0,
                <= 3 => 0.8,
                <= 7 => 0.5,
                <= 21 => 0.25,
                _ => 0.0
            };
        }

        // ---------------------------------------------------------------------
        // Gates — multipliers that can knock a non-match below the threshold
        // ---------------------------------------------------------------------

        // nameSemantic: cosine similarity between the two item names, or -1 when the
        // embedding layer is unavailable (in which case gate 3 behaves exactly as before).
        private double GateMultiplier(Lost lost, Found found, double nameSemantic = -1)
        {
            double m = 1.0;

            // 1) Category is the single strongest "different item" signal. A wallet is
            //    never a phone, so a cross-category pair is effectively vetoed.
            if (BothHaveText(lost.category, found.category))
            {
                m *= CategoryRelationOf(lost.category, found.category) switch
                {
                    CategoryRelation.Same => 1.0,
                    CategoryRelation.Related => 0.85,
                    _ => 0.20
                };
            }

            // 2) For items that DO share a category, a clearly different brand almost
            //    always means a different physical item (iPhone vs Galaxy).
            if (BothHaveText(lost.brand, found.brand) && BrandScore(lost.brand, found.brand) < 0.34)
            {
                m *= 0.70;
            }

            // 3) Item-name floor: don't let two unrelated names slip through on shared
            //    generic words ("black", "small", ...).
            //
            //    HybridTextScore is spelling-based, so on its own this gate also punishes
            //    the pairs the semantic layer exists to catch - laptop/notebook,
            //    wallet/purse - which share no characters but name the same object. It
            //    runs after fusion, so it would multiply the embedding contribution away.
            //    Require the names to be semantically distant too before penalising.
            //    nameSemantic is -1 without embeddings, so the old behaviour is kept.
            if (BothHaveText(lost.itemName, found.itemName)
                && HybridTextScore(lost.itemName, found.itemName) < 0.20
                && nameSemantic < ItemNameSemanticFloor)
            {
                m *= 0.55;
            }

            return m;
        }

        private enum CategoryRelation { Same, Related, Different }

        private static CategoryRelation CategoryRelationOf(string? a, string? b)
        {
            var ca = NormalizeCategory(a);
            var cb = NormalizeCategory(b);

            if (string.IsNullOrEmpty(ca) || string.IsNullOrEmpty(cb)) return CategoryRelation.Related;
            if (ca == cb) return CategoryRelation.Same;

            // "other" is ambiguous by definition — never treat it as a hard mismatch.
            if (ca == "other" || cb == "other") return CategoryRelation.Related;

            foreach (var group in RelatedCategoryGroups)
            {
                if (group.Contains(ca) && group.Contains(cb)) return CategoryRelation.Related;
            }

            return CategoryRelation.Different;
        }

        private static string NormalizeCategory(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var key = value.Trim().ToLowerInvariant();
            return CategorySynonyms.TryGetValue(key, out var mapped) ? mapped : key;
        }

        private static bool BothHaveText(string? a, string? b)
            => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b);

        // ---------------------------------------------------------------------
        // AI / embedding signal
        // ---------------------------------------------------------------------

        // Returns a calibrated 0..1 semantic score, or -1 when embeddings are
        // unavailable — in which case ComputeScoreAsync falls back to rules only.
        private async Task<double> CalculateAiScoreAsync(Lost lost, Found found)
        {
            try
            {
                var lostText = BuildLostText(lost);
                var foundText = BuildFoundText(found);

                var pair = await GetEmbeddingPairAsync(lostText, foundText);
                if (pair is null)
                {
                    return -1;
                }

                var (lostEmbedding, foundEmbedding) = pair.Value;
                if (lostEmbedding.Count == 0 || foundEmbedding.Count == 0)
                {
                    return -1;
                }

                var cosine = CosineSimilarity(lostEmbedding, foundEmbedding);
                return CalibrateEmbeddingScore(cosine);
            }
            catch (Exception ex)
            {
                // Never fail a match because the semantic layer is down, but never
                // hide it either — silence is what made this break go unnoticed.
                RecordFailure($"{ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        // A scoring pass compares one item against every candidate, and each pair needs
        // two vectors: the whole record, and the item name on its own. Fetched lazily
        // that is one network round trip per candidate, in series - seconds of latency
        // on a hosted provider, paid by the user waiting on a submit. Every text the
        // pass will ask for is known up front, so fetch them all now, batched.
        public Task PrimeAsync(Lost lost, IEnumerable<Found> candidates, CancellationToken cancellationToken = default)
            => PrimeTextsAsync(
                new[] { BuildLostText(lost), NameKey(lost.itemName) }
                    .Concat(candidates.SelectMany(c => new[] { BuildFoundText(c), NameKey(c.itemName) })),
                cancellationToken);

        public Task PrimeAsync(Found found, IEnumerable<Lost> candidates, CancellationToken cancellationToken = default)
            => PrimeTextsAsync(
                new[] { BuildFoundText(found), NameKey(found.itemName) }
                    .Concat(candidates.SelectMany(c => new[] { BuildLostText(c), NameKey(c.itemName) })),
                cancellationToken);

        // Providers cap how much they accept per request, so send in chunks. A failure
        // is not fatal: scoring falls back to fetching pairs lazily, or to rules only.
        private const int PrimeBatchSize = 24;

        private async Task PrimeTextsAsync(IEnumerable<string> texts, CancellationToken cancellationToken)
        {
            var pending = texts
                .Where(t => !string.IsNullOrWhiteSpace(t) && !embeddingCache.ContainsKey(t))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (pending.Count == 0 || DateTime.UtcNow < nextRetryAt)
            {
                return;
            }

            for (var offset = 0; offset < pending.Count; offset += PrimeBatchSize)
            {
                var batch = pending.Skip(offset).Take(PrimeBatchSize).ToArray();
                var vectors = await GetEmbeddingsAsync(batch, cancellationToken);

                if (vectors is null || vectors.Count != batch.Length)
                {
                    return;
                }

                for (var i = 0; i < batch.Length; i++)
                {
                    if (vectors[i].Count > 0) embeddingCache[batch[i]] = vectors[i];
                }
            }
        }

        // Resolves a batch of texts through whichever provider is available.
        private async Task<List<List<double>>?> GetEmbeddingsAsync(string[] texts, CancellationToken cancellationToken)
        {
            var baseUrl = LocalServiceUrl;
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                var local = await GetLocalEmbeddingsAsync(baseUrl, texts);
                if (local is not null && local.Count == texts.Length)
                {
                    RecordSuccess("local", baseUrl, local[0].Count);
                    return local;
                }
            }

            var apiKey = configuration["HuggingFace:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var model = configuration["HuggingFace:Model"] ?? "sentence-transformers/all-MiniLM-L6-v2";
                var hf = await GetHuggingFaceEmbeddingsAsync(model, apiKey, texts, cancellationToken);
                if (hf is not null && hf.Count == texts.Length)
                {
                    RecordSuccess("huggingface", HuggingFaceEndpoint(model), hf[0].Count);
                    return hf;
                }
            }

            RecordFailure(status.LastError ?? "No embedding provider is reachable.");
            return null;
        }

        // The cache key for an item name. Must match what ItemNameSemanticAsync looks up,
        // or priming silently misses and the lazy path pays for it anyway.
        private static string NameKey(string? itemName) =>
            string.IsNullOrWhiteSpace(itemName) ? string.Empty : itemName.Trim().ToLowerInvariant();

        // Cosine between the two item names on their own, or -1 when embeddings are
        // unavailable. Deliberately separate from CalculateAiScoreAsync, which embeds the
        // whole record: a shared category and colour would otherwise drown out the one
        // signal this gate asks about, which is whether the names denote the same object.
        // Item names repeat heavily across a catalogue, so the cache absorbs most calls.
        private async Task<double> ItemNameSemanticAsync(Lost lost, Found found)
        {
            if (!BothHaveText(lost.itemName, found.itemName))
            {
                return -1;
            }

            try
            {
                var pair = await GetEmbeddingPairAsync(NameKey(lost.itemName), NameKey(found.itemName));
                if (pair is null || pair.Value.Lost.Count == 0 || pair.Value.Found.Count == 0)
                {
                    return -1;
                }

                return CosineSimilarity(pair.Value.Lost, pair.Value.Found);
            }
            catch
            {
                // The gate simply falls back to its spelling-only behaviour.
                return -1;
            }
        }

        // Resolution order: in-process cache -> self-hosted ML service -> HuggingFace.
        // The two texts are fetched in ONE round trip, not two.
        private async Task<(List<double> Lost, List<double> Found)?> GetEmbeddingPairAsync(string lostText, string foundText)
        {
            var haveLost = embeddingCache.TryGetValue(lostText, out var cachedLost);
            var haveFound = embeddingCache.TryGetValue(foundText, out var cachedFound);
            if (haveLost && haveFound)
            {
                return (cachedLost!, cachedFound!);
            }

            // A provider that just failed is not retried for every remaining candidate
            // in the scoring loop — one cooldown, then try again.
            if (DateTime.UtcNow < nextRetryAt)
            {
                return null;
            }

            status.LastAttemptAt = DateTime.UtcNow;

            // 1) Self-hosted ML service (preferred).
            var baseUrl = LocalServiceUrl;
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                var vectors = await GetLocalEmbeddingsAsync(baseUrl, new[] { lostText, foundText });
                if (vectors is { Count: 2 })
                {
                    embeddingCache[lostText] = vectors[0];
                    embeddingCache[foundText] = vectors[1];
                    RecordSuccess("local", baseUrl, vectors[0].Count);
                    return (vectors[0], vectors[1]);
                }
            }

            // 2) HuggingFace Inference Providers. This is the path that runs when the
            //    API is hosted but the self-hosted service is not reachable from it —
            //    same model as the local service, so scores stay comparable.
            var apiKey = configuration["HuggingFace:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var model = configuration["HuggingFace:Model"] ?? "sentence-transformers/all-MiniLM-L6-v2";
                var vectors = await GetHuggingFaceEmbeddingsAsync(model, apiKey, new[] { lostText, foundText });

                if (vectors is { Count: 2 } && vectors[0].Count > 0 && vectors[1].Count > 0)
                {
                    embeddingCache[lostText] = vectors[0];
                    embeddingCache[foundText] = vectors[1];
                    RecordSuccess("huggingface", HuggingFaceEndpoint(model), vectors[0].Count);
                    return (vectors[0], vectors[1]);
                }
            }

            RecordFailure(status.LastError ?? "No embedding provider is reachable.");
            return null;
        }

        // POST {base}/embed/text  ->  { vectors: [[...], [...]] }
        private async Task<List<List<double>>?> GetLocalEmbeddingsAsync(string baseUrl, string[] texts)
        {
            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);

                var payload = JsonSerializer.Serialize(new { texts });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync($"{baseUrl}/embed/text", content);

                if (!response.IsSuccessStatusCode)
                {
                    status.LastError = $"ML service returned {(int)response.StatusCode} from /embed/text.";
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("vectors", out var vectorsEl)
                    || vectorsEl.ValueKind != JsonValueKind.Array)
                {
                    status.LastError = "ML service response did not contain a 'vectors' array.";
                    return null;
                }

                var result = new List<List<double>>();
                foreach (var row in vectorsEl.EnumerateArray())
                {
                    var vector = new List<double>();
                    ExtractNumbers(row, vector);
                    result.Add(vector);
                }

                if (result.Count != texts.Length)
                {
                    status.LastError = $"ML service returned {result.Count} vectors for {texts.Length} texts.";
                    return null;
                }

                return result;
            }
            catch (Exception ex)
            {
                status.LastError = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private void RecordSuccess(string provider, string endpoint, int dimensions)
        {
            var wasAvailable = status.Available;

            status.Available = true;
            status.Provider = provider;
            status.Endpoint = endpoint;
            status.Dimensions = dimensions;
            status.LastError = null;
            status.LastSuccessAt = DateTime.UtcNow;
            nextRetryAt = DateTime.MinValue;

            LogAvailabilityTransition(wasAvailable);
        }

        private void RecordFailure(string error)
        {
            // Treat "never succeeded yet" as a transition too, so a provider that is
            // broken from startup still produces exactly one warning.
            var wasAvailable = status.Available || status.LastSuccessAt is null;

            status.Available = false;
            status.Provider = "none";
            status.LastError = error;
            nextRetryAt = DateTime.UtcNow.Add(FailureCooldown);

            LogAvailabilityTransition(wasAvailable);
        }

        // Sentence-transformer cosine similarity has a high baseline (~0.3-0.5) even for
        // unrelated short texts. Rescale so that baseline noise maps to ~0 and only a
        // genuinely high similarity approaches 1.
        private static double CalibrateEmbeddingScore(double cosine)
        {
            const double floor = 0.35;
            const double ceil = 0.90;

            if (cosine <= floor) return 0;
            if (cosine >= ceil) return 1;
            return (cosine - floor) / (ceil - floor);
        }

        // api-inference.huggingface.co was retired by HuggingFace - it no longer even
        // resolves - and was replaced by the Inference Providers router.
        private static string HuggingFaceEndpoint(string model) =>
            $"https://router.huggingface.co/hf-inference/models/{model}/pipeline/feature-extraction";

        // POST { inputs: [textA, textB] } -> [[...], [...]]
        // Both texts go in one request: this path is metered per call, and a scoring
        // pass compares the same lost item against every candidate.
        private async Task<List<List<double>>?> GetHuggingFaceEmbeddingsAsync(
            string model, string apiKey, string[] texts, CancellationToken cancellationToken = default)
        {
            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var payload = JsonSerializer.Serialize(new
                {
                    inputs = texts,
                    options = new { wait_for_model = true }
                });

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(HuggingFaceEndpoint(model), content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    status.LastError = $"HuggingFace returned {(int)response.StatusCode}: {Truncate(body, 200)}";
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    status.LastError = "HuggingFace response was not an array of vectors.";
                    return null;
                }

                var result = new List<List<double>>();
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    var vector = new List<double>();
                    ExtractNumbers(row, vector);
                    result.Add(vector);
                }

                if (result.Count != texts.Length)
                {
                    status.LastError = $"HuggingFace returned {result.Count} vectors for {texts.Length} texts.";
                    return null;
                }

                return result;
            }
            catch (Exception ex)
            {
                status.LastError = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private static string Truncate(string value, int max) =>
            string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

        private static void ExtractNumbers(JsonElement element, List<double> target)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var child in element.EnumerateArray())
                    {
                        ExtractNumbers(child, target);
                    }
                    break;
                case JsonValueKind.Number:
                    if (element.TryGetDouble(out var value))
                    {
                        target.Add(value);
                    }
                    break;
            }
        }

        private static double CosineSimilarity(List<double> a, List<double> b)
        {
            if (a.Count == 0 || b.Count == 0 || a.Count != b.Count)
            {
                return 0;
            }

            double dot = 0;
            double magA = 0;
            double magB = 0;

            for (var i = 0; i < a.Count; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }

            if (magA == 0 || magB == 0)
            {
                return 0;
            }

            var score = dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
            return Math.Clamp(score, 0, 1);
        }

        private static string BuildLostText(Lost lost)
        {
            return $"{lost.itemName} {lost.category} {lost.description} {lost.location} {lost.brand} {lost.color}";
        }

        private static string BuildFoundText(Found found)
        {
            return $"{found.itemName} {found.category} {found.description} {found.location} {found.brand} {found.color}";
        }

        // ---------------------------------------------------------------------
        // Field comparisons that spelling alone gets wrong
        // ---------------------------------------------------------------------

        // Colour words people actually use for the same colour. Deliberately a lookup
        // and NOT the embedding model: measured on all-MiniLM-L6-v2, "black" and "white"
        // score 0.759 while "blue" and "navy" score 0.343 - the model encodes "these are
        // both colour words", which is the opposite of what this comparison needs.
        private static readonly Dictionary<string, string> ColourFamilies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = "black",   ["dark"] = "black",    ["charcoal"] = "black",
            ["jet"] = "black",     ["matte black"] = "black", ["space grey"] = "black",
            ["space gray"] = "black",

            ["white"] = "white",   ["ivory"] = "white",   ["cream"] = "white",
            ["off white"] = "white", ["pearl"] = "white",

            ["grey"] = "grey",     ["gray"] = "grey",     ["silver"] = "grey",
            ["ash"] = "grey",      ["graphite"] = "grey", ["titanium"] = "grey",

            ["blue"] = "blue",     ["navy"] = "blue",     ["sky blue"] = "blue",
            ["teal"] = "blue",     ["cyan"] = "blue",     ["midnight"] = "blue",

            ["red"] = "red",       ["maroon"] = "red",    ["crimson"] = "red",
            ["burgundy"] = "red",  ["scarlet"] = "red",

            ["green"] = "green",   ["olive"] = "green",   ["mint"] = "green",
            ["emerald"] = "green",

            ["gold"] = "gold",     ["golden"] = "gold",   ["champagne"] = "gold",
            ["rose gold"] = "gold", ["bronze"] = "gold",  ["copper"] = "gold",

            ["brown"] = "brown",   ["tan"] = "brown",     ["beige"] = "brown",
            ["coffee"] = "brown",  ["chocolate"] = "brown",

            ["pink"] = "pink",     ["rose"] = "pink",     ["magenta"] = "pink",
            ["purple"] = "purple", ["violet"] = "purple", ["lavender"] = "purple",
            ["yellow"] = "yellow", ["mustard"] = "yellow",
            ["orange"] = "orange",
        };

        // Colours that a phone in a photo could plausibly be described as either of.
        private static readonly HashSet<string> GreyAdjacent = new(StringComparer.OrdinalIgnoreCase)
        { "black", "grey" };

        // 1.0 same colour family, 0.5 for the black/grey pair people genuinely disagree
        // about, otherwise fall back to spelling so unknown words still compare.
        private static double ColourScore(string? left, string? right)
        {
            var a = ColourFamilyOf(left);
            var b = ColourFamilyOf(right);

            if (a is null || b is null) return HybridTextScore(left, right);
            if (a == b) return 1.0;
            if (GreyAdjacent.Contains(a) && GreyAdjacent.Contains(b)) return 0.5;
            return 0.0;
        }

        // "dark gray" is two tokens; try the whole phrase first, then any token that is
        // a known colour, so "matte black finish" still resolves to black.
        private static string? ColourFamilyOf(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var normalised = value.Trim().ToLowerInvariant();
            if (ColourFamilies.TryGetValue(normalised, out var whole)) return whole;

            foreach (var token in normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ColourFamilies.TryGetValue(token, out var part)) return part;
            }

            return null;
        }

        // Brand fields get filled in inconsistently: one person writes "Samsung", the
        // other "Samsung Tab" or "Samsung Galaxy Tab". When one side's tokens are a
        // subset of the other's it is the same brand with a model name attached, which
        // spelling similarity scores as a partial mismatch.
        private static double BrandScore(string? left, string? right)
        {
            var a = Tokenize(left);
            var b = Tokenize(right);

            if (a.Count == 0 || b.Count == 0) return HybridTextScore(left, right);
            if (a.SetEquals(b)) return 1.0;
            if (a.IsSubsetOf(b) || b.IsSubsetOf(a)) return 1.0;

            // Partial overlap ("Samsung Galaxy" vs "Samsung Note") still indicates the
            // same maker; leave anything with no shared token to spelling.
            return a.Overlaps(b) ? 0.85 : HybridTextScore(left, right);
        }

        // ---------------------------------------------------------------------
        // Text similarity primitives
        // ---------------------------------------------------------------------

        private static double HybridTextScore(string? left, string? right)
        {
            var leftTokens = Tokenize(left);
            var rightTokens = Tokenize(right);

            var jaccard = JaccardSimilarity(leftTokens, rightTokens);
            var fuzzy = AverageBestTokenSimilarity(leftTokens, rightTokens);

            return (jaccard * 0.60) + (fuzzy * 0.40);
        }

        private static double AverageBestTokenSimilarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0)
            {
                return 0;
            }

            double total = 0;
            foreach (var tokenA in a)
            {
                var best = b.Max(tokenB => TokenSimilarity(tokenA, tokenB));
                total += best;
            }

            return total / a.Count;
        }

        private static double TokenSimilarity(string a, string b)
        {
            if (a == b)
            {
                return 1;
            }

            var distance = LevenshteinDistance(a, b);
            var maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0)
            {
                return 0;
            }

            return 1.0 - ((double)distance / maxLen);
        }

        private static int LevenshteinDistance(string s, string t)
        {
            var n = s.Length;
            var m = t.Length;
            var d = new int[n + 1, m + 1];

            for (var i = 0; i <= n; i++) d[i, 0] = i;
            for (var j = 0; j <= m; j++) d[0, j] = j;

            for (var i = 1; i <= n; i++)
            {
                for (var j = 1; j <= m; j++)
                {
                    var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }

        private static HashSet<string> Tokenize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new HashSet<string>();
            }

            var normalized = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9\\s]", " ");
            return normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length > 1)
                .Select(NormalizeToken)
                .ToHashSet();
        }

        private static string NormalizeToken(string token)
        {
            if (SynonymMap.TryGetValue(token, out var normalized))
            {
                return normalized;
            }

            if (token.EndsWith("s") && token.Length > 3)
            {
                var singular = token[..^1];
                if (SynonymMap.TryGetValue(singular, out var singularNormalized))
                {
                    return singularNormalized;
                }

                return singular;
            }

            return token;
        }

        private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0)
            {
                return 0;
            }

            var intersection = a.Intersect(b).Count();
            var union = a.Union(b).Count();
            return union == 0 ? 0 : (double)intersection / union;
        }
    }
}
