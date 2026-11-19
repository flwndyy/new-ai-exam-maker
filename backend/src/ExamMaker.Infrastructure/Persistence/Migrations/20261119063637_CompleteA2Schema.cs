using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteA2Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bổ sung phần còn lại của mục 8; giữ nguyên các migration đã chạy trước đó.
            migrationBuilder.CreateTable(
                name: "ai_usage_daily",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    jobs = table.Column<int>(type: "integer", nullable: false),
                    tokens = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_daily", x => new { x.user_id, x.date });
                    table.CheckConstraint("ck_ai_usage_daily_counters", "jobs >= 0 AND tokens >= 0");
                    table.ForeignKey(
                        name: "fk_ai_usage_daily_users",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.CheckConstraint("ck_audit_logs_action", "length(btrim(action)) > 0");
                    table.CheckConstraint("ck_audit_logs_entity_type", "entity_type IN ('Question', 'ExamVersion', 'User', 'AiJob', 'Department', 'Subject', 'Topic')");
                    table.ForeignKey(
                        name: "fk_audit_logs_users",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grade = table.Column<int>(type: "integer", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exams", x => x.id);
                    table.CheckConstraint("ck_exams_grade", "grade IN (10, 11, 12)");
                    table.CheckConstraint("ck_exams_title", "length(btrim(title)) > 0");
                    table.ForeignKey(
                        name: "fk_exams_departments",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_exams_subjects",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_exams_users",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exam_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exam_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    total_points = table.Column<decimal>(type: "numeric", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "text", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exam_versions", x => x.id);
                    table.CheckConstraint("ck_exam_versions_duration", "duration_minutes BETWEEN 1 AND 600");
                    table.CheckConstraint("ck_exam_versions_status", "status IN ('Draft', 'Submitted', 'Approved', 'Rejected', 'Published', 'Archived')");
                    table.CheckConstraint("ck_exam_versions_total_points", "total_points >= 0");
                    table.CheckConstraint("ck_exam_versions_version_no", "version_no >= 1");
                    table.ForeignKey(
                        name: "fk_exam_versions_exams",
                        column: x => x.exam_id,
                        principalTable: "exams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_exam_versions_publisher",
                        column: x => x.published_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_exam_versions_reviewer",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exam_version_questions",
                columns: table => new
                {
                    exam_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    points = table.Column<decimal>(type: "numeric", nullable: false),
                    source_question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_revision = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: true),
                    difficulty = table.Column<string>(type: "text", nullable: true),
                    stem_snapshot = table.Column<string>(type: "text", nullable: true),
                    payload_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    explanation_snapshot = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exam_version_questions", x => new { x.exam_version_id, x.sort_order });
                    table.CheckConstraint("ck_exam_version_questions_difficulty", "difficulty IS NULL OR difficulty IN ('Recognition', 'Understanding', 'Application', 'HighApplication')");
                    table.CheckConstraint("ck_exam_version_questions_order", "sort_order >= 1");
                    table.CheckConstraint("ck_exam_version_questions_points", "points > 0");
                    table.CheckConstraint("ck_exam_version_questions_revision", "source_revision >= 1");
                    table.CheckConstraint("ck_exam_version_questions_type", "type IS NULL OR type IN ('MultipleChoice', 'TrueFalse', 'ShortAnswer', 'Essay')");
                    table.ForeignKey(
                        name: "fk_exam_version_questions_source",
                        column: x => x.source_question_id,
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_exam_version_questions_versions",
                        column: x => x.exam_version_id,
                        principalTable: "exam_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_department",
                table: "users",
                sql: "role = 'admin' OR department_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_questions_ai_identity",
                table: "questions",
                sql: "(ai_job_id IS NULL AND ai_ordinal IS NULL AND source = 'Manual') OR (ai_job_id IS NOT NULL AND ai_ordinal IS NOT NULL AND ai_ordinal >= 1)");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_created",
                table: "audit_logs",
                columns: new[] { "actor_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity_created",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_exam_version_questions_source_question_id",
                table: "exam_version_questions",
                column: "source_question_id");

            migrationBuilder.CreateIndex(
                name: "ux_exam_version_questions_source",
                table: "exam_version_questions",
                columns: new[] { "exam_version_id", "source_question_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exam_versions_published_by",
                table: "exam_versions",
                column: "published_by");

            migrationBuilder.CreateIndex(
                name: "IX_exam_versions_reviewed_by",
                table: "exam_versions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "ux_exam_versions_exam_version_no",
                table: "exam_versions",
                columns: new[] { "exam_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_exam_versions_one_published",
                table: "exam_versions",
                column: "exam_id",
                unique: true,
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "IX_exams_created_by",
                table: "exams",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_exams_department_id",
                table: "exams",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_exams_subject_id",
                table: "exams",
                column: "subject_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Xóa bảng con trước bảng cha để tuân thủ các khóa ngoại RESTRICT.
            migrationBuilder.DropTable(
                name: "ai_usage_daily");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "exam_version_questions");

            migrationBuilder.DropTable(
                name: "exam_versions");

            migrationBuilder.DropTable(
                name: "exams");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_department",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_questions_ai_identity",
                table: "questions");
        }
    }
}
