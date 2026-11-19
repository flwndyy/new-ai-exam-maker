using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "questions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grade = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    difficulty = table.Column<string>(type: "text", nullable: false),
                    stem = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    explanation = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    ai_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ai_ordinal = table.Column<int>(type: "integer", nullable: true),
                    ai_verification = table.Column<string>(type: "text", nullable: false),
                    ai_verification_note = table.Column<string>(type: "text", nullable: true),
                    ai_reviewed_by_author = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questions", x => x.id);
                    table.CheckConstraint("ck_questions_ai_verification", "ai_verification IN ('NotChecked', 'Agree', 'Disagree')");
                    table.CheckConstraint("ck_questions_difficulty", "difficulty IN ('Recognition', 'Understanding', 'Application', 'HighApplication')");
                    table.CheckConstraint("ck_questions_grade", "grade IN (10, 11, 12)");
                    table.CheckConstraint("ck_questions_revision", "revision >= 1");
                    table.CheckConstraint("ck_questions_source", "source IN ('AI', 'Manual')");
                    table.CheckConstraint("ck_questions_status", "status IN ('Draft', 'Submitted', 'Approved', 'Rejected', 'Archived')");
                    table.CheckConstraint("ck_questions_type", "type IN ('MultipleChoice', 'TrueFalse', 'ShortAnswer', 'Essay')");
                    table.ForeignKey(
                        name: "fk_questions_ai_generation_jobs",
                        column: x => x.ai_job_id,
                        principalTable: "ai_generation_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questions_creator",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questions_departments",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questions_reviewer",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questions_subjects",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_questions_topics",
                        column: x => x.topic_id,
                        principalTable: "topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_questions_created_by",
                table: "questions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_questions_department_id",
                table: "questions",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_questions_reviewed_by",
                table: "questions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_questions_subject_id",
                table: "questions",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_questions_topic_id",
                table: "questions",
                column: "topic_id");

            migrationBuilder.CreateIndex(
                name: "ux_questions_ai_job_ordinal",
                table: "questions",
                columns: new[] { "ai_job_id", "ai_ordinal" },
                unique: true,
                filter: "ai_job_id IS NOT NULL AND ai_ordinal IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "questions");
        }
    }
}
