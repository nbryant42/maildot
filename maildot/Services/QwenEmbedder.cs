using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tokenizers.DotNet;

namespace maildot.Services;

/// <summary>
/// Embedder for the Qwen family of models using ONNX Runtime.
/// 
/// Unlike HuggingFace's transformers library, this embedder uses ONNX Runtime directly, so it's fairly
/// low-level code which needs to handle tokenization, batching, padding, and pooling manually.
/// Therefore, this class is at least somewhat specific to the Qwen embedding model architecture (assumes
/// left-padding, output in last_hidden_state, last-token pooling, hardcodes the <|endoftext|> padding token).
/// </summary>
public partial class QwenEmbedder : IDisposable
{
    private readonly InferenceSession _sess;
    private readonly Tokenizer _tok;
    private readonly int _maxLen;
    private readonly long _padId;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private static readonly SemaphoreSlim SharedLock = new(1, 1);
    private static QwenEmbedder? _sharedInstance;
    private static bool _sharedUseGpu = true;

    // upper bound on batch_size * seq_len to both avoid OOM and prevent allocations from spilling into shared memory.
    // note there is some room for debate about the optimal value here; in theory, bigger batches improve GPU
    // utilization, but reducing batch size to 16K does not appear to cost much performance on a Geforce RTX 4070.
    private const int MaxTokensPerBatch = 20 * 1024;

    private const string DefaultQueryInstruction = "Given a mailbox and a search query, find emails whose subject or " +
        "body are most relevant to the topic of the query, even if they don't explicitly answer a question.";

    public const string ModelId = "onnx-community/Qwen3-Embedding-0.6B-ONNX";
    const string tokFile = "tokenizer.json";

    private static readonly string hfCacheDir =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "maildot", "hf");

    /// <summary>
    /// do not call except from unit tests. the production app should use GetSharedAsync().
    /// </summary>
    internal static async Task<QwenEmbedder> Build(string modelDir, int maxLen = 1024, bool useGpu = true)
    {
        // disabled; none of the auto-downloadable providers are working for me as of WinAppSDK 2.0.0-experimental3.
#if false
        // Create a new instance of EnvironmentCreationOptions
        EnvironmentCreationOptions envOptions = new()
        {
            logId = "maildot",
            logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };

        // Pass the options by reference to CreateInstanceWithOptions
        OrtEnv ortEnv = OrtEnv.CreateInstanceWithOptions(ref envOptions);

        // Use Windows ML to download and register Execution Providers
        var catalog = ExecutionProviderCatalog.GetDefault();
        Debug.WriteLine("Ensuring and registering execution providers...");

        foreach (var p in catalog.FindAllProviders())
        {
            Debug.WriteLine($"Found EP: {p.Name}, ReadyState: {p.ReadyState}");

            if (!p.Name.Equals("OpenVINOExecutionProvider")) // crashes on registration on my machine
            {
                // Download it
                var result = await p.EnsureReadyAsync();

                // If download succeeded
                if (result != null && result.Status == ExecutionProviderReadyResultState.Success)
                {
                    // Register it
                    p.TryRegister();
                }
            }
        }
#endif

        var tok = new Tokenizer(vocabPath: await HuggingFace.GetFileFromHub(ModelId, tokFile, hfCacheDir, false));
        var onnxDir = Path.Combine(hfCacheDir, ModelId, "onnx");
        Directory.CreateDirectory(onnxDir);
        var so = new SessionOptions();

        // Disabled; none of the auto-downloadable providers are working for me as of WinAppSDK 2.0.0-experimental3.
        // NvTensorRTRTXExecutionProvider, if appended here, causes a StackOverflowException on `new InferenceSession`.
        // This can be worked around by running the `new InferenceSession` in a `new Thread` with 8MB stack size, but
        // it still runs out of memory downstream, during the compilation phase, allocating at least 128GB of memory.
        // Might be related to the fact that it prints many errors about unsupported op types; the logs themselves
        // consume memory?
#if false
        foreach (var dev in ortEnv.GetEpDevices())
        {
            Debug.WriteLine($"Dev: {dev.EpName}");
            if (dev.EpName.Equals("NvTensorRTRTXExecutionProvider"))
            {
                so.AppendExecutionProvider(ortEnv, [dev], new Dictionary<string, string>());
            }
        }
#endif

        // Must register DML before CPU. Use both; ONNX Runtime will use CPU for certain node types if deemed optimal.
        if (useGpu)
        {
            try { so.AppendExecutionProvider_DML(); } catch (OnnxRuntimeException e) { Debug.WriteLine(e); }
        }
        so.AppendExecutionProvider_CPU();
        so.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MAX_PERFORMANCE);

        await HuggingFace.GetFileFromHub(ModelId, "onnx/model_fp16.onnx_data", hfCacheDir, false);
        var path = await HuggingFace.GetFileFromHub(ModelId, "onnx/model_fp16.onnx", hfCacheDir, false);
        var sess = new InferenceSession(path, so);

        // TODO <|endoftext|> seems to encode to two tokens, this may be incorrect.
        return new(sess, tok, maxLen, tok.Encode("<|endoftext|>").FirstOrDefault());
    }

    internal static Task<QwenEmbedder?> GetSharedAsync(bool? useGpu = null, int maxLen = 1024)
    {
        return Task.Run(() => GetSharedAsyncInternal(useGpu, maxLen));
    }

    private static async Task<QwenEmbedder?> GetSharedAsyncInternal(bool? useGpu, int maxLen)
    {
        var desiredUseGpu = useGpu ?? _sharedUseGpu;
        if (_sharedInstance != null && _sharedUseGpu == desiredUseGpu)
        {
            return _sharedInstance;
        }

        await SharedLock.WaitAsync();
        try
        {
            desiredUseGpu = useGpu ?? _sharedUseGpu;
            if (_sharedInstance != null && _sharedUseGpu == desiredUseGpu)
            {
                return _sharedInstance;
            }

            QwenEmbedder? built = null;
            try
            {
                built = await Build(ModelId, maxLen, desiredUseGpu);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize shared Qwen embedder: {ex}");
            }

            if (built != null)
            {
                var old = _sharedInstance;
                _sharedInstance = built;
                _sharedUseGpu = desiredUseGpu;
                old?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to initialize shared Qwen embedder: {ex}");
        }
        finally
        {
            SharedLock.Release();
        }

        return _sharedInstance;
    }

    private QwenEmbedder(InferenceSession sess, Tokenizer tok, int maxLen, long padId)
    {
        _sess = sess;
        _tok = tok;
        _maxLen = maxLen;
        _padId = padId;
    }

    public Float16[][] EmbedBatch(IEnumerable<string> texts)
    {
        _sessionLock.Wait();
        try
        {
        var list = texts.ToList();
        if (list.Count == 0) return [];

        // Encode, keep original indices, and sort by length descending for dense packing.
        var encoded = list
            .Select((t, idx) => new { idx, tokens = _tok.Encode(t).Select(x => (long)x).ToArray() })
            .OrderByDescending(x => x.tokens.Length)
            .ToList();

        if (!_sess.OutputMetadata.ContainsKey("last_hidden_state"))
        {
            throw new InvalidOperationException("Model output 'last_hidden_state' is missing.");
        }

        var outputs = new Float16[list.Count][];

        int pos = 0;
        while (pos < encoded.Count)
        {
            int remaining = encoded.Count - pos;
            int longest = encoded[pos].tokens.Length;
            int seqLen = Math.Min(_maxLen, Math.Max(1, longest));

            int maxBatch = Math.Max(1, MaxTokensPerBatch / seqLen);
            int batchCount = Math.Min(maxBatch, remaining);

            var idsBatch = new long[batchCount * seqLen];
            var maskBatch = new long[batchCount * seqLen];

            for (int i = 0; i < batchCount; i++)
            {
                var encodedIds = encoded[pos + i].tokens.Take(seqLen).ToArray();

                // Left-pad so the final token sits at the right edge; last-token pooling will use that.
                var ids = PadLeft(encodedIds, seqLen, _padId);
                var ms = new long[seqLen];
                var used = Math.Min(encodedIds.Length, seqLen);
                int offset = seqLen - used;
                for (var j = 0; j < used; j++) ms[offset + j] = 1;

                Array.Copy(ids, 0, idsBatch, i * seqLen, seqLen);
                Array.Copy(ms, 0, maskBatch, i * seqLen, seqLen);
            }

            var inputs = new List<NamedOnnxValue> {
                NamedOnnxValue.CreateFromTensor("input_ids",
                    new DenseTensor<long>(idsBatch,  [batchCount, seqLen])),
                NamedOnnxValue.CreateFromTensor("attention_mask",
                    new DenseTensor<long>(maskBatch, [batchCount, seqLen])),
            };

            if (_sess.InputMetadata.TryGetValue("position_ids", out var _))
            {
                // position_ids shape [batch, seq_len], dtype int64
                var posTensor = new long[batchCount * seqLen];
                for (int b = 0; b < batchCount; b++)
                {
                    for (int j = 0; j < seqLen; j++) posTensor[b * seqLen + j] = j;
                }
                inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids",
                    new DenseTensor<long>(posTensor, [batchCount, seqLen])));
            }

            // Add dummy past_key_values.* if present
            foreach (var kv in _sess.InputMetadata.Where(k => k.Key.Contains("past_key_values")))
            {
                var md = kv.Value;
                // md.Dimensions is typically [batch, num_kv_heads, past_seq_len, head_dim] with -1 for dynamic dims.
                int[] dims = md.Dimensions;
                int b = batchCount;
                int numKvHeads = dims.Length > 1 && dims[1] > 0 ? dims[1] : /* model default */ 8;
                int headDim = dims.Length > 3 && dims[3] > 0 ? dims[3] : /* model default */ 128;
                var shape = new[] { b, numKvHeads, 0, headDim };

                switch (md.ElementDataType)
                {
                    case TensorElementType.Float16:
                        inputs.Add(NamedOnnxValue.CreateFromTensor(kv.Key,
                            new DenseTensor<Float16>(Array.Empty<Float16>(), shape)));
                        break;
                    default:
                        inputs.Add(NamedOnnxValue.CreateFromTensor(kv.Key,
                            new DenseTensor<float>(Array.Empty<float>(), shape)));
                        break;
                }
            }

            using var results = _sess.Run(inputs, ["last_hidden_state"]);

            Debug.WriteLine($"Processed batch: {batchCount}x{seqLen}");

            var last = results.FirstOrDefault(r => r.Name == "last_hidden_state")
                ?? throw new InvalidOperationException("Model output 'last_hidden_state' not found in results.");

            var arr = PoolLastToken(last.Value, maskBatch, seqLen);

            for (int i = 0; i < arr.Length; i++)
            {
                var origIdx = encoded[pos + i].idx;
                outputs[origIdx] = arr[i];
            }

            pos += batchCount;
        }

        return outputs;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public Float16[]? EmbedQuery(string query) =>
        EmbedBatch([BuildQueryPrompt(query)]).FirstOrDefault();

    private static string BuildQueryPrompt(string query) =>
        $"Instruct: {DefaultQueryInstruction}\nQuery:{query}";

    static long[] PadLeft(long[] a, int len, long pad)
    {
        if (a.Length >= len) return [.. a.TakeLast(len)];
        var b = new long[len]; Array.Fill(b, pad);
        Array.Copy(a, 0, b, len - a.Length, a.Length);
        return b;
    }
    static void L2NormalizeInPlace(float[] v)
    {
        double s = 0; foreach (var x in v) s += x * x;
        float inv = (float)(1.0 / Math.Sqrt(s + 1e-12));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    static Float16[][] PoolLastToken(object lastHidden, long[] mask, int seqLen)
    {
        return lastHidden switch
        {
            DenseTensor<float> tf => PoolFloat(tf, mask, seqLen),
            DenseTensor<Float16> tf16 => PoolFloat(ToFloatTensor(tf16), mask, seqLen),
            _ => throw new InvalidOperationException(
                $"Unexpected last_hidden_state type: {lastHidden?.GetType().FullName}")
        };
    }

    static DenseTensor<float> ToFloatTensor(DenseTensor<Float16> src)
    {
        var dst = new DenseTensor<float>(new float[src.Buffer.Length], src.Dimensions);
        var srcSpan = src.Buffer.Span;
        var dstSpan = dst.Buffer.Span;
        for (int i = 0; i < dstSpan.Length; i++) dstSpan[i] = (float)srcSpan[i];
        return dst;
    }

    static Float16[][] PoolFloat(DenseTensor<float> tf, long[] mask, int seqLen)
    {
        int n = tf.Dimensions[0], t = tf.Dimensions[1], d = tf.Dimensions[2];
        var span = tf.Buffer.Span;
        var outArr = new Float16[n][];
        for (int b = 0; b < n; b++)
        {
            int lastIdx = -1;
            for (int j = 0; j < seqLen; j++)
            {
                if (mask[b * seqLen + j] != 0) lastIdx = j;
            }
            if (lastIdx < 0) lastIdx = 0;

            var v = new float[d];
            int baseIdx = (b * t + lastIdx) * d;
            for (int k = 0; k < d; k++) v[k] = span[baseIdx + k];
            L2NormalizeInPlace(v);
            var row = new Float16[d];
            for (int k = 0; k < d; k++) row[k] = (Float16)v[k];
            outArr[b] = row;
        }
        return outArr;
    }

    public void Dispose()
    {
        _sessionLock.Wait();
        try
        {
            _sess?.Dispose();
        }
        finally
        {
            _sessionLock.Release();
            _sessionLock.Dispose();
        }
    }
}
