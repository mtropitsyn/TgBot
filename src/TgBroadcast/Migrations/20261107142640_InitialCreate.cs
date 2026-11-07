using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgBroadcast.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Broadcasts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedByUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ForwardOriginal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfirmationSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletionNotified = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Broadcasts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReceiverStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    NextUpdateId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiverStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Subscribers",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SubscribedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscribers", x => x.ChatId);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    BroadcastId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => new { x.BroadcastId, x.ChatId });
                    table.ForeignKey(
                        name: "FK_Deliveries_Broadcasts_BroadcastId",
                        column: x => x.BroadcastId,
                        principalTable: "Broadcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Broadcasts_SourceKey",
                table: "Broadcasts",
                column: "SourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Broadcasts_Status_UpdatedAtUtc",
                table: "Broadcasts",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Status_NextAttemptAtUtc",
                table: "Deliveries",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Deliveries");

            migrationBuilder.DropTable(
                name: "ReceiverStates");

            migrationBuilder.DropTable(
                name: "Subscribers");

            migrationBuilder.DropTable(
                name: "Broadcasts");
        }
    }
}
