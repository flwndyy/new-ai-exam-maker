using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenA2Constraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_exams_subjects",
                table: "exams");

            migrationBuilder.DropForeignKey(
                name: "fk_questions_subjects",
                table: "questions");

            migrationBuilder.DropForeignKey(
                name: "fk_questions_topics",
                table: "questions");

            migrationBuilder.DropIndex(
                name: "IX_questions_subject_id",
                table: "questions");

            migrationBuilder.DropIndex(
                name: "IX_questions_topic_id",
                table: "questions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_questions_ai_identity",
                table: "questions");

            migrationBuilder.DropIndex(
                name: "IX_exams_subject_id",
                table: "exams");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_entity_type",
                table: "audit_logs");

            migrationBuilder.AlterColumn<Guid>(
                name: "actor_id",
                table: "audit_logs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_topics_id_subject_grade",
                table: "topics",
                columns: new[] { "id", "subject_id", "grade" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_subjects_id_department",
                table: "subjects",
                columns: new[] { "id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_questions_subject_id_department_id",
                table: "questions",
                columns: new[] { "subject_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_questions_topic_id_subject_id_grade",
                table: "questions",
                columns: new[] { "topic_id", "subject_id", "grade" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_questions_ai_identity",
                table: "questions",
                sql: "(ai_job_id IS NULL AND ai_ordinal IS NULL AND source = 'Manual') OR (source = 'AI' AND ai_job_id IS NOT NULL AND ai_ordinal IS NOT NULL AND ai_ordinal >= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_exams_subject_id_department_id",
                table: "exams",
                columns: new[] { "subject_id", "department_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_exam_versions_published",
                table: "exam_versions",
                sql: "status <> 'Published' OR (published_by IS NOT NULL AND published_at IS NOT NULL AND content_hash IS NOT NULL AND length(btrim(content_hash)) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_exam_versions_rejected",
                table: "exam_versions",
                sql: "status <> 'Rejected' OR (review_note IS NOT NULL AND length(btrim(review_note)) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_entity_type",
                table: "audit_logs",
                sql: "entity_type IN ('Question', 'Exam', 'ExamVersion', 'QuestionSet', 'User', 'AiJob', 'Department', 'Subject', 'Topic')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_system_note",
                table: "audit_logs",
                sql: "actor_id IS NOT NULL OR (note IS NOT NULL AND length(btrim(note)) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_generation_jobs_error",
                table: "ai_generation_jobs",
                sql: "(status <> 'Failed' OR (error_code IS NOT NULL AND length(btrim(error_code)) > 0)) AND (status <> 'Succeeded' OR error_code IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_generation_jobs_finished",
                table: "ai_generation_jobs",
                sql: "status NOT IN ('Succeeded', 'Failed') OR finished_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_generation_jobs_running_lease",
                table: "ai_generation_jobs",
                sql: "status <> 'Running' OR lease_expires_at IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_exams_subjects",
                table: "exams",
                columns: new[] { "subject_id", "department_id" },
                principalTable: "subjects",
                principalColumns: new[] { "id", "department_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_questions_subjects",
                table: "questions",
                columns: new[] { "subject_id", "department_id" },
                principalTable: "subjects",
                principalColumns: new[] { "id", "department_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_questions_topics",
                table: "questions",
                columns: new[] { "topic_id", "subject_id", "grade" },
                principalTable: "topics",
                principalColumns: new[] { "id", "subject_id", "grade" },
                onDelete: ReferentialAction.Restrict);

            // Serialize item writes with Submit and protect snapshots even outside EF.
            migrationBuilder.Sql("""
                CREATE FUNCTION guard_exam_version_items() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                DECLARE
                    parent record;
                    old_parent uuid;
                    new_parent uuid;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN old_parent := OLD.exam_version_id; END IF;
                    IF TG_OP <> 'DELETE' THEN new_parent := NEW.exam_version_id; END IF;
                    FOR parent IN
                        SELECT id, status FROM exam_versions
                        WHERE id = old_parent OR id = new_parent ORDER BY id FOR UPDATE
                    LOOP
                        IF parent.status NOT IN ('Draft', 'Rejected') THEN
                            RAISE EXCEPTION 'Version % is frozen', parent.id
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_exam_version_items_editable';
                        END IF;
                    END LOOP;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END;
                $body$;

                CREATE TRIGGER tr_guard_exam_version_items
                BEFORE INSERT OR UPDATE OR DELETE ON exam_version_questions
                FOR EACH ROW EXECUTE FUNCTION guard_exam_version_items();

                CREATE FUNCTION validate_exam_version_snapshot() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                DECLARE
                    submitting boolean;
                BEGIN
                    IF TG_OP = 'UPDATE' AND OLD.status NOT IN ('Draft', 'Rejected') THEN
                        IF ROW(NEW.exam_id, NEW.version_no, NEW.duration_minutes, NEW.total_points, NEW.content_hash)
                            IS DISTINCT FROM
                            ROW(OLD.exam_id, OLD.version_no, OLD.duration_minutes, OLD.total_points, OLD.content_hash) THEN
                            RAISE EXCEPTION 'Frozen version metadata cannot change'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_exam_version_frozen_metadata';
                        END IF;
                    END IF;
                    IF NEW.status IN ('Draft', 'Rejected') THEN RETURN NEW; END IF;
                    IF NEW.content_hash IS NULL OR length(btrim(NEW.content_hash)) = 0
                        OR NOT EXISTS (SELECT 1 FROM exam_version_questions WHERE exam_version_id = NEW.id)
                        OR EXISTS (
                            SELECT 1 FROM exam_version_questions WHERE exam_version_id = NEW.id
                            AND (type IS NULL OR difficulty IS NULL OR stem_snapshot IS NULL
                                OR length(btrim(stem_snapshot)) = 0 OR payload_snapshot IS NULL
                                OR jsonb_typeof(payload_snapshot) <> 'object')) THEN
                        RAISE EXCEPTION 'A frozen version requires a hash and complete snapshots'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_exam_version_snapshot_complete';
                    END IF;
                    IF NEW.total_points <> (SELECT sum(points) FROM exam_version_questions WHERE exam_version_id = NEW.id) THEN
                        RAISE EXCEPTION 'Total points must equal the sum of item points'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_exam_version_points_sum';
                    END IF;
                    submitting := TG_OP = 'INSERT';
                    IF TG_OP = 'UPDATE' THEN submitting := OLD.status IN ('Draft', 'Rejected'); END IF;
                    IF submitting THEN
                        -- Lock source content until the submitting transaction commits.
                        PERFORM q.id FROM questions q
                            JOIN exam_version_questions i ON i.source_question_id = q.id
                            WHERE i.exam_version_id = NEW.id ORDER BY q.id FOR SHARE OF q;
                        IF EXISTS (
                            SELECT 1 FROM exam_version_questions i JOIN questions q ON q.id = i.source_question_id
                            JOIN exams e ON e.id = NEW.exam_id
                            WHERE i.exam_version_id = NEW.id AND (
                                q.status <> 'Approved' OR i.source_revision <> q.revision
                                OR ROW(q.subject_id, q.department_id, q.grade) IS DISTINCT FROM
                                    ROW(e.subject_id, e.department_id, e.grade)
                                OR ROW(i.type, i.difficulty, i.stem_snapshot, i.explanation_snapshot)
                                    IS DISTINCT FROM ROW(q.type, q.difficulty, q.stem, q.explanation)
                                OR i.payload_snapshot IS DISTINCT FROM q.payload)) THEN
                            RAISE EXCEPTION 'Submit requires matching Approved source questions and revisions'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_exam_version_snapshot_source';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $body$;

                CREATE TRIGGER tr_validate_exam_version_snapshot
                BEFORE INSERT OR UPDATE ON exam_versions
                FOR EACH ROW EXECUTE FUNCTION validate_exam_version_snapshot();

                -- Refuse to silently grandfather invalid frozen data during an upgrade.
                UPDATE exam_versions SET status = status WHERE status NOT IN ('Draft', 'Rejected');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve audit history: incompatible rows must be handled explicitly before downgrade.
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM audit_logs WHERE actor_id IS NULL OR entity_type IN ('Exam', 'QuestionSet')) THEN
                        RAISE EXCEPTION 'Cannot downgrade: audit history requires the new actor/entity schema';
                    END IF;
                END;
                $body$;
                DROP TRIGGER tr_guard_exam_version_items ON exam_version_questions;
                DROP TRIGGER tr_validate_exam_version_snapshot ON exam_versions;
                DROP FUNCTION guard_exam_version_items();
                DROP FUNCTION validate_exam_version_snapshot();
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_exams_subjects",
                table: "exams");

            migrationBuilder.DropForeignKey(
                name: "fk_questions_subjects",
                table: "questions");

            migrationBuilder.DropForeignKey(
                name: "fk_questions_topics",
                table: "questions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_topics_id_subject_grade",
                table: "topics");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_subjects_id_department",
                table: "subjects");

            migrationBuilder.DropIndex(
                name: "IX_questions_subject_id_department_id",
                table: "questions");

            migrationBuilder.DropIndex(
                name: "IX_questions_topic_id_subject_id_grade",
                table: "questions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_questions_ai_identity",
                table: "questions");

            migrationBuilder.DropIndex(
                name: "IX_exams_subject_id_department_id",
                table: "exams");

            migrationBuilder.DropCheckConstraint(
                name: "ck_exam_versions_published",
                table: "exam_versions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_exam_versions_rejected",
                table: "exam_versions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_entity_type",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_system_note",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_generation_jobs_error",
                table: "ai_generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_generation_jobs_finished",
                table: "ai_generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_generation_jobs_running_lease",
                table: "ai_generation_jobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "actor_id",
                table: "audit_logs",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_questions_subject_id",
                table: "questions",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_questions_topic_id",
                table: "questions",
                column: "topic_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_questions_ai_identity",
                table: "questions",
                sql: "(ai_job_id IS NULL AND ai_ordinal IS NULL AND source = 'Manual') OR (ai_job_id IS NOT NULL AND ai_ordinal IS NOT NULL AND ai_ordinal >= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_exams_subject_id",
                table: "exams",
                column: "subject_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_entity_type",
                table: "audit_logs",
                sql: "entity_type IN ('Question', 'ExamVersion', 'User', 'AiJob', 'Department', 'Subject', 'Topic')");

            migrationBuilder.AddForeignKey(
                name: "fk_exams_subjects",
                table: "exams",
                column: "subject_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_questions_subjects",
                table: "questions",
                column: "subject_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_questions_topics",
                table: "questions",
                column: "topic_id",
                principalTable: "topics",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
