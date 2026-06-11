using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EsportsBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchAutoServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAutoServer",
                table: "Matches",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAutoServer",
                table: "Matches");
        }
    }
}
