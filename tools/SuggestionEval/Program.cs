using maildot.Data;
using maildot.Models;
using maildot.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SuggestionEval;

internal sealed record EvalOptions(
    int? AccountId,
    int Days,
    int Limit,
    double Alpha,
    double Lambda,
    int TopK,
    bool IncludeMultiLabel,
    double[] SweepLambdas);

internal sealed record CandidateMessage(int MessageId, int FolderId, int[] TrueLabels, List<float[]> Embeddings);
internal sealed record Prediction(int MessageId, int[] TrueLabels, int PredictedLabelId, double PredictedScore, int[] TopKLabels);
internal sealed record EvalMetrics(int Total, double Top1, double TopK, double MacroF1);

internal static class Program
{
    private const string LabelIdsCte = @"
WITH label_ids AS (
    SELECT ml.""LabelId"", ml.""MessageId""
    FROM ""message_labels"" ml
    JOIN ""imap_messages"" m ON m.""Id"" = ml.""MessageId""
    JOIN ""imap_folders"" f ON f.""id"" = m.""FolderId""
    WHERE f.""AccountId"" = @accountId
    UNION
    SELECT sl.""LabelId"", m.""Id""
    FROM ""sender_labels"" sl
    JOIN ""labels"" l ON l.""Id"" = sl.""LabelId""
    JOIN ""imap_messages"" m ON sl.""from_address"" = lower(m.""FromAddress"")
    JOIN ""imap_folders"" f ON f.""id"" = m.""FolderId""
    WHERE l.""AccountId"" = @accountId
      AND f.""AccountId"" = @accountId
)";

    public static async Task Main(string[] args)
    {
        var options = ParseOptions(args);
        var settings = PostgresSettingsStore.Load();
        var pwResponse = await CredentialManager.RequestPostgresPasswordAsync(settings);
        if (pwResponse.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(pwResponse.Password))
        {
            Console.WriteLine("PostgreSQL password not found. Please set it in the vault first.");
            return;
        }

        await using var db = MailDbContextFactory.CreateDbContext(settings, pwResponse.Password);
        var accountId = await ResolveAccountIdAsync(db, options.AccountId);
        if (accountId == null)
        {
            return;
        }

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        var labels = await LoadLabelsAsync(conn, accountId.Value);
        if (labels.Count == 0)
        {
            Console.WriteLine("No labels found for this account.");
            return;
        }

        var candidates = await LoadCandidatesAsync(conn, accountId.Value, options);
        if (candidates.Count == 0)
        {
            Console.WriteLine("No candidate messages found for evaluation.");
            return;
        }

        var centroids = await LoadCentroidsAsync(conn, accountId.Value);
        if (centroids.Count == 0)
        {
            Console.WriteLine("No centroids found (need labeled messages with embeddings).");
            return;
        }

        var priorStats = await LoadPriorStatsAsync(conn, accountId.Value);
        if (options.SweepLambdas.Length > 0)
        {
            Console.WriteLine("Suggestion Eval Lambda Sweep");
            Console.WriteLine("============================");
            Console.WriteLine($"Messages evaluated: {candidates.Count}");
            Console.WriteLine($"Params: days={options.Days}, limit={options.Limit}, alpha={options.Alpha}, topk={options.TopK}, includeMultilabel={options.IncludeMultiLabel}");
            Console.WriteLine();
            Console.WriteLine($"{"lambda",8} {"top1",8} {"top"+options.TopK,8} {"macroF1",10}");

            foreach (var lambda in options.SweepLambdas.Distinct().OrderBy(x => x))
            {
                var predictions = ScoreCandidates(candidates, centroids, priorStats, options.Alpha, lambda, options.TopK);
                if (predictions.Count == 0)
                {
                    continue;
                }

                var metrics = ComputeMetrics(options.TopK, labels.Keys, predictions);
                Console.WriteLine($"{lambda,8:F3} {metrics.Top1,8:P2} {metrics.TopK,8:P2} {metrics.MacroF1,10:P2}");
            }

            return;
        }

        var runPredictions = ScoreCandidates(candidates, centroids, priorStats, options.Alpha, options.Lambda, options.TopK);
        if (runPredictions.Count == 0)
        {
            Console.WriteLine("No predictions produced.");
            return;
        }

        Report(options, labels, runPredictions);
    }

    private static EvalOptions ParseOptions(string[] args)
    {
        int? accountId = null;
        var days = 30;
        var limit = 2000;
        var alpha = 24.0d;
        var lambda = 0.35d;
        var topK = 3;
        var includeMultiLabel = false;
        var sweepLambdas = new List<double>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--include-multilabel", StringComparison.OrdinalIgnoreCase))
            {
                includeMultiLabel = true;
                continue;
            }
            if (arg.Equals("--sweep-lambda", StringComparison.OrdinalIgnoreCase))
            {
                var raw = i + 1 < args.Length ? args[i + 1] : null;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (double.TryParse(piece, out var parsed))
                        {
                            sweepLambdas.Add(parsed);
                        }
                    }
                }

                i++;
                continue;
            }

            var next = i + 1 < args.Length ? args[i + 1] : null;
            if (arg.Equals("--account-id", StringComparison.OrdinalIgnoreCase) && int.TryParse(next, out var aid))
            {
                accountId = aid;
                i++;
            }
            else if (arg.Equals("--days", StringComparison.OrdinalIgnoreCase) && int.TryParse(next, out var d))
            {
                days = Math.Max(1, d);
                i++;
            }
            else if (arg.Equals("--limit", StringComparison.OrdinalIgnoreCase) && int.TryParse(next, out var l))
            {
                limit = Math.Max(50, l);
                i++;
            }
            else if (arg.Equals("--alpha", StringComparison.OrdinalIgnoreCase) && double.TryParse(next, out var a))
            {
                alpha = Math.Max(0.0d, a);
                i++;
            }
            else if (arg.Equals("--lambda", StringComparison.OrdinalIgnoreCase) && double.TryParse(next, out var lm))
            {
                lambda = lm;
                i++;
            }
            else if (arg.Equals("--topk", StringComparison.OrdinalIgnoreCase) && int.TryParse(next, out var k))
            {
                topK = Math.Max(1, k);
                i++;
            }
        }

        return new EvalOptions(accountId, days, limit, alpha, lambda, topK, includeMultiLabel, [.. sweepLambdas]);
    }

    private static async Task<int?> ResolveAccountIdAsync(MailDbContext db, int? requestedId)
    {
        var accounts = await db.ImapAccounts
            .AsNoTracking()
            .Select(a => new { a.Id, a.DisplayName, a.Username })
            .OrderBy(a => a.Id)
            .ToListAsync();

        if (accounts.Count == 0)
        {
            Console.WriteLine("No IMAP accounts found.");
            return null;
        }

        if (requestedId != null)
        {
            if (accounts.Any(a => a.Id == requestedId.Value))
            {
                return requestedId.Value;
            }

            Console.WriteLine($"Account id {requestedId.Value} not found.");
            Console.WriteLine("Available account ids:");
            foreach (var account in accounts)
            {
                Console.WriteLine($"  {account.Id}: {account.DisplayName} ({account.Username})");
            }
            return null;
        }

        if (accounts.Count == 1)
        {
            return accounts[0].Id;
        }

        Console.WriteLine("Multiple accounts found; pass --account-id.");
        foreach (var account in accounts)
        {
            Console.WriteLine($"  {account.Id}: {account.DisplayName} ({account.Username})");
        }
        return null;
    }

    private static async Task<Dictionary<int, string>> LoadLabelsAsync(NpgsqlConnection conn, int accountId)
    {
        var map = new Dictionary<int, string>();
        const string sql = @"SELECT ""Id"", ""Name"" FROM ""labels"" WHERE ""AccountId"" = @accountId";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            map[reader.GetInt32(0)] = reader.GetString(1);
        }

        return map;
    }

    private static async Task<List<CandidateMessage>> LoadCandidatesAsync(NpgsqlConnection conn, int accountId, EvalOptions options)
    {
        var sinceUtc = DateTimeOffset.UtcNow.AddDays(-options.Days);
        var sql = $@"
{LabelIdsCte},
candidate_messages AS (
    SELECT m.""Id"", m.""FolderId"", m.""ReceivedUtc""
    FROM ""imap_messages"" m
    JOIN ""imap_folders"" f ON f.""id"" = m.""FolderId""
    WHERE f.""AccountId"" = @accountId
      AND m.""ReceivedUtc"" >= @sinceUtc
      AND EXISTS (SELECT 1 FROM label_ids li WHERE li.""MessageId"" = m.""Id"")
    ORDER BY m.""ReceivedUtc"" DESC
    LIMIT @limit
),
truth AS (
    SELECT c.""Id"", c.""FolderId"", array_agg(DISTINCT li.""LabelId"") AS labels
    FROM candidate_messages c
    JOIN label_ids li ON li.""MessageId"" = c.""Id""
    GROUP BY c.""Id"", c.""FolderId""
)
SELECT t.""Id"", t.""FolderId"", t.labels
FROM truth t";

        var candidates = new List<CandidateMessage>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("accountId", accountId);
            cmd.Parameters.AddWithValue("sinceUtc", sinceUtc);
            cmd.Parameters.AddWithValue("limit", options.Limit);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var labels = reader.GetFieldValue<int[]>(2);
                if (!options.IncludeMultiLabel && labels.Length != 1)
                {
                    continue;
                }

                candidates.Add(new CandidateMessage(
                    MessageId: reader.GetInt32(0),
                    FolderId: reader.GetInt32(1),
                    TrueLabels: labels,
                    Embeddings: []));
            }
        }

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var byId = candidates.ToDictionary(c => c.MessageId);
        var messageIds = byId.Keys.ToArray();
        const string embeddingsSql = @"SELECT ""MessageId"", ""Vector""::real[] AS vec FROM ""message_embeddings"" WHERE ""MessageId"" = ANY(@messageIds)";
        await using var emCmd = new NpgsqlCommand(embeddingsSql, conn);
        emCmd.Parameters.AddWithValue("messageIds", messageIds);
        await using var emReader = await emCmd.ExecuteReaderAsync();
        while (await emReader.ReadAsync())
        {
            var messageId = emReader.GetInt32(0);
            if (byId.TryGetValue(messageId, out var candidate))
            {
                candidate.Embeddings.Add(emReader.GetFieldValue<float[]>(1));
            }
        }

        return candidates.Where(c => c.Embeddings.Count > 0).ToList();
    }

    private static async Task<Dictionary<int, float[]>> LoadCentroidsAsync(NpgsqlConnection conn, int accountId)
    {
        var sql = $@"
{LabelIdsCte}
SELECT li.""LabelId"", avg(e.""Vector"")::real[] AS centroid
FROM ""message_embeddings"" e
JOIN label_ids li ON li.""MessageId"" = e.""MessageId""
GROUP BY li.""LabelId""";

        var map = new Dictionary<int, float[]>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(1))
            {
                continue;
            }

            var centroid = Normalize(reader.GetFieldValue<float[]>(1));
            map[reader.GetInt32(0)] = centroid;
        }

        return map;
    }

    private sealed class PriorStats
    {
        public double GlobalTotal { get; set; }
        public Dictionary<int, double> LabelGlobalCounts { get; } = [];
        public Dictionary<int, double> FolderTotals { get; } = [];
        public Dictionary<(int LabelId, int FolderId), double> FolderLabelCounts { get; } = [];
    }

    private static async Task<PriorStats> LoadPriorStatsAsync(NpgsqlConnection conn, int accountId)
    {
        var stats = new PriorStats();
        var sql = $@"
{LabelIdsCte},
global_counts AS (
    SELECT li.""LabelId"", count(*)::double precision AS c
    FROM label_ids li
    GROUP BY li.""LabelId""
),
global_total AS (
    SELECT count(*)::double precision AS c
    FROM label_ids li
),
folder_counts AS (
    SELECT m.""FolderId"", li.""LabelId"", count(*)::double precision AS c
    FROM label_ids li
    JOIN ""imap_messages"" m ON m.""Id"" = li.""MessageId""
    GROUP BY m.""FolderId"", li.""LabelId""
),
folder_totals AS (
    SELECT m.""FolderId"", count(*)::double precision AS c
    FROM label_ids li
    JOIN ""imap_messages"" m ON m.""Id"" = li.""MessageId""
    GROUP BY m.""FolderId""
)
SELECT 'global_total'::text AS kind, 0::integer AS label_id, 0::integer AS folder_id, gt.c AS cnt
FROM global_total gt
UNION ALL
SELECT 'global_label', gc.""LabelId"", 0, gc.c FROM global_counts gc
UNION ALL
SELECT 'folder_total', 0, ft.""FolderId"", ft.c FROM folder_totals ft
UNION ALL
SELECT 'folder_label', fc.""LabelId"", fc.""FolderId"", fc.c FROM folder_counts fc";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var kind = reader.GetString(0);
            var labelId = reader.GetInt32(1);
            var folderId = reader.GetInt32(2);
            var count = reader.GetDouble(3);

            switch (kind)
            {
                case "global_total":
                    stats.GlobalTotal = count;
                    break;
                case "global_label":
                    stats.LabelGlobalCounts[labelId] = count;
                    break;
                case "folder_total":
                    stats.FolderTotals[folderId] = count;
                    break;
                case "folder_label":
                    stats.FolderLabelCounts[(labelId, folderId)] = count;
                    break;
            }
        }

        return stats;
    }

    private static List<Prediction> ScoreCandidates(
        List<CandidateMessage> candidates,
        Dictionary<int, float[]> centroids,
        PriorStats stats,
        double alpha,
        double lambda,
        int topK)
    {
        var predictions = new List<Prediction>(candidates.Count);
        var labelIds = centroids.Keys.OrderBy(id => id).ToArray();

        foreach (var msg in candidates)
        {
            var scored = new List<(int LabelId, double Score)>(labelIds.Length);

            foreach (var labelId in labelIds)
            {
                var centroid = centroids[labelId];
                var semantic = double.NegativeInfinity;
                foreach (var embedding in msg.Embeddings)
                {
                    var d = Dot(embedding, centroid);
                    if (d > semantic)
                    {
                        semantic = d;
                    }
                }

                var priorLift = ComputePriorLift(stats, labelId, msg.FolderId, alpha);
                var combined = semantic + (lambda * priorLift);
                scored.Add((labelId, combined));
            }

            var ordered = scored
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.LabelId)
                .ToList();

            var topKLabels = ordered.Take(topK).Select(x => x.LabelId).ToArray();
            var top1 = ordered[0];
            predictions.Add(new Prediction(msg.MessageId, msg.TrueLabels, top1.LabelId, top1.Score, topKLabels));
        }

        return predictions;
    }

    private static double ComputePriorLift(PriorStats stats, int labelId, int folderId, double alpha)
    {
        if (stats.GlobalTotal <= 0)
        {
            return 0.0d;
        }

        var globalLabel = stats.LabelGlobalCounts.TryGetValue(labelId, out var gl) ? gl : 0.0d;
        var globalP = ClampProb(globalLabel / stats.GlobalTotal);
        var folderTotal = stats.FolderTotals.TryGetValue(folderId, out var ft) ? ft : 0.0d;
        if (folderTotal <= 0)
        {
            return 0.0d;
        }

        var folderLabel = stats.FolderLabelCounts.TryGetValue((labelId, folderId), out var fl) ? fl : 0.0d;
        var smoothedFolderP = ClampProb((folderLabel + (alpha * globalP)) / (folderTotal + alpha));
        return Logit(smoothedFolderP) - Logit(globalP);
    }

    private static double ClampProb(double p) => Math.Min(1.0d - 1e-6d, Math.Max(1e-6d, p));
    private static double Logit(double p) => Math.Log(p / (1.0d - p));

    private static float[] Normalize(float[] input)
    {
        var v = (float[])input.Clone();
        double sumSq = 0.0d;
        for (var i = 0; i < v.Length; i++)
        {
            sumSq += v[i] * v[i];
        }

        if (sumSq <= 1e-20d)
        {
            return v;
        }

        var inv = (float)(1.0d / Math.Sqrt(sumSq));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] *= inv;
        }

        return v;
    }

    private static double Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double sum = 0.0d;
        for (var i = 0; i < n; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static void Report(EvalOptions options, Dictionary<int, string> labels, List<Prediction> predictions)
    {
        var metrics = ComputeMetrics(options.TopK, labels.Keys, predictions);

        Console.WriteLine("Suggestion Eval");
        Console.WriteLine("===============");
        Console.WriteLine($"Messages evaluated: {metrics.Total}");
        Console.WriteLine($"Top-1 accuracy:     {metrics.Top1:P2}");
        Console.WriteLine($"Top-{options.TopK} accuracy:     {metrics.TopK:P2}");
        Console.WriteLine($"Macro F1:           {metrics.MacroF1:P2}");
        Console.WriteLine();
        Console.WriteLine($"Params: days={options.Days}, limit={options.Limit}, alpha={options.Alpha}, lambda={options.Lambda}, includeMultilabel={options.IncludeMultiLabel}");
        Console.WriteLine();

        var perLabel = predictions
            .GroupBy(p => p.PredictedLabelId)
            .Select(g =>
            {
                var predicted = g.Count();
                var correct = g.Count(p => p.TrueLabels.Contains(p.PredictedLabelId));
                return new
                {
                    LabelId = g.Key,
                    Predicted = predicted,
                    Precision = predicted == 0 ? 0.0d : (double)correct / predicted
                };
            })
            .OrderByDescending(x => x.Predicted)
            .Take(15)
            .ToList();

        Console.WriteLine("Top predicted labels (precision over predicted set):");
        foreach (var row in perLabel)
        {
            var name = labels.TryGetValue(row.LabelId, out var labelName) ? labelName : $"#{row.LabelId}";
            Console.WriteLine($"  {name,-24} predicted={row.Predicted,5} precision={row.Precision:P2}");
        }

        Console.WriteLine();
        Console.WriteLine("Per-label metrics (support >= 10):");
        var perLabelMetrics = BuildPerLabelMetrics(labels.Keys, predictions)
            .Where(m => m.Support >= 10)
            .OrderByDescending(m => m.Support)
            .Take(20)
            .ToList();
        foreach (var row in perLabelMetrics)
        {
            var name = labels.TryGetValue(row.LabelId, out var labelName) ? labelName : $"#{row.LabelId}";
            Console.WriteLine($"  {name,-24} support={row.Support,5} precision={row.Precision,8:P2} recall={row.Recall,8:P2} f1={row.F1,8:P2}");
        }

        Console.WriteLine();
        Console.WriteLine("Top confusion pairs:");
        var confusions = BuildConfusions(predictions)
            .OrderByDescending(c => c.Count)
            .Take(20)
            .ToList();
        foreach (var c in confusions)
        {
            var trueName = labels.TryGetValue(c.TrueLabelId, out var tname) ? tname : $"#{c.TrueLabelId}";
            var predName = labels.TryGetValue(c.PredLabelId, out var pname) ? pname : $"#{c.PredLabelId}";
            Console.WriteLine($"  {trueName,-24} -> {predName,-24} count={c.Count,5}");
        }
    }

    private sealed record LabelMetric(int LabelId, int Support, double Precision, double Recall, double F1);
    private sealed record Confusion(int TrueLabelId, int PredLabelId, int Count);

    private static EvalMetrics ComputeMetrics(int topK, IEnumerable<int> allLabelIds, List<Prediction> predictions)
    {
        var total = predictions.Count;
        var top1 = total == 0 ? 0.0d : (double)predictions.Count(p => p.TrueLabels.Contains(p.PredictedLabelId)) / total;
        var topKAcc = total == 0
            ? 0.0d
            : (double)predictions.Count(p => p.TopKLabels.Any(k => p.TrueLabels.Contains(k))) / total;
        var perLabel = BuildPerLabelMetrics(allLabelIds, predictions).Where(m => m.Support > 0).ToList();
        var macroF1 = perLabel.Count == 0 ? 0.0d : perLabel.Average(m => m.F1);
        return new EvalMetrics(total, top1, topKAcc, macroF1);
    }

    private static List<LabelMetric> BuildPerLabelMetrics(IEnumerable<int> allLabelIds, List<Prediction> predictions)
    {
        var predictedCounts = new Dictionary<int, int>();
        var supportCounts = new Dictionary<int, int>();
        var truePositiveCounts = new Dictionary<int, int>();

        foreach (var p in predictions)
        {
            predictedCounts[p.PredictedLabelId] = predictedCounts.TryGetValue(p.PredictedLabelId, out var pc) ? pc + 1 : 1;

            foreach (var trueLabel in p.TrueLabels.Distinct())
            {
                supportCounts[trueLabel] = supportCounts.TryGetValue(trueLabel, out var sc) ? sc + 1 : 1;
                if (p.PredictedLabelId == trueLabel)
                {
                    truePositiveCounts[trueLabel] = truePositiveCounts.TryGetValue(trueLabel, out var tp) ? tp + 1 : 1;
                }
            }
        }

        var metrics = new List<LabelMetric>();
        foreach (var labelId in allLabelIds.OrderBy(x => x))
        {
            var support = supportCounts.TryGetValue(labelId, out var s) ? s : 0;
            var predicted = predictedCounts.TryGetValue(labelId, out var p) ? p : 0;
            var tp = truePositiveCounts.TryGetValue(labelId, out var t) ? t : 0;
            var precision = predicted == 0 ? 0.0d : (double)tp / predicted;
            var recall = support == 0 ? 0.0d : (double)tp / support;
            var f1 = precision + recall == 0 ? 0.0d : (2.0d * precision * recall) / (precision + recall);
            metrics.Add(new LabelMetric(labelId, support, precision, recall, f1));
        }

        return metrics;
    }

    private static List<Confusion> BuildConfusions(List<Prediction> predictions)
    {
        var map = new Dictionary<(int TrueLabel, int PredLabel), int>();
        foreach (var p in predictions)
        {
            var primaryTrue = p.TrueLabels.OrderBy(x => x).FirstOrDefault();
            if (primaryTrue == 0 || p.PredictedLabelId == primaryTrue)
            {
                continue;
            }

            var key = (primaryTrue, p.PredictedLabelId);
            map[key] = map.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        return map.Select(kvp => new Confusion(kvp.Key.TrueLabel, kvp.Key.PredLabel, kvp.Value)).ToList();
    }
}
