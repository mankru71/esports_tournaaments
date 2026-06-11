using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EsportsBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase3Scouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HighlightsUrl",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlayerEndorsements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EndorsedUserId = table.Column<int>(type: "integer", nullable: false),
                    EndorserUserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerEndorsements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerEndorsements_Users_EndorsedUserId",
                        column: x => x.EndorsedUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlayerEndorsements_Users_EndorserUserId",
                        column: x => x.EndorserUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerEndorsements_EndorsedUserId",
                table: "PlayerEndorsements",
                column: "EndorsedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerEndorsements_EndorserUserId",
                table: "PlayerEndorsements",
                column: "EndorserUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerEndorsements");

            migrationBuilder.DropColumn(
                name: "HighlightsUrl",
                table: "Users");
        }
    }
}
