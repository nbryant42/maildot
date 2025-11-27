using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace maildot.Data.Migrations
{
    /// <inheritdoc />
    public partial class UseHalfvecForEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_imap_folders_AccountId",
                table: "imap_folders");

            migrationBuilder.AlterColumn<Vector>(
                name: "Vector",
                table: "message_embeddings",
                type: "halfvec(1024)",
                nullable: false,
                oldClrType: typeof(Vector),
                oldType: "vector(1024)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Vector>(
                name: "Vector",
                table: "message_embeddings",
                type: "vector(1024)",
                nullable: false,
                oldClrType: typeof(Vector),
                oldType: "halfvec(1024)");

            migrationBuilder.CreateIndex(
                name: "IX_imap_folders_AccountId",
                table: "imap_folders",
                column: "AccountId");
        }
    }
}
