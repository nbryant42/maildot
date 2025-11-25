using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tokenizers.DotNet;

namespace maildot.Services;

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
        var centroids = new List<float[]>
        {
            ([.. x[rnd.Next(n)]]) // clone
        };
        while (centroids.Count < k)
        {
            int bestIdx = -1; double bestDist = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double minD = double.PositiveInfinity;
                foreach (var c in centroids) minD = Math.Min(minD, CosDist(x[i], c));
                if (minD > bestDist) { bestDist = minD; bestIdx = i; }
            }
            centroids.Add([.. x[bestIdx]]); // clone
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