using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogDepartmentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                table: "audit_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_department_created",
                table: "audit_logs",
                columns: new[] { "department_id", "created_at" });

            // Backfill department_id for existing audit rows whose target entity still resolves to a department.
            migrationBuilder.Sql("""
                UPDATE audit_logs AS a
                SET department_id = resolved.department_id
                FROM (
                    SELECT a2.id,
                        CASE a2.entity_type
                            WHEN 'Question' THEN (SELECT q.department_id FROM questions q WHERE q.id = a2.entity_id)
                            WHEN 'Exam' THEN (SELECT e.department_id FROM exams e WHERE e.id = a2.entity_id)
                            WHEN 'ExamVersion' THEN (
                                SELECT e.department_id
                                FROM exam_versions v
                                JOIN exams e ON e.id = v.exam_id
                                WHERE v.id = a2.entity_id
                            )
                            WHEN 'QuestionSet' THEN (
                                SELECT q.department_id
                                FROM question_set_items i
                                JOIN questions q ON q.id = i.question_id
                                WHERE i.question_set_id = a2.entity_id
                                ORDER BY i.question_id
                                LIMIT 1
                            )
                            WHEN 'AiJob' THEN (
                                SELECT u.department_id
                                FROM ai_generation_jobs j
                                JOIN users u ON u.id = j.requested_by
                                WHERE j.id = a2.entity_id
                            )
                            WHEN 'User' THEN (SELECT u.department_id FROM users u WHERE u.id = a2.entity_id)
                            WHEN 'Department' THEN (SELECT d.id FROM departments d WHERE d.id = a2.entity_id)
                            WHEN 'Subject' THEN (SELECT s.department_id FROM subjects s WHERE s.id = a2.entity_id)
                            WHEN 'Topic' THEN (
                                SELECT s.department_id
                                FROM topics t
                                JOIN subjects s ON s.id = t.subject_id
                                WHERE t.id = a2.entity_id
                            )
                        END AS department_id
                    FROM audit_logs a2
                    WHERE a2.department_id IS NULL
                ) AS resolved
                WHERE a.id = resolved.id
                  AND resolved.department_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_logs_department_created",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "department_id",
                table: "audit_logs");
        }
    }
}
