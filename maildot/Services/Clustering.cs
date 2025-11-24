using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tokenizers.DotNet;

namespace maildot.Services;

class Embedder : IDisposable
{
    private readonly InferenceSession _sess;
    private readonly Tokenizer _tok;
    private readonly int _maxLen;

    const string hubName = "onnx-community/Qwen3-Embedding-0.6B-ONNX";
    const string tokFile = "tokenizer.json";

    private static readonly string settingsDir =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "maildot", "hf", hubName);

    public static async Task<Embedder> BuildEmbedder(string modelDir, int maxLen = 128)
    {
        var tok = new Tokenizer(vocabPath: await HuggingFace.GetFileFromHub(hubName, tokFile, settingsDir));
        var so = new SessionOptions();
        var sess = new InferenceSession(Path.Combine(modelDir, "model.onnx"), so);

        return new(sess, tok, maxLen);
    }

    private Embedder(InferenceSession sess, Tokenizer tok, int maxLen)
    {
        _sess = sess;
        _tok = tok;
        _maxLen = maxLen;
    }

    public float[][] EmbedBatch(IEnumerable<string> texts)
    {
        var list = texts.ToList();
        var idsBatch = new long[list.Count * _maxLen];
        var maskBatch = new long[list.Count * _maxLen];

        for (int i = 0; i < list.Count; i++)
        {
            var enc = _tok.Encode(list[i]);
            var encodedIds = enc.Select(x => (long)x).Take(_maxLen).ToArray();

            var ids = Pad(encodedIds, _maxLen, 0);
            var ms = new long[_maxLen];
            var used = Math.Min(encodedIds.Length, _maxLen);
            for (var j = 0; j < used; j++) ms[j] = 1;

            Array.Copy(ids, 0, idsBatch, i * _maxLen, _maxLen);
            Array.Copy(ms, 0, maskBatch, i * _maxLen, _maxLen);
        }

        //var inputs = new List<NamedOnnxValue> {
        //    NamedOnnxValue.CreateFromTensor("input_ids",     new DenseTensor<long>(idsBatch,  new[] { list.Count, _maxLen })),
        //    NamedOnnxValue.CreateFromTensor("attention_mask",new DenseTensor<long>(maskBatch, new[] { list.Count, _maxLen })),
        //};

        //using var results = _sess.Run(inputs);
        //var outName = results.Select(r => r.Name).FirstOrDefault(n => n.Contains("sentence_embedding"))
        //          ?? results.Last().Name;

        //// Expect shape [N, D]
        //var mat = (float[,])results.First(r => r.Name == outName).Value;
        //int n = mat.GetLength(0), d = mat.GetLength(1);
        //var arr = new float[n][];
        //for (int i = 0; i < n; i++)
        //{
        //    var v = new float[d];
        //    for (int j = 0; j < d; j++) v[j] = mat[i, j];
        //    L2NormalizeInPlace(v);
        //    arr[i] = v;
        //}
        //return arr;

        return [];
    }

    static long[] Pad(long[] a, int len, long pad)
    {
        if (a.Length >= len) return a.Take(len).ToArray();
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

static class Clustering
{
    // Cosine distance on already L2-normalized vectors: 1 - dot(a,b)
    static float CosDist(float[] a, float[] b)
    {
        double dot = 0; for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return (float)(1.0 - dot);
    }

    public static (int[] labels, float[][] centroids) KMeans(float[][] x, int k, int iters = 100, int seed = 42)
    {
        int n = x.Length, d = x[0].Length;
        var rnd = new Random(seed);

        // k-means++ (simplified: pick first random, then farthest each time)
        var centroids = new List<float[]>();
        centroids.Add(x[rnd.Next(n)].ToArray());
        while (centroids.Count < k)
        {
            int bestIdx = -1; double bestDist = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double minD = double.PositiveInfinity;
                foreach (var c in centroids) minD = Math.Min(minD, CosDist(x[i], c));
                if (minD > bestDist) { bestDist = minD; bestIdx = i; }
            }
            centroids.Add(x[bestIdx].ToArray());
        }

        var labels = new int[n];
        for (int iter = 0; iter < iters; iter++)
        {
            bool moved = false;
            // assign
            for (int i = 0; i < n; i++)
            {
                int best = 0; float bd = CosDist(x[i], centroids[0]);
                for (int c = 1; c < k; c++)
                {
                    float d0 = CosDist(x[i], centroids[c]);
                    if (d0 < bd) { bd = d0; best = c; }
                }
                if (labels[i] != best) { labels[i] = best; moved = true; }
            }
            // update
            var sums = new float[k][]; var counts = new int[k];
            for (int c = 0; c < k; c++) sums[c] = new float[d];
            for (int i = 0; i < n; i++)
            {
                int c = labels[i]; counts[c]++;
                var xi = x[i]; var sc = sums[c];
                for (int j = 0; j < d; j++) sc[j] += xi[j];
            }
            for (int c = 0; c < k; c++)
            {
                if (counts[c] == 0) continue;
                var cen = sums[c];
                for (int j = 0; j < d; j++) cen[j] /= counts[c];
                // re-normalize centroid for cosine distance stability
                double s = 0; for (int j = 0; j < d; j++) s += cen[j] * cen[j];
                float inv = (float)(1.0 / Math.Sqrt(s + 1e-12));
                for (int j = 0; j < d; j++) cen[j] *= inv;
                centroids[c] = cen;
            }
            if (!moved) break;
        }
        return (labels, centroids.Select(c => c.ToArray()).ToArray());
    }

    public static double Silhouette(float[][] x, int[] labels)
    {
        int n = x.Length;
        // group indices by label
        var groups = labels.Distinct().ToDictionary(c => c, c => new List<int>());
        for (int i = 0; i < n; i++) groups[labels[i]].Add(i);

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            int ci = labels[i];
            // a(i): mean intra-cluster distance
            double a = 0; int ca = 0;
            foreach (var j in groups[ci]) if (j != i) { a += CosDist(x[i], x[j]); ca++; }
            a = ca > 0 ? a / ca : 0;

            // b(i): min mean distance to other clusters
            double b = double.PositiveInfinity;
            foreach (var kv in groups)
            {
                if (kv.Key == ci || kv.Value.Count == 0) continue;
                double sumd = 0; foreach (var j in kv.Value) sumd += CosDist(x[i], x[j]);
                double mean = sumd / kv.Value.Count;
                if (mean < b) b = mean;
            }
            double s = (b - a) / Math.Max(a, b + 1e-12);
            sum += s;
        }
        return sum / n;
    }
}

//class Program
//{
//    static void Main()
//    {
//        // 1) Your emails (subject + short body). Replace with your data loader.
//        var emails = new[]
//        {
//            "Invoice for October attached...",
//            "Team offsite agenda and travel details...",
//            "Re: GPU driver update causes crashes",
//            "Reminder: dentist appointment next week",
//            "Build pipeline outage postmortem draft",
//            "Holiday plans with family – flights and hotel",
//            "Security alert: password reset required",
//            "RE: Meeting notes + action items",
//            "Bug report: out-of-memory on RTX 4070",
//            "Receipt: order #123456 shipped",
//            "Fwd: Photos from the weekend trip",
//            "Re: Performance review scheduling"
//        };

//        // 2) Embed (Qwen3-Embedding-0.6B ONNX folder)
//        using var emb = Embedder.BuildEmbedder("Qwen3-Embedding-0.6B-ONNX", maxLen: 128).Result;
//        var X = emb.EmbedBatch(emails); // float[N][D], L2-normalized

//        // 3) Choose K by silhouette
//        int bestK = 0; double bestSil = double.NegativeInfinity;
//        int kMin = 3, kMax = Math.Min(10, emails.Length - 1);
//        for (int k = kMin; k <= kMax; k++)
//        {
//            var (lbl, _) = Clustering.KMeans(X, k, iters: 50);
//            double s = Clustering.Silhouette(X, lbl);
//            if (s > bestSil) { bestSil = s; bestK = k; }
//        }
//        var (labels, centroids) = Clustering.KMeans(X, bestK, iters: 100);
//        Console.WriteLine($"Best K: {bestK}   silhouette: {bestSil:F3}");

//        // 4) Show 3 representatives per cluster (closest to centroid)
//        for (int c = 0; c < bestK; c++)
//        {
//            var members = Enumerable.Range(0, emails.Length).Where(i => labels[i] == c).ToList();
//            if (members.Count == 0) continue;
//            var cen = centroids[c];
//            var reps = members
//                .Select(i => (i, dist: 1.0 - Dot(X[i], cen)))
//                .OrderBy(t => t.dist).Take(3).ToList();

//            Console.WriteLine($"\nCluster {c}  (n={members.Count})");
//            foreach (var r in reps) Console.WriteLine($"  - {emails[r.i]}");
//        }
//    }

//    static double Dot(float[] a, float[] b)
//    {
//        double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
//        return s;
//    }
//}
