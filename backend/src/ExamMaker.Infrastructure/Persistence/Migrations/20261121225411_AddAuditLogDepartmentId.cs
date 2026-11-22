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

            migrationBuilder.AddColumn<bool>(
                name: "is_secondary",
                table: "audit_logs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_department_created",
                table: "audit_logs",
                columns: new[] { "department_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_id",
                table: "audit_logs",
                columns: new[] { "created_at", "id" });

            // Backfill department_id only for existing entities with an immutable department (Question, Exam, ExamVersion, Department).
            // Migration note (option b): entities whose department can change over time (User, Subject, Topic, QuestionSet, AiJob)
            // or pre-deleted entities without an existing department snapshot remain NULL (admin-only) so historical events are never misattributed across departments.
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
                            WHEN 'Department' THEN (SELECT d.id FROM departments d WHERE d.id = a2.entity_id)
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
                name: "ix_audit_logs_created_id",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_department_created",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "is_secondary",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "department_id",
                table: "audit_logs");
        }
    }
}
