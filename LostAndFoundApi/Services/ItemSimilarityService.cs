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

        private readonly Dictionary<string, List<double>> embeddingCache = new(StringComparer.Ordinal);

        public ItemSimilarityService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            this.httpClientFactory = httpClientFactory;
            this.configuration = configuration;
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

        public async Task<double> CalculateLostFoundScoreAsync(Lost lost, Found found)
        {
            var ruleScore = CalculateRuleScore(lost, found);

            var aiScore = await CalculateAiScoreAsync(lost, found);
            if (aiScore < 0)
            {
                return ruleScore;
            }

            return (aiScore * 0.80) + (ruleScore * 0.20);
        }

        private async Task<double> CalculateAiScoreAsync(Lost lost, Found found)
        {
            try
            {
                var apiKey = configuration["HuggingFace:ApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return -1;
                }

                var model = configuration["HuggingFace:Model"] ?? "sentence-transformers/all-MiniLM-L6-v2";

                var lostText = BuildLostText(lost);
                var foundText = BuildFoundText(found);

                var lostEmbedding = await GetEmbeddingAsync(model, apiKey, lostText);
                var foundEmbedding = await GetEmbeddingAsync(model, apiKey, foundText);

                if (lostEmbedding.Count == 0 || foundEmbedding.Count == 0)
                {
                    return -1;
                }

                return CosineSimilarity(lostEmbedding, foundEmbedding);
            }
            catch
            {
                return -1;
            }
        }

        private async Task<List<double>> GetEmbeddingAsync(string model, string apiKey, string text)
        {
            if (embeddingCache.TryGetValue(text, out var cached))
            {
                return cached;
            }

            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var requestBody = new
            {
                inputs = text,
                options = new
                {
                    wait_for_model = true
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var endpoint = $"https://api-inference.huggingface.co/pipeline/feature-extraction/{model}";

            using var response = await client.PostAsync(endpoint, content);
            if (!response.IsSuccessStatusCode)
            {
                return new List<double>();
            }

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);

            var numbers = new List<double>();
            ExtractNumbers(doc.RootElement, numbers);

            if (numbers.Count == 0)
            {
                return numbers;
            }

            embeddingCache[text] = numbers;
            return numbers;
        }

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

        private static double CalculateRuleScore(Lost lost, Found found)
        {
            var itemNameScore = HybridTextScore(lost.itemName, found.itemName);
            var descriptionScore = HybridTextScore(lost.description, found.description);
            var locationScore = JaccardSimilarity(Tokenize(lost.location), Tokenize(found.location));

            var categoryScore = string.Equals(lost.category, found.category, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            var brandScore = string.Equals(lost.brand, found.brand, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            var colorScore = string.Equals(lost.color, found.color, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

            var dayDiff = Math.Abs((lost.dateLost.Date - found.dateFound.Date).TotalDays);
            var dateScore = dayDiff switch
            {
                <= 1 => 1.0,
                <= 3 => 0.7,
                <= 7 => 0.4,
                _ => 0.0
            };

            return (itemNameScore * 0.30)
                 + (descriptionScore * 0.20)
                 + (locationScore * 0.15)
                 + (categoryScore * 0.15)
                 + (brandScore * 0.08)
                 + (colorScore * 0.07)
                 + (dateScore * 0.05);
        }

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
