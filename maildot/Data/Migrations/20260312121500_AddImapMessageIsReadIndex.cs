using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using maildot.Data;

#nullable disable

namespace maildot.Data.Migrations
{
    [DbContext(typeof(MailDbContext))]
    [Migration("20260312121500_AddImapMessageIsReadIndex")]
    public partial class AddImapMessageIsReadIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_imap_messages_IsRead",
                table: "imap_messages",
                column: "IsRead");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_imap_messages_IsRead",
                table: "imap_messages");
        }
    }
}
