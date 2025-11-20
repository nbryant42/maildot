# PostgreSQL Schema

The initial schema is designed to mirror what the app already tracks locally (accounts, folders, messages, embeddings) while leaving room for future expansion (vector queries, message bodies, sync checkpoints).

## Tables

| Table | Purpose | Key Columns |
|-------|---------|-------------|
| `imap_accounts` | Stores IMAP connection metadata and sync checkpoints. | `id (int generated always as identity, pk)`, `display_name`, `server`, `port`, `use_ssl`, `username`, `last_synced_at` |
| `imap_folders` | Folder hierarchy per account. | `id (int identity, pk)`, `account_id (fk)`, `full_name`, `display_name`, `uid_validity (bigint)`, `last_uid (bigint)`, `sync_token` |
| `imap_messages` | One row per message in a folder. | `id (int identity, pk)`, `folder_id (fk)`, `imap_uid (bigint)`, `message_id`, `subject`, `from_name`, `from_address`, `received_utc`, `hash`, `headers text[][]` |
| `message_bodies` | Normalized body/preview text for search. | `message_id (fk)`, `plain_text`, `html_text`, `sanitized_html`, `preview` |
| `message_attachments` | Attachment metadata with pg_largeobject storage. | `id (int identity, pk)`, `message_id (fk)`, `file_name`, `content_type`, `size_bytes`, `hash`, `large_object_id (oid)` |
| `message_embeddings` | pgvector column that stores one embedding per message (or per chunk). | `message_id (fk)`, `chunk_index`, `embedding vector(1024)`, `model_version`, `created_at` |

Notes:
- `imap_messages.hash` can be a deterministic SHA based on Message-Id + Date headers to avoid duplicates.
- `message_embeddings.embedding` uses `vector(1024)` to match the Qwen3-Embedding-0.6B model.

## EF Core Entities

```csharp
public sealed class ImapAccount
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public DateTimeOffset? LastSyncedAt { get; set; }

    public ICollection<ImapFolder> Folders { get; set; } = new List<ImapFolder>();
}

public sealed class ImapFolder
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long? UidValidity { get; set; }
    public long? LastUid { get; set; }
    public string? SyncToken { get; set; }

    public ImapAccount Account { get; set; } = default!;
    public ICollection<ImapMessage> Messages { get; set; } = new List<ImapMessage>();
}

public sealed class ImapMessage
{
    public int Id { get; set; }
    public int FolderId { get; set; }
    public long ImapUid { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public DateTimeOffset ReceivedUtc { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string[][]? Headers { get; set; }

    public ImapFolder Folder { get; set; } = default!;
    public MessageBody Body { get; set; } = default!;
    public ICollection<MessageEmbedding> Embeddings { get; set; } = new List<MessageEmbedding>();
}

public sealed class MessageBody
{
    public int MessageId { get; set; }
    public string? PlainText { get; set; }
    public string? HtmlText { get; set; }
    public string? SanitizedHtml { get; set; }
    public string Preview { get; set; } = string.Empty;

    public ImapMessage Message { get; set; } = default!;
}

public sealed class MessageAttachment
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Hash { get; set; } = string.Empty;
    public uint LargeObjectId { get; set; } // OID for pg_largeobject

    public ImapMessage Message { get; set; } = default!;
}
public sealed class MessageEmbedding
{
    public int MessageId { get; set; }
    public int ChunkIndex { get; set; }
    public float[] Vector { get; set; } = Array.Empty<float>();
    public string ModelVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public ImapMessage Message { get; set; } = default!;
}
```

### Fluent Configuration Highlights

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.HasPostgresExtension("vector");

    modelBuilder.Entity<ImapAccount>(entity =>
    {
        entity.ToTable("imap_accounts");
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.DisplayName).HasColumnName("display_name");
        // ...
    });

    modelBuilder.Entity<ImapMessage>(entity =>
    {
        entity.ToTable("imap_messages");
        entity.HasIndex(e => new { e.FolderId, e.ImapUid }).IsUnique();
        entity.HasOne(e => e.Body)
              .WithOne(b => b.Message)
              .HasForeignKey<MessageBody>(b => b.MessageId);
    });

    modelBuilder.Entity<MessageEmbedding>(entity =>
    {
        entity.ToTable("message_embeddings");
        entity.Property(e => e.Vector)
              .HasColumnType("vector(1024)");
        entity.HasKey(e => new { e.MessageId, e.ChunkIndex });
    });

    modelBuilder.Entity<MessageAttachment>(entity =>
    {
        entity.ToTable("message_attachments");
        entity.Property(e => e.LargeObjectId).HasColumnName("large_object_id");
        entity.HasOne<ImapMessage>()
              .WithMany()
              .HasForeignKey(e => e.MessageId);
    });
}
```

### Migration Plan

1. `InitialPostgresSchema` migration creates `imap_accounts`, `imap_folders`, `imap_messages`, `message_bodies`, `message_embeddings`, `message_attachments`.
2. Migration seeds nothing, but ensures pgvector extension is installed (`migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");`).
3. Future migrations can add indexes for vector similarity (`USING ivfflat`), triggers for full-text search, etc.
