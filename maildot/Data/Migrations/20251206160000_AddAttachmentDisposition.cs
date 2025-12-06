using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace maildot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentDisposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Disposition",
                table: "message_attachments",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Disposition",
                table: "message_attachments");
        }
    }
}
