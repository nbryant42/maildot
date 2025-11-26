using maildot.Services;
using System.Diagnostics;
using maildot.Models;
using maildot.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;

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

    [Fact]
    public async Task TestClusteringWithInboxSample()
    {
        var settings = PostgresSettingsStore.Load();
        if (!settings.HasCredentials)
        {
            Debug.WriteLine("Skipping inbox clustering: PostgreSQL settings missing.");
            return;
        }

        var pwResponse = await CredentialManager.RequestPostgresPasswordAsync(settings);
        if (pwResponse.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(pwResponse.Password))
        {
            Debug.WriteLine("Skipping inbox clustering: PostgreSQL password not available.");
            return;
        }

        using var db = MailDbContextFactory.CreateDbContext(settings, pwResponse.Password);

        var messages = await db.ImapMessages
            .AsNoTracking()
            .Include(m => m.Body)
            .Include(m => m.Folder)
            .Where(m => m.Folder.FullName == "INBOX")
            .OrderByDescending(m => m.ReceivedUtc)
            //.Take(1000)
            .ToListAsync();

        if (messages.Count == 0)
        {
            Debug.WriteLine("Skipping inbox clustering: no INBOX messages found.");
            return;
        }

        var texts = messages.Select(m =>
        {
            string body = m.Body?.PlainText ?? m.Body?.HtmlText ?? m.Body?.Preview ?? string.Empty;
            body = body.Replace('\r', ' ').Replace('\n', ' ');
            var subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject;
            var combined = $"{subject} {body}".Trim();
            //const int maxChars = 4000;
            //return combined.Length > maxChars ? combined[..maxChars] : combined;
            return combined;
        }).ToList();

        if (texts.Count < 3)
        {
            Debug.WriteLine("Skipping inbox clustering: not enough messages to cluster.");
            return;
        }

        using var emb = await Embedder.BuildEmbedder("Qwen3-Embedding-0.6B-ONNX");
        var emb16 = emb.EmbedBatch(texts);
        var X = emb16.Select(row => row.Select(v => (float)v).ToArray()).ToArray();

        // forcing kMax=2 for now, but leaving the code flexible. This block could be simplified out if kMax=2.
        int kMin = 2, kMax = Math.Min(2, Math.Max(kMin, texts.Count / 3));
        int bestK = kMin; double bestSil = double.NegativeInfinity;
        for (int k = kMin; k <= kMax; k++)
        {
            var (lbl, _) = Clustering.KMeans(X, k, iters: 50);
            double s = Clustering.Silhouette(X, lbl);
            if (s > bestSil) { bestSil = s; bestK = k; }
        }

        var (labels, centroids) = Clustering.KMeans(X, bestK, iters: 100);
        Debug.WriteLine($"[INBOX] Best K: {bestK}   silhouette: {bestSil:F3}");

        // Show 3 representatives per cluster (closest to centroid)
        for (int c = 0; c < bestK; c++)
        {
            var members = Enumerable.Range(0, texts.Count).Where(i => labels[i] == c).ToList();
            if (members.Count == 0) continue;
            var cen = centroids[c];
            var reps = members
                .Select(i => (i, dist: 1.0 - Dot(X[i], cen)))
                .OrderBy(t => t.dist).Take(3).ToList();

            var recruiterScores = members
                .Select(i => (i, score: RecruiterScore(messages[i])))
                .Where(t => t.score > 0)
                .ToList();
            int recruiterCount = recruiterScores.Count;
            double recruiterScoreSum = recruiterScores.Sum(t => t.score);

            // Length stats (characters) to spot length-driven clustering
            var lengths = members.Select(i => texts[i].Length).OrderBy(x => x).ToArray();
            double avgLen = lengths.Average();
            int minLen = lengths.First();
            int maxLen = lengths.Last();
            int medianLen = lengths[lengths.Length / 2];

            Debug.WriteLine($"\n[INBOX] Cluster {c}  (n={members.Count})");
            Debug.WriteLine($"[INBOX]   Recruiter/LinkedIn-ish: {recruiterCount} (score sum: {recruiterScoreSum:F1})");
            Debug.WriteLine($"[INBOX]   Length stats (chars): min={minLen}, median={medianLen}, max={maxLen}, avg={avgLen:F1}");
            foreach (var r in reps)
            {
                var subj = string.IsNullOrWhiteSpace(messages[r.i].Subject)
                    ? "(no subject)"
                    : messages[r.i].Subject;
                Debug.WriteLine($"  - {subj}");
            }

            // Top 5 recruiter-ish emails by score
            var topRecruiters = recruiterScores
                .OrderByDescending(t => t.score)
                .Take(5)
                .ToList();
            if (topRecruiters.Count > 0)
            {
                Debug.WriteLine("[INBOX]   Top recruiter-ish examples:");
                foreach (var t in topRecruiters)
                {
                    var subj = string.IsNullOrWhiteSpace(messages[t.i].Subject)
                        ? "(no subject)"
                        : messages[t.i].Subject;
                    Debug.WriteLine($"        score={t.score:F1}  subj={subj}");
                }
            }
        }
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static double RecruiterScore(ImapMessage m)
    {
        var subject = m.Subject ?? string.Empty;
        var body = m.Body?.PlainText ?? m.Body?.HtmlText ?? m.Body?.Preview ?? string.Empty;
        var text = $"{subject} {body}".ToLowerInvariant();
        // heuristic keywords to gauge if recruiter/career-related
        string[] needles =
        [
            "recruit", "recruiter", "talent", "headhunt", "headhunter",
            "sourcing", "opportunity", "opening", "role", "position",
            "interview", "screen", "hiring", "job", "offer", "linkedin",
            "apply", "application", "candidate"
        ];
        double score = 0;
        foreach (var n in needles)
        {
            int idx = 0;
            while (true)
            {
                idx = text.IndexOf(n, idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                score += 1;
                idx += n.Length;
            }
        }
        return score;
    }
}
