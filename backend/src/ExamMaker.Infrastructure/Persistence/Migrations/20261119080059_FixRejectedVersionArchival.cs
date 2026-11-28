using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixRejectedVersionArchival : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Update existing installations without rewriting an applied migration.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION validate_exam_version_snapshot() RETURNS trigger
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
                    -- Archival preserves abandoned content; only Submit validates it for use.
                    IF NEW.status IN ('Draft', 'Rejected', 'Archived') THEN RETURN NEW; END IF;
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
                    submitting := NEW.status = 'Submitted';
                    IF TG_OP = 'UPDATE' THEN
                        submitting := submitting AND OLD.status IN ('Draft', 'Rejected');
                    END IF;
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION validate_exam_version_snapshot() RETURNS trigger
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
                """);
        }
    }
}
