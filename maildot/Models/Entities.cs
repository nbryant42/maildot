using System;
using System.Collections.Generic;
using Pgvector;

namespace maildot.Models;

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
    public ICollection<Label> Labels { get; set; } = new List<Label>();
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

    public ImapFolder Folder { get; set; } = default!;
    public MessageBody Body { get; set; } = default!;
    public ICollection<MessageAttachment> Attachments { get; set; } = new List<MessageAttachment>();
    public ICollection<MessageEmbedding> Embeddings { get; set; } = new List<MessageEmbedding>();
    public ICollection<MessageLabel> LabelLinks { get; set; } = new List<MessageLabel>();
}

public sealed class MessageBody
{
    public int MessageId { get; set; }
    public string? PlainText { get; set; }
    public string? HtmlText { get; set; }
    public string? SanitizedHtml { get; set; }
    public Dictionary<string, string[]>? Headers { get; set; }
    public string Preview { get; set; } = string.Empty;

    public ImapMessage Message { get; set; } = default!;
}

public sealed class MessageAttachment
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string? Disposition { get; set; }
    public long SizeBytes { get; set; }
    public string Hash { get; set; } = string.Empty;
    public uint LargeObjectId { get; set; }

    public ImapMessage Message { get; set; } = default!;
}

public sealed class MessageEmbedding
{
    public int MessageId { get; set; }
    public int ChunkIndex { get; set; }
    public Vector Vector { get; set; } = new Vector(Array.Empty<float>());
    public string ModelVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public ImapMessage Message { get; set; } = default!;
}

public sealed class Label
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentLabelId { get; set; }

    public ImapAccount Account { get; set; } = default!;
    public Label? ParentLabel { get; set; }
    public ICollection<Label> Children { get; set; } = new List<Label>();
    public ICollection<MessageLabel> MessageLabels { get; set; } = new List<MessageLabel>();
}

public sealed class MessageLabel
{
    public int LabelId { get; set; }
    public int MessageId { get; set; }

    public Label Label { get; set; } = default!;
    public ImapMessage Message { get; set; } = default!;
}

public sealed class SenderLabel
{
    public int Id { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public int LabelId { get; set; }

    public Label Label { get; set; } = default!;
}
