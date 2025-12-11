using maildot.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace maildot.Data;

public sealed class MailDbContext : DbContext
{
    public MailDbContext(DbContextOptions<MailDbContext> options) : base(options)
    {
    }

    public DbSet<ImapAccount> ImapAccounts => Set<ImapAccount>();
    public DbSet<ImapFolder> ImapFolders => Set<ImapFolder>();
    public DbSet<ImapMessage> ImapMessages => Set<ImapMessage>();
    public DbSet<MessageBody> MessageBodies => Set<MessageBody>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<MessageEmbedding> MessageEmbeddings => Set<MessageEmbedding>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<MessageLabel> MessageLabels => Set<MessageLabel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<ImapAccount>(entity =>
        {
            entity.ToTable("imap_accounts");
            entity.Property(e => e.Id).HasColumnName("id");
        });

        modelBuilder.Entity<ImapFolder>(entity =>
        {
            entity.ToTable("imap_folders");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.UidValidity).HasColumnName("uid_validity");
            entity.Property(e => e.LastUid).HasColumnName("last_uid");
            entity.HasIndex(f => new { f.AccountId, f.FullName }).IsUnique();
            entity.HasOne(f => f.Account)
                  .WithMany(a => a.Folders)
                  .HasForeignKey(f => f.AccountId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImapMessage>(entity =>
        {
            entity.ToTable("imap_messages");
            entity.HasIndex(e => new { e.FolderId, e.ImapUid }).IsUnique();
            entity.HasIndex(e => e.ReceivedUtc);
            entity.HasOne(m => m.Folder)
                  .WithMany(f => f.Messages)
                  .HasForeignKey(m => m.FolderId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Body)
                  .WithOne(b => b.Message)
                  .HasForeignKey<MessageBody>(b => b.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageBody>(entity =>
        {
            entity.ToTable("message_bodies");
            entity.HasKey(b => b.MessageId);
            entity.Property(b => b.Headers).HasColumnType("jsonb");
        });

        modelBuilder.Entity<MessageAttachment>(entity =>
        {
            entity.ToTable("message_attachments");
            entity.Property(e => e.LargeObjectId).HasColumnName("large_object_id");
            entity.HasOne(a => a.Message)
                  .WithMany(m => m.Attachments)
                  .HasForeignKey(a => a.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageEmbedding>(entity =>
        {
            entity.ToTable("message_embeddings");
            entity.HasKey(e => new { e.MessageId, e.ChunkIndex });
            entity.Property(e => e.Vector).HasColumnType("halfvec(1024)");
            entity.HasOne(e => e.Message)
                  .WithMany(m => m.Embeddings)
                  .HasForeignKey(e => e.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Label>(entity =>
        {
            entity.ToTable("labels");
            entity.Property(e => e.ParentLabelId).HasColumnName("parent_label_id");
            entity.HasIndex(e => new { e.AccountId, e.ParentLabelId, e.Name }).IsUnique();

            entity.HasOne(l => l.Account)
                  .WithMany(a => a.Labels)
                  .HasForeignKey(l => l.AccountId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(l => l.ParentLabel)
                  .WithMany(l => l.Children)
                  .HasForeignKey(l => l.ParentLabelId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageLabel>(entity =>
        {
            entity.ToTable("message_labels");
            entity.HasKey(e => new { e.LabelId, e.MessageId });
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => e.LabelId);

            entity.HasOne(ml => ml.Label)
                  .WithMany(l => l.MessageLabels)
                  .HasForeignKey(ml => ml.LabelId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ml => ml.Message)
                  .WithMany(m => m.LabelLinks)
                  .HasForeignKey(ml => ml.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
