using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace maildot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImapMessageReceivedUtcIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_imap_messages_ReceivedUtc",
                table: "imap_messages",
                column: "ReceivedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_imap_messages_ReceivedUtc",
                table: "imap_messages");
        }
    }
}
