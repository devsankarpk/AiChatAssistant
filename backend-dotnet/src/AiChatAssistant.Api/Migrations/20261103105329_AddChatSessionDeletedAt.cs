using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiChatAssistant.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSessionDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ChatSessions",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_UserId_DeletedAt_CreatedAt",
                table: "ChatSessions",
                columns: new[] { "UserId", "DeletedAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_UserId_DeletedAt_CreatedAt",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ChatSessions");
        }
    }
}
