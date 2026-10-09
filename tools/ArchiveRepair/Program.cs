using System.Data;
using System.Globalization;
using System.Text.Json;
using maildot.Models;
using Npgsql;
using Windows.Security.Credentials;

// A deliberately explicit, one-time tool. Default execution only plans and rolls back.
string Option(string key) => args.SkipWhile(a => a != key).Skip(1).FirstOrDefault()
    ?? throw new ArgumentException($"Missing {key}");
var boundary = int.Parse(Option("--archive-boundary"), CultureInfo.InvariantCulture);
var since = DateTimeOffset.Parse(Option("--since"), CultureInfo.InvariantCulture).ToUniversalTime();
var apply = args.Contains("--apply");
var expected = apply ? int.Parse(Option("--expected-count"), CultureInfo.InvariantCulture) : -1;
var testConnection = Environment.GetEnvironmentVariable("MAILDOT_REPAIR_TEST_CONNECTION");
string connectionString;
if (!string.IsNullOrWhiteSpace(testConnection))
{
    var test = new NpgsqlConnectionStringBuilder(testConnection);
    if (test.Database?.StartsWith("maildot_test_", StringComparison.Ordinal) != true)
        throw new ArgumentException("Test connection must name a maildot_test_* database.");
    connectionString = test.ConnectionString;
}
else
{
    var settings = PostgresSettingsStore.Load();
    var vault = new PasswordVault();
    var credential = vault.Retrieve($"PG:{settings.Host.Trim()}:{settings.Username.Trim()}", settings.Username);
    credential.RetrievePassword();
    connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = settings.Host, Port = settings.Port, Database = settings.Database,
        Username = settings.Username, Password = credential.Password,
        SslMode = settings.UseSsl ? SslMode.Require : SslMode.Disable,
        ApplicationName = "maildot one-time archive repair"
    }.ConnectionString;
}

await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();
await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);
async Task<int> Execute(string sql)
{
    await using var command = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 300 };
    return await command.ExecuteNonQueryAsync();
}
async Task<long> Count(string sql)
{
    await using var command = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 300 };
    return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}
await Execute("SET LOCAL lock_timeout = '10s'; SET LOCAL statement_timeout = '300s'");
if (apply)
{
    // Block app/backfill writes while retaining concurrent read access. All changes,
    // including recovery snapshots, are committed together or rolled back together.
    await Execute("LOCK TABLE public.imap_folders, public.imap_messages, public.message_bodies, public.message_attachments, public.message_embeddings, public.message_labels IN SHARE ROW EXCLUSIVE MODE");
}

await using (var command = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Candidates.sql")), conn, tx) { CommandTimeout = 300 })
{
    command.Parameters.AddWithValue("boundary", boundary);
    command.Parameters.AddWithValue("since", since);
    await command.ExecuteNonQueryAsync();
}

var candidates = new List<(int Keep, int Remove)>();
await using (var command = new NpgsqlCommand("SELECT keep_id, remove_id FROM repair_candidates ORDER BY keep_id, remove_id", conn, tx))
await using (var reader = await command.ExecuteReaderAsync())
    while (await reader.ReadAsync()) candidates.Add((reader.GetInt32(0), reader.GetInt32(1)));

var attachments = new Dictionary<(int, int), List<(uint, uint)>>();
await using (var command = new NpgsqlCommand("SELECT keep_id, remove_id, left_oid, right_oid FROM repair_attachment_pairs", conn, tx))
await using (var reader = await command.ExecuteReaderAsync())
    while (await reader.ReadAsync())
    {
        var key = (reader.GetInt32(0), reader.GetInt32(1));
        if (!attachments.TryGetValue(key, out var list)) attachments[key] = list = [];
        list.Add((reader.GetFieldValue<uint>(2), reader.GetFieldValue<uint>(3)));
    }

async Task<bool> SameBytes(uint left, uint right)
{
    if (left == right) return true;
    // Compare the actual large objects, through EOF, without loading whole attachments
    // or relying solely on possibly incorrect recorded hashes/lengths.
    const int chunk = 1024 * 1024;
    for (long offset = 0; ; offset += chunk)
    {
        await using var command = new NpgsqlCommand("""
            WITH bytes AS MATERIALIZED (SELECT lo_get(@left, @offset, @chunk) AS l, lo_get(@right, @offset, @chunk) AS r)
            SELECT l = r, octet_length(l) FROM bytes
            """, conn, tx) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("left", NpgsqlTypes.NpgsqlDbType.Oid, left);
        command.Parameters.AddWithValue("right", NpgsqlTypes.NpgsqlDbType.Oid, right);
        command.Parameters.AddWithValue("offset", offset);
        command.Parameters.AddWithValue("chunk", chunk);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        if (!reader.GetBoolean(0)) return false;
        if (reader.GetInt32(1) < chunk) return true;
    }
}

var usedOriginals = new HashSet<int>();
var usedCopies = new HashSet<int>();
var plan = new List<(int Keep, int Remove)>();
var unequalAttachments = 0;
foreach (var pair in candidates)
{
    if (usedOriginals.Contains(pair.Keep) || usedCopies.Contains(pair.Remove)) continue;
    var equal = true;
    foreach (var (left, right) in attachments.GetValueOrDefault(pair) ?? [])
        if (!await SameBytes(left, right)) { equal = false; break; }
    if (!equal) { unequalAttachments++; continue; }
    usedOriginals.Add(pair.Keep);
    usedCopies.Add(pair.Remove);
    plan.Add(pair);
}
await using (var command = new NpgsqlCommand("INSERT INTO repair_plan SELECT unnest(@keep), unnest(@remove)", conn, tx))
{
    command.Parameters.AddWithValue("keep", plan.Select(p => p.Keep).ToArray());
    command.Parameters.AddWithValue("remove", plan.Select(p => p.Remove).ToArray());
    await command.ExecuteNonQueryAsync();
}
var datesRestored = await Count("""
    SELECT count(*) FROM repair_plan p JOIN public.imap_messages o ON o."Id"=p.keep_id
    JOIN public.imap_messages n ON n."Id"=p.remove_id
    WHERE n."ReceivedUtc">o."ReceivedUtc"+interval '1 day'
    """);
Console.WriteLine(JsonSerializer.Serialize(new { mode = apply ? "apply" : "dry-run", boundary, since,
    candidatePairs = candidates.Count, exactDuplicates = plan.Count, unequalAttachments, datesRestored }));
if (!apply) { await tx.RollbackAsync(); return; }
if (plan.Count != expected || plan.Count == 0) throw new InvalidOperationException("Plan differs from reviewed count, or there is nothing to remove; rolling back.");

var schema = "maildot_repair_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
await Execute($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.plan AS SELECT * FROM repair_plan");
await Execute($"""
    CREATE TABLE {schema}.imap_messages AS SELECT m.* FROM public.imap_messages m
    WHERE "Id" IN (SELECT keep_id FROM repair_plan UNION SELECT remove_id FROM repair_plan)
    """);
foreach (var table in new[] { "message_bodies", "message_attachments", "message_embeddings", "message_labels" })
    await Execute($"CREATE TABLE {schema}.{table} AS SELECT t.* FROM public.{table} t WHERE \"MessageId\" IN (SELECT keep_id FROM repair_plan UNION SELECT remove_id FROM repair_plan)");

var labelsAdded = await Execute("""
    INSERT INTO public.message_labels ("LabelId", "MessageId")
    SELECT l."LabelId", p.keep_id FROM public.message_labels l JOIN repair_plan p ON p.remove_id=l."MessageId"
    ON CONFLICT DO NOTHING
    """);
var embeddingsAdded = await Execute("""
    INSERT INTO public.message_embeddings ("MessageId", "ChunkIndex", "Vector", "ModelVersion", "CreatedAt")
    SELECT p.keep_id, e."ChunkIndex", e."Vector", e."ModelVersion", e."CreatedAt"
    FROM public.message_embeddings e JOIN repair_plan p ON p.remove_id=e."MessageId"
    ON CONFLICT DO NOTHING
    """);
// Delete the newer rows first to release the unique (FolderId, ImapUid) values.
// Their attachment bytes are intentionally retained: the recovery snapshot refers to them.
var removed = await Execute("DELETE FROM public.imap_messages m USING repair_plan p WHERE m.\"Id\"=p.remove_id");
var rebound = await Execute($"""
    UPDATE public.imap_messages o SET "ImapUid"=n."ImapUid", "Hash"=o."MessageId"||':'||n."ImapUid"::text,
        "IsRead"=o."IsRead" AND n."IsRead"
    FROM {schema}.imap_messages n JOIN repair_plan p ON n."Id"=p.remove_id
    WHERE o."Id"=p.keep_id
    """);
if (removed != expected || rebound != expected) throw new InvalidOperationException("Unexpected changed-row counts; rolling back.");
if (await Count($"""
    SELECT count(*) FROM repair_plan p JOIN public.imap_messages o ON o."Id"=p.keep_id
    JOIN {schema}.imap_messages n ON n."Id"=p.remove_id
    JOIN {schema}.imap_messages prior ON prior."Id"=p.keep_id
    WHERE o."ImapUid"=n."ImapUid" AND o."FolderId"=prior."FolderId" AND o."ReceivedUtc"=prior."ReceivedUtc"
    """) != expected) throw new InvalidOperationException("Survivor validation failed; rolling back.");
if (await Count($"""
    SELECT count(*) FROM {schema}.message_labels l JOIN repair_plan p ON l."MessageId" IN (p.keep_id,p.remove_id)
    WHERE NOT EXISTS (SELECT FROM public.message_labels x WHERE x."MessageId"=p.keep_id AND x."LabelId"=l."LabelId")
    """) != 0) throw new InvalidOperationException("Label preservation validation failed; rolling back.");
await tx.CommitAsync();
Console.WriteLine(JsonSerializer.Serialize(new { committed = true, removed, rebound, labelsAdded, embeddingsAdded, backupSchema = schema }));
