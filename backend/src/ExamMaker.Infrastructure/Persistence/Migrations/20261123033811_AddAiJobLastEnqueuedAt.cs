using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiJobLastEnqueuedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add nullable, backfill from created_at (no SQS send time was recorded before), then enforce NOT NULL.
            migrationBuilder.AddColumn<DateTime>(
                name: "last_enqueued_at",
                table: "ai_generation_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("UPDATE ai_generation_jobs SET last_enqueued_at = created_at;");

            migrationBuilder.AlterColumn<DateTime>(
                name: "last_enqueued_at",
                table: "ai_generation_jobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_enqueued_at",
                table: "ai_generation_jobs");
        }
    }
}
