using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using maildot.Data;

#nullable disable

namespace maildot.Data.Migrations
{
    [DbContext(typeof(MailDbContext))]
    [Migration("20260312170000_AddAttachmentContentId")]
    public partial class AddAttachmentContentId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_id",
                table: "message_attachments",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_id",
                table: "message_attachments");
        }
    }
}
