using maildot.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace maildot.Tests;

public class ClusteringTests
{
    [Fact]
    public async Task TestClustering()
    {
        // 1) "emails" (subject + short body)
        string[] emails =
        [
            "Invoice for October attached...",
            "Team offsite agenda and travel details...",
            "Re: GPU driver update causes crashes",
            "Reminder: dentist appointment next week",
            "Build pipeline outage postmortem draft",
            "Holiday plans with family – flights and hotel",
            "Security alert: password reset required",
            "RE: Meeting notes + action items",
            "Bug report: out-of-memory on RTX 4070",
            "Receipt: order #123456 shipped",
            "Fwd: Photos from the weekend trip",
            "Re: Performance review scheduling"
        ];

        // 2) Embed
        using var emb = await Embedder.BuildEmbedder("Qwen3-Embedding-0.6B-ONNX");
        var X16 = emb.EmbedBatch(emails); // Float16[N][D], L2-normalized
        var X = X16.Select(row => row.Select(v => (float)v).ToArray()).ToArray();

        // 3) Choose K by silhouette
        int bestK = 0; double bestSil = double.NegativeInfinity;
        int kMin = 3, kMax = Math.Min(10, Math.Max(3, emails.Length / 3));
        for (int k = kMin; k <= kMax; k++)
        {
            var (lbl, _) = Clustering.KMeans(X, k, iters: 50);
            double s = Clustering.Silhouette(X, lbl);
            if (s > bestSil) { bestSil = s; bestK = k; }
        }
        var (labels, centroids) = Clustering.KMeans(X, bestK, iters: 100);
        Debug.WriteLine($"Best K: {bestK}   silhouette: {bestSil:F3}");

        // 4) Show 3 representatives per cluster (closest to centroid)
        for (int c = 0; c < bestK; c++)
        {
            var members = Enumerable.Range(0, emails.Length).Where(i => labels[i] == c).ToList();
            if (members.Count == 0) continue;
            var cen = centroids[c];
            var reps = members
                .Select(i => (i, dist: 1.0 - Dot(X[i], cen)))
                .OrderBy(t => t.dist).Take(3).ToList();

            Debug.WriteLine($"\nCluster {c}  (n={members.Count})");
            foreach (var r in reps) Debug.WriteLine($"  - {emails[r.i]}");
        }
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
