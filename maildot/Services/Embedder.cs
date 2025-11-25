using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
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

    const string hubName = "onnx-community/Qwen3-Embedding-0.6B-ONNX";
    const string tokFile = "tokenizer.json";

    private static readonly string settingsDir =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "maildot", "hf");

    public static async Task<Embedder> BuildEmbedder(string modelDir, int maxLen = 8192)
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
        so.AppendExecutionProvider_DML();
        //so.AppendExecutionProvider_CUDA();
        //so.AppendExecutionProvider_CPU();
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
        // Two-pass: keep per-batch sequence length as small as possible to avoid huge buffers.
        var encoded = list.Select(t => _tok.Encode(t).Select(x => (long)x).ToArray()).ToList();
        int seqLen = Math.Min(_maxLen, encoded.Max(e => e.Length));
        if (seqLen <= 0) seqLen = Math.Min(_maxLen, 1);

        var idsBatch = new long[list.Count * seqLen];
        var maskBatch = new long[list.Count * seqLen];

        for (int i = 0; i < list.Count; i++)
        {
            var encodedIds = encoded[i].Take(seqLen).ToArray();

            var ids = Pad(encodedIds, seqLen, _padId);
            var ms = new long[seqLen];
            var used = Math.Min(encodedIds.Length, seqLen);
            for (var j = 0; j < used; j++) ms[j] = 1;

            Array.Copy(ids, 0, idsBatch, i * seqLen, seqLen);
            Array.Copy(ms, 0, maskBatch, i * seqLen, seqLen);
        }

        var inputs = new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("input_ids",     new DenseTensor<long>(idsBatch,  [list.Count, seqLen])),
            NamedOnnxValue.CreateFromTensor("attention_mask",new DenseTensor<long>(maskBatch, [list.Count, seqLen])),
        };

        if (_sess.InputMetadata.TryGetValue("position_ids", out var _))
        {
            // position_ids shape [batch, seq_len], dtype int64
            var pos = new long[list.Count * seqLen];
            for (int b = 0; b < list.Count; b++)
            {
                for (int j = 0; j < seqLen; j++) pos[b * seqLen + j] = j;
            }
            inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids",
                new DenseTensor<long>(pos, [list.Count, seqLen])));
        }

        // Add dummy past_key_values.* if present
        foreach (var kv in _sess.InputMetadata.Where(k => k.Key.Contains("past_key_values")))
        {
            var md = kv.Value;
            // md.Dimensions is typically [batch, num_kv_heads, past_seq_len, head_dim] with -1 for dynamic dims.
            int[] dims = md.Dimensions;
            int b = list.Count;
            int numKvHeads = dims.Length > 1 && dims[1] > 0 ? dims[1] : /* model default */ 8;
            int headDim = dims.Length > 3 && dims[3] > 0 ? dims[3] : /* model default */ 128;
            var shape = new[] { b, numKvHeads, 0, headDim };

            switch (md.ElementDataType)
            {
                case TensorElementType.Float16:
                    inputs.Add(NamedOnnxValue.CreateFromTensor(kv.Key,
                        new DenseTensor<Float16>(new Float16[0], shape)));
                    break;
                default:
                    inputs.Add(NamedOnnxValue.CreateFromTensor(kv.Key,
                        new DenseTensor<float>(new float[0], shape)));
                    break;
            }
        }

        using var results = _sess.Run(inputs);
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
                    return arr;
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
                    return arr;
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
                    return arr;
                }
            default:
                throw new InvalidOperationException(
                    $"Unexpected embedding output type: {result.Value?.GetType().FullName}");
        }
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
