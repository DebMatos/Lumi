using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumi.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMicroActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MicroActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MicroActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MicroActionFollowUps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MicroActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DueAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MicroActionFollowUps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MicroActionFollowUps_MicroActions_MicroActionId",
                        column: x => x.MicroActionId,
                        principalTable: "MicroActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MicroActionFollowUps_MicroActionId",
                table: "MicroActionFollowUps",
                column: "MicroActionId");

            migrationBuilder.CreateIndex(
                name: "IX_MicroActionFollowUps_Status_DueAt",
                table: "MicroActionFollowUps",
                columns: new[] { "Status", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MicroActionFollowUps");

            migrationBuilder.DropTable(
                name: "MicroActions");
        }
    }
}
