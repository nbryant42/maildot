using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace maildot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "labels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    parent_label_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_labels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_labels_imap_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "imap_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_labels_labels_parent_label_id",
                        column: x => x.parent_label_id,
                        principalTable: "labels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_labels",
                columns: table => new
                {
                    LabelId = table.Column<int>(type: "integer", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_labels", x => new { x.LabelId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_message_labels_imap_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "imap_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_message_labels_labels_LabelId",
                        column: x => x.LabelId,
                        principalTable: "labels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_labels_account_id_parent_label_id_name",
                table: "labels",
                columns: new[] { "AccountId", "parent_label_id", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_labels_parent_label_id",
                table: "labels",
                column: "parent_label_id");

            migrationBuilder.CreateIndex(
                name: "IX_labels_AccountId",
                table: "labels",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_message_labels_LabelId",
                table: "message_labels",
                column: "LabelId");

            migrationBuilder.CreateIndex(
                name: "IX_message_labels_MessageId",
                table: "message_labels",
                column: "MessageId");

            migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_labels_account_id_parent_label_id_name";
CREATE UNIQUE INDEX "IX_labels_account_id_parent_label_id_name" ON "labels" ("AccountId", "parent_label_id", "Name") NULLS NOT DISTINCT;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_labels");

            migrationBuilder.DropTable(
                name: "labels");
        }
    }
}
