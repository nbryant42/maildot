using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using maildot.Data;

#nullable disable

namespace maildot.Data.Migrations
{
    [DbContextAttribute(typeof(MailDbContext))]
    [Migration("20260408173000_AddImapMessageFolderReceivedUtcIndex")]
    public partial class AddImapMessageFolderReceivedUtcIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_imap_messages_FolderId_ReceivedUtc",
                table: "imap_messages",
                columns: new[] { "FolderId", "ReceivedUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_imap_messages_FolderId_ReceivedUtc",
                table: "imap_messages");
        }
    }
}
