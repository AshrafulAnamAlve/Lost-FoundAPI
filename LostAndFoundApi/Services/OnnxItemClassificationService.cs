using LostAndFoundApi.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LostAndFoundApi.Services
{
    // Runs the item image classifier inside this process, straight from
    // MLModels/model.onnx - no Python, no second service, nothing to keep alive.
    //
    // Why this exists next to ItemClassificationService
    // ------------------------------------------------
    // The HTTP one calls /ml_service, which is a Python app. The API is hosted on
    // Windows shared hosting that runs .NET and nothing else, so on the live site
    // that URL points at a localhost port with nothing behind it and classification
    // never happens. ONNX Runtime is a plain NuGet package, so the same model file
    // the Python service loads can be run here instead and ship with the ordinary
    // publish.
    //
    // Both implementations stay: ItemClassificationService is still the right answer
    // when the model is hosted somewhere with more memory, and it is selected by
    // setting ImageClassification:Provider to "service" (see Program.cs).
    //
    // Everything is best-effort, exactly as in the HTTP implementation: any failure
    // - no model file, corrupt export, unreadable upload - resolves to a result with
    // Error set, never an exception, and the item posts as if this did not exist.
    public class OnnxItemClassificationService : IItemClassificationService
    {
        // Kept in step with ml_service/classifier.py, which is the same model run the
        // other way. If the export changes, both read the new files; neither
        // hard-codes class order, tensor names or input size.
        private const double FallbackThreshold = 0.65;

        private readonly IConfiguration configuration;
        private readonly IHostEnvironment environment;
        private readonly ILogger<OnnxItemClassificationService> logger;

        // Guards loading only. InferenceSession.Run is thread-safe, so requests share
        // one session rather than paying for a new one each time - loading the graph
        // is the expensive part, inference is not.
        private readonly object loadLock = new();

        private InferenceSession? session;
        private string[] classNames = [];
        private string inputName = "";
        private string outputName = "";
        private int inputHeight;
        private int inputWidth;
        private double threshold = FallbackThreshold;
        private string? loadError;
        private bool loadAttempted;

        public OnnxItemClassificationService(
            IConfiguration configuration,
            IHostEnvironment environment,
            ILogger<OnnxItemClassificationService> logger)
        {
            this.configuration = configuration;
            this.environment = environment;
            this.logger = logger;
        }

        // Resolved against the content root so a publish carries them, while an
        // absolute path in configuration still wins - which is how you point a host
        // at a model kept outside the deployment.
        private string ModelPath => ResolvePath("ImageClassification:ModelPath", "model.onnx");
        private string ClassNamesPath => ResolvePath("ImageClassification:ClassNamesPath", "class_names.json");
        private string ConfigPath => ResolvePath("ImageClassification:ConfigPath", "config.json");

        private string ResolvePath(string key, string defaultFileName)
        {
            var configured = configuration[key];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(environment.ContentRootPath, configured);
            }

            return Path.Combine(environment.ContentRootPath, "MLModels", defaultFileName);
        }

        // Cheap enough to answer on every upload: the caller uses it to skip building
        // a request at all, so it must not load the model to decide.
        public bool IsConfigured => File.Exists(ModelPath);

        public ClassifierStatus Describe()
        {
            if (!IsConfigured)
            {
                return new ClassifierStatus
                {
                    Provider = "onnx (in-process)",
                    Configured = false,
                    Ready = false,
                    Source = Path.GetFileName(ModelPath),
                    Error = $"no model file at {Path.GetFileName(ModelPath)}",
                    Runtime = RuntimeDescription(),
                };
            }

            EnsureLoaded();

            return new ClassifierStatus
            {
                Provider = "onnx (in-process)",
                Configured = true,
                Ready = session is not null,
                Source = Path.GetFileName(ModelPath),
                Classes = classNames,
                Threshold = threshold,
                Error = loadError,
                Runtime = RuntimeDescription(),
            };
        }

        // Loads the session once. A failed attempt is not retried: the causes are all
        // static (missing file, bad export, class count mismatch), so retrying would
        // just pay the cost again on every upload.
        private void EnsureLoaded()
        {
            if (loadAttempted) return;

            lock (loadLock)
            {
                if (loadAttempted) return;
                loadAttempted = true;

                try
                {
                    if (!File.Exists(ModelPath))
                    {
                        loadError = $"no model at {ModelPath}";
                        logger.LogInformation(
                            "Image classifier not present ({Path}); items will post without a detected category.",
                            ModelPath);
                        return;
                    }

                    if (!File.Exists(ClassNamesPath))
                    {
                        loadError = $"no class names at {ClassNamesPath}";
                        logger.LogError("Image classifier class names missing: {Path}", ClassNamesPath);
                        return;
                    }

                    // Class order defines the meaning of every output index, so it is
                    // read from the file and never assumed. Reordering class_names.json
                    // without re-exporting the model mislabels every prediction.
                    var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(ClassNamesPath));

                    if (names is null || names.Length == 0)
                    {
                        loadError = $"{Path.GetFileName(ClassNamesPath)} must contain a non-empty JSON array";
                        logger.LogError("Image classifier class names malformed: {Error}", loadError);
                        return;
                    }

                    var config = ReadConfig();

                    // Threads are capped because this runs on shared hosting with very
                    // few cores. One image through MobileNetV2 is small work; letting
                    // ONNX Runtime spin up a thread per core costs more than it saves
                    // and competes with the web server for the same cores.
                    // Fully qualified: ASP.NET has a SessionOptions of its own, for
                    // HTTP sessions, and ImplicitUsings pulls it into scope here.
                    // ONNX Runtime publishes no win-x86 build, so a 32-bit app pool
                    // cannot load it however the files are arranged. Say that plainly
                    // rather than letting it surface as a TypeInitializationException
                    // that reads like a missing dependency.
                    if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
                    {
                        loadError =
                            "the app pool is running 32-bit, and ONNX Runtime ships no win-x86 build - "
                            + "switch the site to a 64-bit app pool, or host the model out of process";
                        logger.LogError("Image classifier: {Error}", loadError);
                        return;
                    }

                    var options = new Microsoft.ML.OnnxRuntime.SessionOptions
                    {
                        IntraOpNumThreads = 1,
                        InterOpNumThreads = 1,
                    };

                    var loaded = new InferenceSession(ModelPath, options);

                    // Prefer the names the export documented; fall back to whatever the
                    // graph declares, so a re-export cannot break this on a rename.
                    var configuredInput = GetString(config, "input_name");
                    var configuredOutput = GetString(config, "output_name");

                    inputName = configuredInput is not null && loaded.InputMetadata.ContainsKey(configuredInput)
                        ? configuredInput
                        : loaded.InputMetadata.Keys.First();

                    outputName = configuredOutput is not null && loaded.OutputMetadata.ContainsKey(configuredOutput)
                        ? configuredOutput
                        : loaded.OutputMetadata.Keys.First();

                    (inputHeight, inputWidth) = ReadInputSize(config, loaded.InputMetadata[inputName]);

                    if (inputHeight <= 0 || inputWidth <= 0)
                    {
                        loaded.Dispose();
                        loadError = "could not determine the model's input size";
                        logger.LogError("Image classifier: {Error}", loadError);
                        return;
                    }

                    // [N, classes] - the last dimension must match class_names.json or
                    // every label is off by however far the two disagree.
                    var declared = loaded.OutputMetadata[outputName].Dimensions.LastOrDefault();
                    if (declared > 0 && declared != names.Length)
                    {
                        loaded.Dispose();
                        loadError =
                            $"model outputs {declared} classes but class_names.json lists {names.Length} - these must agree";
                        logger.LogError("Image classifier mismatch: {Error}", loadError);
                        return;
                    }

                    threshold = GetDouble(config, "recommended_threshold") ?? FallbackThreshold;
                    classNames = names;
                    session = loaded;
                    loadError = null;

                    logger.LogInformation(
                        "Image classifier ready in-process: {Model} ({Width}x{Height}, {Count} classes: {Classes}, threshold {Threshold:F2})",
                        Path.GetFileName(ModelPath), inputWidth, inputHeight, names.Length,
                        string.Join(", ", names), threshold);
                }
                catch (Exception ex)
                {
                    loadError = Explain(ex);
                    logger.LogError(ex, "Image classifier failed to load: {Error}", loadError);
                }
            }
        }

        private Dictionary<string, JsonElement> ReadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return [];

                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(ConfigPath)) ?? [];
            }
            catch (Exception ex)
            {
                // config.json only carries hints - every value in it has a fallback
                // read off the graph itself, so a malformed file is not fatal.
                logger.LogWarning(
                    "Ignoring unreadable {File}: {Message}", Path.GetFileName(ConfigPath), ex.Message);
                return [];
            }
        }

        private static string? GetString(Dictionary<string, JsonElement> config, string key)
        {
            if (config.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }

        private static double? GetDouble(Dictionary<string, JsonElement> config, string key) =>
            config.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.Number
                ? element.GetDouble()
                : null;

        private static (int Height, int Width) ReadInputSize(
            Dictionary<string, JsonElement> config, NodeMetadata graphInput)
        {
            if (config.TryGetValue("input_size", out var size)
                && size.ValueKind == JsonValueKind.Array
                && size.GetArrayLength() == 2)
            {
                var values = size.EnumerateArray().ToArray();
                if (values[0].TryGetInt32(out var height) && values[1].TryGetInt32(out var width))
                {
                    return (height, width);
                }
            }

            // [N, H, W, C] - the batch dimension is dynamic (-1), H and W are not.
            var dimensions = graphInput.Dimensions;
            return dimensions.Length == 4 ? (dimensions[1], dimensions[2]) : (0, 0);
        }

        public async Task<ImageClassificationResult> ClassifyAsync(
            Stream image,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            EnsureLoaded();

            if (session is null)
            {
                return Failed(loadError ?? "classifier not loaded");
            }

            float[] pixels;
            try
            {
                pixels = await ReadPixelsAsync(image, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // An upload that is not a usable image is the caller's problem, not a
                // server fault: report it and let the item post without a category.
                logger.LogInformation(
                    "Could not read {File} as an image: {Type}: {Message}",
                    fileName, ex.GetType().Name, ex.Message);
                return Failed($"image could not be read ({ex.GetType().Name})");
            }

            try
            {
                var input = new DenseTensor<float>(pixels, [1, inputHeight, inputWidth, 3]);

                using var results = session.Run(
                    [NamedOnnxValue.CreateFromTensor(inputName, input)],
                    [outputName]);

                var row = results.First().AsEnumerable<float>().ToArray();

                if (row.Length != classNames.Length)
                {
                    return Failed($"model returned {row.Length} scores for {classNames.Length} classes");
                }

                var probabilities = AsProbabilities(row);

                var best = 0;
                for (var i = 1; i < probabilities.Length; i++)
                {
                    if (probabilities[i] > probabilities[best]) best = i;
                }

                var confidence = Math.Round(probabilities[best], 4);
                var known = confidence >= threshold;

                // Below the threshold the model is guessing, so it names nothing and
                // the caller asks the user instead. The confidence is still reported
                // so the UI can explain why it is asking.
                var label = known ? classNames[best] : null;
                var category = ItemClassificationService.CategoryForLabel(label);

                // A label the mapping does not recognise means class_names.json and
                // LabelToCategory have drifted apart - worth saying out loud, because
                // the prediction is otherwise silently discarded.
                if (known && label is not null && category is null)
                {
                    logger.LogWarning(
                        "Classifier returned label '{Label}', which maps to no app category. "
                        + "Update LabelToCategory in ItemClassificationService.", label);
                }

                var scores = new Dictionary<string, double>(classNames.Length);
                for (var i = 0; i < classNames.Length; i++)
                {
                    scores[classNames[i]] = Math.Round(probabilities[i], 4);
                }

                logger.LogDebug(
                    "Classified {File} in-process: label={Label} confidence={Confidence:F4} known={Known} category={Category}",
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
            catch (Exception ex)
            {
                logger.LogError(
                    "In-process image classification failed: {Type}: {Message}",
                    ex.GetType().Name, ex.Message);
                return Failed($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Decodes, resizes to the model's input size and returns RAW 0-255 float32 in
        // NHWC order.
        //
        // DO NOT NORMALIZE HERE. NO /255. NO (x/127.5 - 1). NO mean/std subtraction.
        //
        // This export carries its own preprocessing inside the graph (a TrueDivide
        // followed by a Subtract, right after the input node), so it expects plain
        // pixel values in [0, 255]. Normalizing here would scale the input twice.
        //
        // That mistake does not raise - the shapes and dtype stay valid, inference
        // still returns four probabilities, and they are simply wrong. There is no
        // error to notice, so the only defence is this comment. It is missing on
        // purpose. (MLModels/config.json records the same thing - "preprocessing":
        // "internal" - and so does the identical note in ml_service/classifier.py.)
        private async Task<float[]> ReadPixelsAsync(Stream image, CancellationToken cancellationToken)
        {
            using var decoded = await Image.LoadAsync<Rgb24>(image, cancellationToken);

            // Stretch, not fit: the Python side resizes with PIL's Image.resize, which
            // ignores aspect ratio, and the model was trained that way. Preserving
            // aspect here would letterbox the subject and shift every prediction.
            decoded.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(inputWidth, inputHeight),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Bicubic,
            }));

            var pixels = new float[inputHeight * inputWidth * 3];

            decoded.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var sourceRow = accessor.GetRowSpan(y);
                    var offset = y * inputWidth * 3;

                    for (var x = 0; x < sourceRow.Length; x++)
                    {
                        var pixel = sourceRow[x];
                        var index = offset + (x * 3);

                        pixels[index] = pixel.R;
                        pixels[index + 1] = pixel.G;
                        pixels[index + 2] = pixel.B;
                    }
                }
            });

            return pixels;
        }

        // The export ends in softmax, so these are already probabilities. Only
        // normalise if a future re-export emits raw logits instead.
        private static double[] AsProbabilities(float[] row)
        {
            var sum = 0.0;
            foreach (var value in row) sum += value;

            if (Math.Abs(sum - 1.0) <= 1e-3)
            {
                return Array.ConvertAll(row, value => (double)value);
            }

            var max = row.Max();
            var exponentiated = Array.ConvertAll(row, value => Math.Exp(value - max));
            var total = exponentiated.Sum();

            return total > 0
                ? Array.ConvertAll(exponentiated, value => value / total)
                : Array.ConvertAll(row, value => (double)value);
        }

        // A native library that will not load surfaces as a TypeInitializationException
        // whose own message says nothing useful - the reason is always one level down,
        // and sometimes two. Flatten the chain so /api/health carries the whole thing:
        // on a host you cannot log into, this is the only copy of it you will ever see.
        private static string Explain(Exception exception)
        {
            var parts = new List<string>();

            for (var current = exception; current is not null; current = current.InnerException)
            {
                parts.Add($"{current.GetType().Name}: {current.Message}");
            }

            return string.Join(" <- ", parts);
        }

        // Both halves matter: ONNX Runtime has no win-x86 build, so a 32-bit pool can
        // never work, and the framework version pins down which C runtime is expected.
        private static string RuntimeDescription() =>
            $"{RuntimeInformation.ProcessArchitecture} process, {RuntimeInformation.FrameworkDescription}, "
            + $"{RuntimeInformation.OSDescription}";

        private static ImageClassificationResult Failed(string error) =>
            new() { Known = false, Error = error };
    }
}
