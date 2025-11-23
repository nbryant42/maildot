using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace maildot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderAccountFullNameIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_imap_folders_AccountId_FullName",
                table: "imap_folders",
                columns: new[] { "AccountId", "FullName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_imap_folders_AccountId_FullName",
                table: "imap_folders");
        }
    }
}
