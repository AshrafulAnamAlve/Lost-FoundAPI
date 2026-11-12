using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Globalization;
using System.Text;

namespace LostAndFoundApi.Services
{
    public interface ITextEmbeddingService
    {
        /// <summary>True when the model files are on disk. Cheap - does not load anything.</summary>
        bool IsConfigured { get; }

        /// <summary>One L2-normalised vector per text, or null if the model could not run.</summary>
        List<List<double>>? Embed(IReadOnlyList<string> texts);

        /// <summary>Why the last load or run failed, if it did.</summary>
        string? LastError { get; }

        string Source { get; }
    }

    // Runs all-MiniLM-L6-v2 - the same sentence-embedding model the HuggingFace API
    // and /ml_service serve - inside this process through ONNX Runtime.
    //
    // Why: the hosted API used HuggingFace Inference Providers, which is metered. When
    // the account's credits ran out every request came back 402 and matching silently
    // fell back to rules only. Running the model here needs no key, no network and no
    // credits, and ships with an ordinary publish exactly like the image classifier.
    //
    // MLModels/text/minilm.onnx is the full-precision export with mean pooling and L2
    // normalisation built into the graph, so its vectors match sentence-transformers to
    // five decimal places. That matters: the calibration floor/ceiling and the
    // item-name floor in ItemSimilarityService were measured on those exact vectors.
    // An int8 export was tried and rejected - it moved pair cosines by up to 0.03,
    // more than the gap between the closest true and false pairs.
    public class OnnxTextEmbeddingService : ITextEmbeddingService
    {
        // The model was trained with sequences of up to 256 word pieces. Item texts are
        // far shorter; this only stops a pasted essay from costing a second of CPU.
        private const int MaxTokens = 256;

        private readonly IHostEnvironment environment;
        private readonly IConfiguration configuration;
        private readonly ILogger<OnnxTextEmbeddingService> logger;
        private readonly object loadLock = new();

        private InferenceSession? session;
        private WordPieceTokenizer? tokenizer;
        private bool loadAttempted;

        public OnnxTextEmbeddingService(
            IHostEnvironment environment,
            IConfiguration configuration,
            ILogger<OnnxTextEmbeddingService> logger)
        {
            this.environment = environment;
            this.configuration = configuration;
            this.logger = logger;
        }

        private string Folder
        {
            get
            {
                var configured = configuration["Embedding:OnnxFolder"];
                if (string.IsNullOrWhiteSpace(configured))
                    return Path.Combine(environment.ContentRootPath, "MLModels", "text");
                return Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
            }
        }

        private string ModelPath => Path.Combine(Folder, "minilm.onnx");
        private string VocabPath => Path.Combine(Folder, "vocab.txt");

        // Setting Embedding:InProcess to false turns this provider off without
        // removing the files, e.g. to compare against the HTTP providers.
        public bool IsConfigured =>
            !string.Equals(configuration["Embedding:InProcess"], "false", StringComparison.OrdinalIgnoreCase)
            && File.Exists(ModelPath) && File.Exists(VocabPath);

        public string? LastError { get; private set; }

        public string Source => "MLModels/text/minilm.onnx";

        private bool EnsureLoaded()
        {
            if (session is not null && tokenizer is not null) return true;
            if (loadAttempted) return false;

            lock (loadLock)
            {
                if (session is not null) return true;
                if (loadAttempted) return false;
                loadAttempted = true;

                try
                {
                    tokenizer = WordPieceTokenizer.Load(VocabPath);
                    using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    session = new InferenceSession(ModelPath, options);
                    LastError = null;
                    logger.LogInformation("Text embedding model loaded in-process from {Path}.", ModelPath);
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    logger.LogWarning(ex, "Could not load the in-process text embedding model.");
                    session = null;
                    return false;
                }
            }
        }

        public List<List<double>>? Embed(IReadOnlyList<string> texts)
        {
            if (texts.Count == 0) return new List<List<double>>();
            if (!IsConfigured || !EnsureLoaded()) return null;

            try
            {
                var encoded = texts.Select(t => tokenizer!.Encode(t ?? "", MaxTokens)).ToArray();
                var vectors = new List<double>[encoded.Length];

                // Run similar lengths together: a batch is padded to its longest text,
                // so one long description in a batch of short names would make every
                // name in it cost as much as the description.
                var order = Enumerable.Range(0, encoded.Length).OrderBy(i => encoded[i].Length).ToArray();
                for (var offset = 0; offset < order.Length; offset += BatchSize)
                {
                    var chunk = order.Skip(offset).Take(BatchSize).ToArray();
                    var chunkVectors = RunBatch(chunk.Select(i => encoded[i]).ToArray());
                    for (var k = 0; k < chunk.Length; k++) vectors[chunk[k]] = chunkVectors[k];
                }

                LastError = null;
                return vectors.ToList();
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                logger.LogWarning(ex, "In-process text embedding failed.");
                return null;
            }
        }

        private const int BatchSize = 8;

        private List<double>[] RunBatch(long[][] encoded)
        {
            var length = encoded.Max(e => e.Length);
            var batch = encoded.Length;

            var ids = new DenseTensor<long>(new[] { batch, length });
            var mask = new DenseTensor<long>(new[] { batch, length });
            var types = new DenseTensor<long>(new[] { batch, length });

            for (var b = 0; b < batch; b++)
            {
                for (var i = 0; i < encoded[b].Length; i++)
                {
                    ids[b, i] = encoded[b][i];
                    mask[b, i] = 1;
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", ids),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", types),
            };

            using var results = session!.Run(inputs);
            var output = results.First().AsTensor<float>();
            var dims = output.Dimensions[1];

            var vectors = new List<double>[batch];
            for (var b = 0; b < batch; b++)
            {
                var v = new List<double>(dims);
                for (var d = 0; d < dims; d++) v.Add(output[b, d]);
                vectors[b] = v;
            }
            return vectors;
        }
    }

    // The uncased BERT tokenizer all-MiniLM-L6-v2 was trained with, written out so the
    // API needs no extra package. Mirrors HuggingFace's BertTokenizer step for step -
    // clean, split CJK, lowercase, strip accents, split punctuation, then greedy
    // longest-match WordPiece - because a token that differs from training produces a
    // different vector, and the scoring thresholds assume the trained one.
    internal sealed class WordPieceTokenizer
    {
        private const int Cls = 101, Sep = 102, Unk = 100;
        private const int MaxCharsPerWord = 100;

        private readonly Dictionary<string, int> vocab;

        private WordPieceTokenizer(Dictionary<string, int> vocab) => this.vocab = vocab;

        public static WordPieceTokenizer Load(string path)
        {
            var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = 0;
            foreach (var line in File.ReadLines(path))
            {
                vocab.TryAdd(line.TrimEnd('\r', '\n'), index);
                index++;
            }
            return new WordPieceTokenizer(vocab);
        }

        public long[] Encode(string text, int maxTokens)
        {
            var pieces = new List<long> { Cls };
            foreach (var word in BasicTokenize(text))
            {
                foreach (var id in WordPiece(word))
                {
                    if (pieces.Count >= maxTokens - 1) break;
                    pieces.Add(id);
                }
            }
            pieces.Add(Sep);
            return pieces.ToArray();
        }

        private IEnumerable<int> WordPiece(string word)
        {
            if (word.Length > MaxCharsPerWord) { yield return Unk; yield break; }

            var ids = new List<int>();
            var start = 0;
            while (start < word.Length)
            {
                var end = word.Length;
                var found = -1;
                while (start < end)
                {
                    var piece = word.Substring(start, end - start);
                    if (start > 0) piece = "##" + piece;
                    if (vocab.TryGetValue(piece, out var id)) { found = id; break; }
                    end--;
                }
                if (found < 0) { yield return Unk; yield break; }
                ids.Add(found);
                start = end;
            }
            foreach (var id in ids) yield return id;
        }

        private static IEnumerable<string> BasicTokenize(string text)
        {
            // Clean: drop NUL, U+FFFD and control characters; turn every kind of
            // whitespace into a plain space. Then put spaces around CJK ideographs.
            var cleaned = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                var cp = rune.Value;
                if (cp == 0 || cp == 0xFFFD || IsControl(rune)) continue;
                if (IsWhitespace(rune)) { cleaned.Append(' '); continue; }
                if (IsCjk(cp)) { cleaned.Append(' ').Append(rune.ToString()).Append(' '); continue; }
                cleaned.Append(rune.ToString());
            }

            foreach (var raw in cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // Strip accents (decompose, drop combining marks), THEN lowercase - the
                // order HuggingFace uses. The other way round "İ" lowercases to itself
                // in .NET and ends up as an unknown token.
                var decomposed = raw.Normalize(NormalizationForm.FormD);
                var stripped = new StringBuilder(decomposed.Length);
                foreach (var rune in decomposed.EnumerateRunes())
                {
                    if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) continue;
                    stripped.Append(rune.ToString());
                }
                var lowered = stripped.ToString().ToLowerInvariant();

                // Every punctuation character becomes a token of its own.
                var current = new StringBuilder();
                foreach (var rune in lowered.EnumerateRunes())
                {
                    if (IsPunctuation(rune))
                    {
                        if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                        yield return rune.ToString();
                    }
                    else
                    {
                        current.Append(rune.ToString());
                    }
                }
                if (current.Length > 0) yield return current.ToString();
            }
        }

        private static bool IsWhitespace(Rune r)
        {
            if (r.Value is ' ' or '\t' or '\n' or '\r') return true;
            return Rune.GetUnicodeCategory(r) == UnicodeCategory.SpaceSeparator;
        }

        private static bool IsControl(Rune r)
        {
            if (r.Value is '\t' or '\n' or '\r') return false;
            var c = Rune.GetUnicodeCategory(r);
            return c is UnicodeCategory.Control or UnicodeCategory.Format;
        }

        private static bool IsPunctuation(Rune r)
        {
            var cp = r.Value;
            if ((cp >= 33 && cp <= 47) || (cp >= 58 && cp <= 64) || (cp >= 91 && cp <= 96) || (cp >= 123 && cp <= 126))
                return true;
            return Rune.GetUnicodeCategory(r) switch
            {
                UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
                UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
                UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
                UnicodeCategory.OtherPunctuation => true,
                _ => false,
            };
        }

        private static bool IsCjk(int cp) =>
            (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) ||
            (cp >= 0x20000 && cp <= 0x2A6DF) || (cp >= 0x2A700 && cp <= 0x2B73F) ||
            (cp >= 0x2B740 && cp <= 0x2B81F) || (cp >= 0x2B820 && cp <= 0x2CEAF) ||
            (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0x2F800 && cp <= 0x2FA1F);
    }
}
