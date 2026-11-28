using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiGenerationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_generation_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    @params = table.Column<string>(name: "params", type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    model_id = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_generation_jobs", x => x.id);
                    table.CheckConstraint("ck_ai_generation_jobs_attempts", "attempt >= 0 AND max_attempts >= 1 AND attempt <= max_attempts");
                    table.CheckConstraint("ck_ai_generation_jobs_status", "status IN ('Queued', 'Running', 'Succeeded', 'Failed')");
                    table.CheckConstraint("ck_ai_generation_jobs_tokens", "input_tokens >= 0 AND output_tokens >= 0");
                    table.ForeignKey(
                        name: "fk_ai_generation_jobs_users",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_generation_jobs_requested_created",
                table: "ai_generation_jobs",
                columns: new[] { "requested_by", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_generation_jobs_status_lease",
                table: "ai_generation_jobs",
                columns: new[] { "status", "lease_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_generation_jobs");
        }
    }
}
