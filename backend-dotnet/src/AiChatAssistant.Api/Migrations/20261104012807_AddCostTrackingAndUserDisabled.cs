using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiChatAssistant.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCostTrackingAndUserDisabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DisabledAt",
                table: "Users",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CostEstimate",
                table: "UsageLogs",
                type: "decimal(14,8)",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(10,4)",
                oldDefaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "CompletionTokens",
                table: "UsageLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "UsageLogs",
                type: "varchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                table: "UsageLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "UsageLogs");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "UsageLogs");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "UsageLogs");

            migrationBuilder.AlterColumn<decimal>(
                name: "CostEstimate",
                table: "UsageLogs",
                type: "decimal(10,4)",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(14,8)",
                oldDefaultValue: 0m);
        }
    }
}
