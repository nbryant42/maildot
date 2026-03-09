using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using maildot.Data;

#nullable disable

namespace maildot.Data.Migrations
{
    [DbContext(typeof(MailDbContext))]
    [Migration("20260308193000_AddMessageBodySanitizedHtmlVersion")]
    public partial class AddMessageBodySanitizedHtmlVersion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SanitizedHtmlVersion",
                table: "message_bodies",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SanitizedHtmlVersion",
                table: "message_bodies");
        }
    }
}
