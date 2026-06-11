using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EsportsBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartScoutingTeamInvite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CaptainId",
                table: "TeamInvites",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvites_CaptainId",
                table: "TeamInvites",
                column: "CaptainId");

            migrationBuilder.AddForeignKey(
                name: "FK_TeamInvites_Users_CaptainId",
                table: "TeamInvites",
                column: "CaptainId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TeamInvites_Users_CaptainId",
                table: "TeamInvites");

            migrationBuilder.DropIndex(
                name: "IX_TeamInvites_CaptainId",
                table: "TeamInvites");

            migrationBuilder.DropColumn(
                name: "CaptainId",
                table: "TeamInvites");
        }
    }
}
