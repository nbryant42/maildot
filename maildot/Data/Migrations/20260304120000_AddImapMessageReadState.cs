using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using maildot.Data;

#nullable disable

namespace maildot.Data.Migrations
{
    [DbContext(typeof(MailDbContext))]
    [Migration("20260304120000_AddImapMessageReadState")]
    public partial class AddImapMessageReadState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRead",
                table: "imap_messages",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsRead",
                table: "imap_messages");
        }
    }
}
