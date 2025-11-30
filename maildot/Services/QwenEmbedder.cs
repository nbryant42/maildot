using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private const int MaxTokensPerBatch = 16 * 1024; // upper bound on batch_size * seq_len to avoid OOM
    private const string DefaultQueryInstruction = "Given a mailbox and a search query, find emails whose subject or " +
        "body are most relevant to the topic of the query, even if they don't explicitly answer a question.";

    public const string ModelId = "onnx-community/Qwen3-Embedding-0.6B-ONNX";
    const string tokFile = "tokenizer.json";

    private static readonly string hfCacheDir =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "maildot", "hf");

    public static async Task<QwenEmbedder> Build(string modelDir, int maxLen = 1024)
    {
        //// Create a new instance of EnvironmentCreationOptions
        //EnvironmentCreationOptions envOptions = new()
        //{
        //    logId = "maildot",
        //    logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        //};

        //// Pass the options by reference to CreateInstanceWithOptions
        //OrtEnv ortEnv = OrtEnv.CreateInstanceWithOptions(ref envOptions);

        // Use Windows ML to download and register Execution Providers
        //var catalog = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
        //Console.WriteLine("Ensuring and registering execution providers...");
        //await catalog.EnsureAndRegisterCertifiedAsync();

        var tok = new Tokenizer(vocabPath: await HuggingFace.GetFileFromHub(ModelId, tokFile, hfCacheDir));
        var onnxDir = Path.Combine(hfCacheDir, ModelId, "onnx");
        Directory.CreateDirectory(onnxDir);
        var so = new SessionOptions
        {
            OptimizedModelFilePath = Path.Combine(onnxDir, "model_fp16.optimized.ort"),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        //TODO: the way this is supposed to work is that ONNX Runtime picks the best available EP for each node type.
        //but in practice, it seems to pick CPU over DML even when DML is clearly better.
        //so.AppendExecutionProvider_CPU();
        //so.AppendExecutionProvider_CUDA();
        so.AppendExecutionProvider_DML();
        so.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MIN_OVERALL_POWER);
        await HuggingFace.GetFileFromHub(ModelId, "onnx/model_fp16.onnx_data", hfCacheDir);
        var path = await HuggingFace.GetFileFromHub(ModelId, "onnx/model_fp16.onnx", hfCacheDir);
        var sess = new InferenceSession(path, so);

        // TODO <|endoftext|> seems to encode to two tokens, this may be incorrect.
        return new(sess, tok, maxLen, tok.Encode("<|endoftext|>").FirstOrDefault());
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

            int maxBatch = (int)Math.Max(1, MaxTokensPerBatch / (long)seqLen);
            int batchCount = Math.Min(maxBatch, remaining);

            // Recompute seqLen for this slice
            seqLen = Math.Min(_maxLen, Math.Max(1, encoded.Skip(pos).Take(batchCount).Max(e => e.tokens.Length)));
            batchCount = (int)Math.Max(1, Math.Min(batchCount, MaxTokensPerBatch / (long)seqLen));

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
                NamedOnnxValue.CreateFromTensor("input_ids",     new DenseTensor<long>(idsBatch,  [batchCount, seqLen])),
                NamedOnnxValue.CreateFromTensor("attention_mask",new DenseTensor<long>(maskBatch, [batchCount, seqLen])),
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

    public void Dispose() => _sess?.Dispose();
}
