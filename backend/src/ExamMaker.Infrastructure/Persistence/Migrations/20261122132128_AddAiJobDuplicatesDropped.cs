using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiJobDuplicatesDropped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "duplicates_dropped",
                table: "ai_generation_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_generation_jobs_duplicates_dropped",
                table: "ai_generation_jobs",
                sql: "duplicates_dropped >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_generation_jobs_duplicates_dropped",
                table: "ai_generation_jobs");

            migrationBuilder.DropColumn(
                name: "duplicates_dropped",
                table: "ai_generation_jobs");
        }
    }
}
