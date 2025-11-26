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

public partial class Embedder : IDisposable
{
    private readonly InferenceSession _sess;
    private readonly Tokenizer _tok;
    private readonly int _maxLen;
    private readonly long _padId;
    private const int MaxTokensPerBatch = 16 * 1024; // upper bound on batch_size * seq_len to avoid OOM

    const string hubName = "onnx-community/Qwen3-Embedding-0.6B-ONNX";
    const string tokFile = "tokenizer.json";

    private static readonly string settingsDir =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "maildot", "hf");

    public static async Task<Embedder> BuildEmbedder(string modelDir, int maxLen = 2 * 1024)
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

        var tok = new Tokenizer(vocabPath: await HuggingFace.GetFileFromHub(hubName, tokFile, settingsDir));
        var onnxDir = Path.Combine(settingsDir, hubName, "onnx");
        Directory.CreateDirectory(onnxDir);
        var so = new SessionOptions
        {
            OptimizedModelFilePath = Path.Combine(onnxDir, "model_fp16.optimized.ort"),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        //so.AppendExecutionProvider_CPU();
        //so.AppendExecutionProvider_CUDA();
        so.AppendExecutionProvider_DML();
        so.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MIN_OVERALL_POWER);
        await HuggingFace.GetFileFromHub(hubName, "onnx/model_fp16.onnx_data", settingsDir);
        var path = await HuggingFace.GetFileFromHub(hubName, "onnx/model_fp16.onnx", settingsDir);
        var sess = new InferenceSession(path, so);

        // TODO <|endoftext|> seems to encode to two tokens, this may be incorrect.
        return new(sess, tok, maxLen, tok.Encode("<|endoftext|>").FirstOrDefault());
    }

    private Embedder(InferenceSession sess, Tokenizer tok, int maxLen, long padId)
    {
        _sess = sess;
        _tok = tok;
        _maxLen = maxLen;
        _padId = padId;
    }

    public Float16[][] EmbedBatch(IEnumerable<string> texts)
    {
        var list = texts.ToList();
        if (list.Count == 0) return Array.Empty<Float16[]>();

        // Encode, keep original indices, and sort by length descending for dense packing.
        var encoded = list
            .Select((t, idx) => new { idx, tokens = _tok.Encode(t).Select(x => (long)x).ToArray() })
            .OrderByDescending(x => x.tokens.Length)
            .ToList();

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

                var ids = Pad(encodedIds, seqLen, _padId);
                var ms = new long[seqLen];
                var used = Math.Min(encodedIds.Length, seqLen);
                for (var j = 0; j < used; j++) ms[j] = 1;

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

            using var results = _sess.Run(inputs);

            Debug.WriteLine($"Processed batch: {batchCount}x{seqLen}");

            var outName = results.Select(r => r.Name).FirstOrDefault(n => n.Contains("sentence_embedding"))
                      ?? results[results.Count - 1].Name;

            var result = results.First(r => r.Name == outName);

            // Expect shape [N, D] across float or float16 outputs.
            Float16[][] arr;
            switch (result.Value)
            {
                case float[,] mat:
                    {
                        int n = mat.GetLength(0), d = mat.GetLength(1);
                        arr = new Float16[n][];
                        for (int i = 0; i < n; i++)
                        {
                            var v = new float[d];
                            for (int j = 0; j < d; j++) v[j] = mat[i, j];
                            L2NormalizeInPlace(v);
                            var row = new Float16[d];
                            for (int j = 0; j < d; j++) row[j] = (Float16)v[j];
                            arr[i] = row;
                        }
                        break;
                    }
                case DenseTensor<float> tf:
                    {
                        int n = tf.Dimensions[0], d = tf.Dimensions[1];
                        arr = new Float16[n][];
                        var span = tf.Buffer.Span;
                        for (int i = 0; i < n; i++)
                        {
                            var v = new float[d];
                            for (int j = 0; j < d; j++) v[j] = span[i * d + j];
                            L2NormalizeInPlace(v);
                            var row = new Float16[d];
                            for (int j = 0; j < d; j++) row[j] = (Float16)v[j];
                            arr[i] = row;
                        }
                        break;
                    }
                case DenseTensor<Float16> tf16:
                    {
                        int n = tf16.Dimensions[0], d = tf16.Dimensions[1];
                        arr = new Float16[n][];
                        var span = tf16.Buffer.Span;
                        for (int i = 0; i < n; i++)
                        {
                            var v = new float[d];
                            for (int j = 0; j < d; j++) v[j] = (float)span[i * d + j];
                            L2NormalizeInPlace(v);
                            var row = new Float16[d];
                            for (int j = 0; j < d; j++) row[j] = (Float16)v[j];
                            arr[i] = row;
                        }
                        break;
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unexpected embedding output type: {result.Value?.GetType().FullName}");
            }

            for (int i = 0; i < arr.Length; i++)
            {
                var origIdx = encoded[pos + i].idx;
                outputs[origIdx] = arr[i];
            }

            pos += batchCount;
        }

        return outputs;
    }

    static long[] Pad(long[] a, int len, long pad)
    {
        if (a.Length >= len) return [.. a.Take(len)];
        var b = new long[len]; Array.Fill(b, pad); Array.Copy(a, b, a.Length); return b;
    }
    static void L2NormalizeInPlace(float[] v)
    {
        double s = 0; foreach (var x in v) s += x * x;
        float inv = (float)(1.0 / Math.Sqrt(s + 1e-12));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose() => _sess?.Dispose();
}
