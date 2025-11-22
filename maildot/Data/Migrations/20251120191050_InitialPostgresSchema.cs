using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace maildot.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgresSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "imap_accounts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Server = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    UseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imap_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "imap_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    uid_validity = table.Column<long>(type: "bigint", nullable: true),
                    last_uid = table.Column<long>(type: "bigint", nullable: true),
                    SyncToken = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imap_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_imap_folders_imap_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "imap_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "imap_messages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FolderId = table.Column<int>(type: "integer", nullable: false),
                    ImapUid = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    FromName = table.Column<string>(type: "text", nullable: false),
                    FromAddress = table.Column<string>(type: "text", nullable: false),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Headers = table.Column<Dictionary<string, string[]>>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imap_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_imap_messages_imap_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "imap_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_attachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    large_object_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_message_attachments_imap_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "imap_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_bodies",
                columns: table => new
                {
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    PlainText = table.Column<string>(type: "text", nullable: true),
                    HtmlText = table.Column<string>(type: "text", nullable: true),
                    SanitizedHtml = table.Column<string>(type: "text", nullable: true),
                    Preview = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_bodies", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_message_bodies_imap_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "imap_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_embeddings",
                columns: table => new
                {
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    Vector = table.Column<Vector>(type: "vector(1024)", nullable: false),
                    ModelVersion = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_embeddings", x => new { x.MessageId, x.ChunkIndex });
                    table.ForeignKey(
                        name: "FK_message_embeddings_imap_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "imap_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_imap_folders_AccountId",
                table: "imap_folders",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_imap_messages_FolderId_ImapUid",
                table: "imap_messages",
                columns: new[] { "FolderId", "ImapUid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_attachments_MessageId",
                table: "message_attachments",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_attachments");

            migrationBuilder.DropTable(
                name: "message_bodies");

            migrationBuilder.DropTable(
                name: "message_embeddings");

            migrationBuilder.DropTable(
                name: "imap_messages");

            migrationBuilder.DropTable(
                name: "imap_folders");

            migrationBuilder.DropTable(
                name: "imap_accounts");
        }
    }
}
