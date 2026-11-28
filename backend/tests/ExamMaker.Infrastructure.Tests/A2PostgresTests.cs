using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ExamMaker.Infrastructure.Tests;

// These tests execute the committed migrations and SQL against a disposable PostgreSQL database.
public sealed class A2PostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string UserId = "40000000-0000-0000-0000-000000000001";
    private const string QuestionId = "40000000-0000-0000-0000-000000000002";
    private const string VersionId = "40000000-0000-0000-0000-000000000005";

    private async Task<Session> OpenAsync()
    {
        var session = await Session.OpenAsync(fixture.ConnectionString);
        try
        {
            await session.ExecuteAsync(Seed);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    [PostgresFact]
    public async Task Migrations_seed_rollback_and_idempotent_script_work_on_postgres()
    {
        await using var context = fixture.CreateContext();
        async Task Counts()
        {
            Assert.Equal(4, await context.Departments.CountAsync());
            Assert.Equal(4, await context.Subjects.CountAsync());
            Assert.Equal(82, await context.Topics.CountAsync());
        }
        await Counts();
        var migrator = context.GetService<IMigrator>();
        var sql = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await context.Database.ExecuteSqlRawAsync(sql);
        await context.Database.ExecuteSqlRawAsync(sql);
        await migrator.MigrateAsync("20261119063637_CompleteA2Schema");
        await context.Database.MigrateAsync();
        await Counts();
        await migrator.MigrateAsync("0");
        await context.Database.MigrateAsync();
        await Counts();
    }

    [PostgresFact]
    public async Task Original_checks_unique_keys_json_and_restrict_are_enforced()
    {
        await using var s = await OpenAsync();
        await s.RejectAsync("UPDATE users SET department_id = NULL", "23514", "ck_users_department");
        await s.RejectAsync("UPDATE questions SET ai_ordinal = NULL", "23514", "ck_questions_ai_identity");
        await s.RejectAsync("UPDATE questions SET grade = 9", "23514");
        await s.RejectAsync("UPDATE questions SET type = 'Unknown'", "23514");
        await s.RejectAsync("UPDATE questions SET payload = NULL", "23502");
        await s.RejectAsync("UPDATE questions SET payload = 'invalid'::jsonb", "22P02");
        await s.RejectAsync("UPDATE ai_generation_jobs SET attempt = 4", "23514");
        await s.RejectAsync("UPDATE ai_generation_jobs SET input_tokens = -1", "23514");
        await s.RejectAsync("UPDATE exams SET grade = 9", "23514");
        await s.RejectAsync("UPDATE exam_versions SET version_no = 1", "23505", "ux_exam_versions_exam_version_no");
        await s.RejectAsync("UPDATE exam_versions SET status = 'Unknown'", "23514");
        await s.RejectAsync("UPDATE exam_versions SET duration_minutes = 0", "23514");
        await s.RejectAsync("UPDATE exam_version_questions SET points = 0", "23514");
        await s.RejectAsync("DELETE FROM questions", "23503", "fk_exam_version_questions_source");
        await s.RejectAsync("DELETE FROM departments", "23503");
        await s.RejectAsync("UPDATE ai_usage_daily SET tokens = -1", "23514");
        await s.RejectAsync("UPDATE audit_logs SET entity_type = 'Unknown'", "23514");
        await s.RejectAsync("INSERT INTO ai_usage_daily SELECT * FROM ai_usage_daily", "23505");
        await s.RejectAsync($"""
            INSERT INTO exam_version_questions (exam_version_id, sort_order, points, source_question_id, source_revision)
            VALUES ('{VersionId}', 2, 1, '{QuestionId}', 1)
            """, "23505", "ux_exam_version_questions_source");
        // Clone the source with a new primary key but the same AI job and ordinal.
        await s.RejectAsync("""
            INSERT INTO questions
                (id, subject_id, topic_id, department_id, grade, type, difficulty, stem, payload,
                 status, revision, source, ai_job_id, ai_ordinal, ai_verification,
                 ai_reviewed_by_author, created_by, created_at, updated_at)
            SELECT gen_random_uuid(), subject_id, topic_id, department_id, grade, type, difficulty,
                stem, payload, status, revision, source, ai_job_id, ai_ordinal, ai_verification,
                ai_reviewed_by_author, created_by, created_at, updated_at FROM questions
            """, "23505", "ux_questions_ai_job_ordinal");
        Assert.Equal(0.125m, await s.ScalarAsync<decimal>("SELECT points FROM exam_version_questions"));
        Assert.Equal(1L, await s.ScalarAsync<long>("SELECT count(*) FROM exam_version_questions WHERE payload_snapshot IS NULL"));
    }

    [PostgresFact]
    public async Task System_audit_requires_a_task_note_and_supports_exam_and_collection()
    {
        await using var s = await OpenAsync();
        await s.RejectAsync("UPDATE audit_logs SET actor_id = NULL", "23514", "ck_audit_logs_system_note");
        await s.ExecuteAsync("UPDATE audit_logs SET actor_id = NULL, note = 'worker: recover expired lease', entity_type = 'Exam'");
        await s.ExecuteAsync("UPDATE audit_logs SET entity_type = 'QuestionSet'");
        await s.RejectAsync("UPDATE audit_logs SET note = '  '", "23514", "ck_audit_logs_system_note");
        await s.RejectAsync("UPDATE audit_logs SET actor_id = gen_random_uuid()", "23503", "fk_audit_logs_users");
        Assert.Equal(1L, await s.ScalarAsync<long>("SELECT count(*) FROM audit_logs WHERE actor_id IS NULL"));
    }

    [PostgresFact]
    public async Task Ai_provenance_and_job_state_metadata_are_consistent()
    {
        await using var s = await OpenAsync();
        await s.RejectAsync("UPDATE questions SET source = 'Manual'", "23514", "ck_questions_ai_identity");
        await s.RejectAsync("UPDATE questions SET ai_job_id = NULL, ai_ordinal = NULL", "23514", "ck_questions_ai_identity");
        await s.RejectAsync("UPDATE questions SET ai_ordinal = 0", "23514", "ck_questions_ai_identity");
        await s.ExecuteAsync("UPDATE questions SET source = 'Manual', ai_job_id = NULL, ai_ordinal = NULL");
        await s.RejectAsync("UPDATE ai_generation_jobs SET status = 'Running'", "23514", "ck_ai_generation_jobs_running_lease");
        await s.ExecuteAsync("UPDATE ai_generation_jobs SET status = 'Running', lease_expires_at = now() + interval '1 minute'");
        await s.RejectAsync("UPDATE ai_generation_jobs SET status = 'Succeeded'", "23514", "ck_ai_generation_jobs_finished");
        await s.ExecuteAsync("UPDATE ai_generation_jobs SET status = 'Succeeded', finished_at = now()");
        await s.RejectAsync("UPDATE ai_generation_jobs SET error_code = 'failure'", "23514", "ck_ai_generation_jobs_error");
        await s.RejectAsync("UPDATE ai_generation_jobs SET status = 'Failed'", "23514", "ck_ai_generation_jobs_error");
        await s.ExecuteAsync("UPDATE ai_generation_jobs SET status = 'Failed', error_code = 'TIMEOUT'");
        await s.RejectAsync("UPDATE ai_generation_jobs SET finished_at = NULL", "23514", "ck_ai_generation_jobs_finished");
        await s.RejectAsync("UPDATE ai_generation_jobs SET error_code = ' '", "23514", "ck_ai_generation_jobs_error");
    }

    [PostgresFact]
    public async Task Composite_foreign_keys_reject_mismatched_catalog_and_parent_changes()
    {
        await using var s = await OpenAsync();
        await s.RejectAsync("UPDATE questions SET subject_id = '20000000-0000-0000-0000-000000000002'", "23503");
        await s.RejectAsync("UPDATE questions SET department_id = '10000000-0000-0000-0000-000000000002'", "23503", "fk_questions_subjects");
        await s.RejectAsync("UPDATE questions SET grade = 11", "23503", "fk_questions_topics");
        await s.RejectAsync("UPDATE questions SET topic_id = (SELECT id FROM topics WHERE subject_id <> questions.subject_id LIMIT 1)", "23503", "fk_questions_topics");
        await s.RejectAsync("UPDATE exams SET department_id = '10000000-0000-0000-0000-000000000002'", "23503", "fk_exams_subjects");
        await s.RejectAsync("UPDATE subjects SET department_id = '10000000-0000-0000-0000-000000000002' WHERE id = '20000000-0000-0000-0000-000000000001'", "23503");
        await s.RejectAsync("UPDATE topics SET grade = 11 WHERE id = '30000000-0000-0000-0000-000000000001'", "23503", "fk_questions_topics");
    }

    [PostgresFact]
    public async Task Submit_validates_snapshots_source_revision_hash_and_points_then_freezes_content()
    {
        await using var s = await OpenAsync();
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'", "23514", "ck_exam_version_snapshot_complete");
        await SnapshotAsync(s);
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE version_no = 2", "23514", "ck_exam_version_snapshot_complete");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'", "23514", "ck_exam_version_snapshot_source");
        await s.ExecuteAsync("UPDATE questions SET status = 'Approved'");
        foreach (var column in new[] { "type", "difficulty", "stem_snapshot", "payload_snapshot" })
        {
            await s.RejectAsync($"UPDATE exam_version_questions SET {column} = NULL; " +
                $"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'",
                "23514", "ck_exam_version_snapshot_complete");
        }
        await s.RejectAsync("UPDATE exam_version_questions SET payload_snapshot = 'null'::jsonb; " +
            $"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'",
            "23514", "ck_exam_version_snapshot_complete");
        await s.ExecuteAsync("UPDATE exam_version_questions SET source_revision = 2");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'", "23514", "ck_exam_version_snapshot_source");
        await s.ExecuteAsync("UPDATE exam_version_questions SET source_revision = 1, stem_snapshot = 'incorrect'");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'", "23514", "ck_exam_version_snapshot_source");
        await SnapshotAsync(s);
        await s.ExecuteAsync($"UPDATE exam_versions SET total_points = 1 WHERE id = '{VersionId}'");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'", "23514", "ck_exam_version_points_sum");
        await s.ExecuteAsync($"UPDATE exam_versions SET total_points = 0.125, status = 'Submitted' WHERE id = '{VersionId}'");
        await s.RejectAsync("UPDATE exam_version_questions SET points = 1", "23514", "ck_exam_version_items_editable");
        await s.RejectAsync("DELETE FROM exam_version_questions", "23514", "ck_exam_version_items_editable");
        await s.RejectAsync($"UPDATE exam_versions SET duration_minutes = 60 WHERE id = '{VersionId}'", "23514", "ck_exam_version_frozen_metadata");
        await s.RejectAsync($"UPDATE exam_version_questions SET exam_version_id = '40000000-0000-0000-0000-000000000006'", "23514", "ck_exam_version_items_editable");
        // Editing a bank question after Submit must not alter or invalidate the saved snapshot.
        await s.ExecuteAsync("UPDATE questions SET stem = 'new bank content', revision = 2, status = 'Draft'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Approved' WHERE id = '{VersionId}'");
        Assert.Equal("Synthetic data", await s.ScalarAsync<string>("SELECT stem_snapshot FROM exam_version_questions"));
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Rejected' WHERE id = '{VersionId}'", "23514", "ck_exam_versions_rejected");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Rejected', review_note = 'Revise the wording' WHERE id = '{VersionId}'");
        await s.ExecuteAsync("UPDATE exam_version_questions SET stem_snapshot = 'editable again'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Draft' WHERE id = '{VersionId}'");
    }

    [PostgresFact]
    public async Task Rejected_version_can_be_archived_with_missing_snapshots_and_hash()
    {
        await using var s = await OpenAsync();
        await SnapshotAsync(s);
        await s.ExecuteAsync("UPDATE questions SET status = 'Approved'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Rejected', review_note = 'Abandon this version' WHERE id = '{VersionId}'");
        // A rejected version may have incomplete snapshots when its author abandons it.
        await s.ExecuteAsync("UPDATE exam_version_questions SET type = NULL, difficulty = NULL, stem_snapshot = NULL, payload_snapshot = NULL");
        await s.ExecuteAsync($"UPDATE exam_versions SET content_hash = NULL, total_points = 1 WHERE id = '{VersionId}'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Archived' WHERE id = '{VersionId}'");
        Assert.Equal("Archived", await s.ScalarAsync<string>($"SELECT status FROM exam_versions WHERE id = '{VersionId}'"));
        Assert.Equal(1L, await s.ScalarAsync<long>($"SELECT count(*) FROM exam_versions WHERE id = '{VersionId}' AND content_hash IS NULL AND total_points = 1"));
        await s.ExecuteAsync($"UPDATE exam_versions SET updated_at = now() WHERE id = '{VersionId}'");
        await s.RejectAsync($"UPDATE exam_versions SET total_points = 2 WHERE id = '{VersionId}'", "23514", "ck_exam_version_frozen_metadata");
        await s.RejectAsync($"""
            INSERT INTO exam_version_questions (exam_version_id, sort_order, points, source_question_id, source_revision)
            VALUES ('{VersionId}', 1, 1, '{QuestionId}', 1)
            """, "23514", "ck_exam_version_items_editable");
    }

    [PostgresFact]
    public async Task Rejected_version_can_be_archived_after_source_revision_and_status_change()
    {
        await using var s = await OpenAsync();
        await SnapshotAsync(s);
        await s.ExecuteAsync("UPDATE questions SET status = 'Approved'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Rejected', review_note = 'Do not use' WHERE id = '{VersionId}'");
        await s.ExecuteAsync("UPDATE questions SET revision = 2, stem = 'Changed source', status = 'Archived'");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'", "23514", "ck_exam_version_snapshot_source");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Archived' WHERE id = '{VersionId}'");
        Assert.Equal("Synthetic data", await s.ScalarAsync<string>("SELECT stem_snapshot FROM exam_version_questions"));
        await s.RejectAsync("UPDATE exam_version_questions SET stem_snapshot = NULL", "23514", "ck_exam_version_items_editable");
        await s.RejectAsync("DELETE FROM exam_version_questions", "23514", "ck_exam_version_items_editable");
    }

    [PostgresFact]
    public async Task Archived_versions_cannot_return_to_any_active_state()
    {
        await using var s = await OpenAsync();
        await SnapshotAsync(s);
        await s.ExecuteAsync("UPDATE questions SET status = 'Approved'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Approved' WHERE id = '{VersionId}'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Archived' WHERE id = '{VersionId}'");
        foreach (var status in new[] { "Draft", "Rejected", "Submitted", "Approved", "Published" })
        {
            // A complete snapshot would otherwise allow direct restoration to Approved/Published.
            await s.RejectAsync($"UPDATE exam_versions SET status = '{status}', review_note = 'Synthetic note', " +
                $"published_by = '{UserId}', published_at = now() WHERE id = '{VersionId}'",
                "23514", "ck_exam_version_archived_terminal");
        }
        await s.ExecuteAsync($"UPDATE exam_versions SET updated_at = now() WHERE id = '{VersionId}'");
        Assert.Equal("Archived", await s.ScalarAsync<string>($"SELECT status FROM exam_versions WHERE id = '{VersionId}'"));
    }

    [PostgresFact]
    public async Task Withdraw_and_reject_preserve_frozen_metadata_until_the_version_is_editable()
    {
        await using var s = await OpenAsync();
        await SnapshotAsync(s);
        await s.ExecuteAsync("UPDATE questions SET status = 'Approved'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Draft', content_hash = NULL WHERE id = '{VersionId}'", "23514", "ck_exam_version_frozen_metadata");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Rejected', review_note = 'Revise', total_points = 0 WHERE id = '{VersionId}'", "23514", "ck_exam_version_frozen_metadata");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Draft' WHERE id = '{VersionId}'");
        Assert.Equal("synthetic-hash", await s.ScalarAsync<string>($"SELECT content_hash FROM exam_versions WHERE id = '{VersionId}'"));
        Assert.Equal(0.125m, await s.ScalarAsync<decimal>($"SELECT total_points FROM exam_versions WHERE id = '{VersionId}'"));
        await s.ExecuteAsync($"UPDATE exam_versions SET content_hash = NULL WHERE id = '{VersionId}'");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'", "23514", "ck_exam_version_snapshot_complete");
        await SnapshotAsync(s);
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Rejected', review_note = 'Revise' WHERE id = '{VersionId}'");
        Assert.Equal("synthetic-hash", await s.ScalarAsync<string>($"SELECT content_hash FROM exam_versions WHERE id = '{VersionId}'"));
        await s.ExecuteAsync($"UPDATE exam_versions SET content_hash = NULL, total_points = 1 WHERE id = '{VersionId}'");
    }

    [PostgresFact]
    public async Task Publication_metadata_and_partial_unique_index_are_enforced()
    {
        await using var s = await OpenAsync();
        await SnapshotAsync(s);
        await s.ExecuteAsync("UPDATE questions SET status = 'Approved'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'");
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Published' WHERE id = '{VersionId}'", "23514", "ck_exam_versions_published");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Published', published_by = '{UserId}', published_at = now() WHERE id = '{VersionId}'");
        await s.RejectAsync($"""
            INSERT INTO exam_version_questions (exam_version_id, sort_order, points, source_question_id, source_revision)
            VALUES ('{VersionId}', 2, 1, '{QuestionId}', 1)
            """, "23514", "ck_exam_version_items_editable");
        await s.ExecuteAsync($"""
            INSERT INTO exam_version_questions
                SELECT '40000000-0000-0000-0000-000000000006'::uuid, sort_order, points,
                    source_question_id, source_revision, type, difficulty, stem_snapshot, payload_snapshot, explanation_snapshot
                FROM exam_version_questions WHERE exam_version_id = '{VersionId}';
            UPDATE exam_versions SET total_points = 0.125, content_hash = 'synthetic-hash-2', status = 'Submitted' WHERE version_no = 2;
            """);
        await s.RejectAsync($"UPDATE exam_versions SET status = 'Published', published_by = '{UserId}', published_at = now() WHERE version_no = 2", "23505", "ux_exam_versions_one_published");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Approved' WHERE id = '{VersionId}'");
        await s.ExecuteAsync($"UPDATE exam_versions SET status = 'Published', published_by = '{UserId}', published_at = now() WHERE version_no = 2");
    }

    [PostgresFact]
    public async Task Draft_reorder_replaces_items_atomically_and_collection_delete_detaches_links()
    {
        await using var s = await OpenAsync();
        await s.ExecuteAsync($"""
            SELECT id FROM exam_versions WHERE id = '{VersionId}' FOR UPDATE;
            UPDATE questions SET source = 'Manual', ai_job_id = NULL, ai_ordinal = NULL;
            INSERT INTO questions
                SELECT '40000000-0000-0000-0000-000000000007'::uuid, subject_id, topic_id, department_id,
                    grade, type, difficulty, stem, payload, explanation, status, revision, source,
                    ai_job_id, ai_ordinal, ai_verification, ai_verification_note, ai_reviewed_by_author,
                    created_by, submitted_at, reviewed_by, reviewed_at, review_note, created_at, updated_at FROM questions;
            INSERT INTO exam_version_questions (exam_version_id, sort_order, points, source_question_id, source_revision)
                VALUES ('{VersionId}', 2, 1, '40000000-0000-0000-0000-000000000007', 1);
            CREATE TEMP TABLE reordered ON COMMIT DROP AS SELECT * FROM exam_version_questions;
            DELETE FROM exam_version_questions;
            INSERT INTO exam_version_questions SELECT exam_version_id, 3 - sort_order, points, source_question_id,
                source_revision, type, difficulty, stem_snapshot, payload_snapshot, explanation_snapshot FROM reordered;
            INSERT INTO question_sets VALUES ('40000000-0000-0000-0000-000000000008', 'Synthetic set', '{UserId}');
            INSERT INTO question_set_items VALUES ('40000000-0000-0000-0000-000000000008', '{QuestionId}');
            """);
        Assert.Equal(2, await s.ScalarAsync<int>($"SELECT sort_order FROM exam_version_questions WHERE source_question_id = '{QuestionId}'"));
        await s.RejectAsync("DELETE FROM question_sets", "23503", "fk_question_set_items_question_sets");
        await s.ExecuteAsync("DELETE FROM question_set_items; DELETE FROM question_sets");
        Assert.Equal(2L, await s.ScalarAsync<long>("SELECT count(*) FROM questions"));
        Assert.Equal(1L, await s.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE tablename = 'question_set_items' AND indexdef LIKE '%(question_id)%'"));
    }

    [PostgresFact]
    public async Task Item_writes_and_submit_serialize_on_the_parent_version_row()
    {
        await using var first = await OpenAsync();
        await SnapshotAsync(first);
        await first.ExecuteAsync("UPDATE questions SET status = 'Approved'");
        await first.Transaction.CommitAsync();
        try
        {
            await using var editor = await Session.OpenAsync(fixture.ConnectionString);
            await editor.ExecuteAsync("UPDATE exam_version_questions SET points = points");
            await using var submitter = await Session.OpenAsync(fixture.ConnectionString);
            await submitter.ExecuteAsync("SET LOCAL lock_timeout = '250ms'");
            await submitter.RejectAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'", "55P03");
            await editor.Transaction.CommitAsync();
            await submitter.ExecuteAsync($"UPDATE exam_versions SET status = 'Submitted' WHERE id = '{VersionId}'");
            await submitter.Transaction.CommitAsync();
            await using var lateEditor = await Session.OpenAsync(fixture.ConnectionString);
            await lateEditor.RejectAsync("UPDATE exam_version_questions SET points = 1", "23514", "ck_exam_version_items_editable");
        }
        finally
        {
            // Restore the shared fixture even if an assertion above fails.
            await using var cleanup = await Session.OpenAsync(fixture.ConnectionString);
            await cleanup.ExecuteAsync("""
                UPDATE exam_versions SET status = 'Draft';
                DELETE FROM exam_version_questions; DELETE FROM exam_versions; DELETE FROM exams;
                DELETE FROM audit_logs; DELETE FROM ai_usage_daily; DELETE FROM questions;
                DELETE FROM ai_generation_jobs; DELETE FROM users;
                """);
            await cleanup.Transaction.CommitAsync();
        }
    }

    private static Task SnapshotAsync(Session s) => s.ExecuteAsync($"""
        UPDATE exam_version_questions i SET type = q.type, difficulty = q.difficulty,
            stem_snapshot = q.stem, payload_snapshot = q.payload, explanation_snapshot = q.explanation
            FROM questions q WHERE i.source_question_id = q.id;
        UPDATE exam_versions SET content_hash = 'synthetic-hash' WHERE id = '{VersionId}';
        """);

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlTransaction Transaction => transaction;

        public static async Task<Session> OpenAsync(string connectionString)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return new Session(connection, await connection.BeginTransactionAsync());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        public async Task RejectAsync(string sql, string state, string? constraint = null)
        {
            await transaction.SaveAsync("expected_failure");
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(state, error.SqlState);
            if (constraint is not null) Assert.Equal(constraint, error.ConstraintName);
            await transaction.RollbackAsync("expected_failure");
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private const string Seed = """
        INSERT INTO users (id, cognito_sub, email, full_name, role, department_id, is_active) VALUES ('40000000-0000-0000-0000-000000000001',
            'a2-test', 'test@example.invalid', 'Synthetic teacher', 'teacher',
            '10000000-0000-0000-0000-000000000001', true);
        INSERT INTO ai_generation_jobs
            (id, requested_by, params, status, attempt, max_attempts, input_tokens, output_tokens, created_at)
            VALUES ('40000000-0000-0000-0000-000000000003', '40000000-0000-0000-0000-000000000001',
                '{}', 'Queued', 0, 3, 0, 0, now());
        INSERT INTO questions
            (id, subject_id, topic_id, department_id, grade, type, difficulty, stem, payload,
             status, revision, source, ai_job_id, ai_ordinal, ai_verification,
             ai_reviewed_by_author, created_by, created_at, updated_at)
            VALUES ('40000000-0000-0000-0000-000000000002',
                '20000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001',
                '10000000-0000-0000-0000-000000000001', 10, 'MultipleChoice', 'Recognition',
                'Synthetic data', '{}', 'Draft', 1, 'AI',
                '40000000-0000-0000-0000-000000000003', 1, 'NotChecked', false,
                '40000000-0000-0000-0000-000000000001', now(), now());
        INSERT INTO exams (id, title, subject_id, grade, department_id, created_by, created_at, updated_at)
            VALUES ('40000000-0000-0000-0000-000000000004', 'Synthetic exam',
                '20000000-0000-0000-0000-000000000001', 10,
                '10000000-0000-0000-0000-000000000001',
                '40000000-0000-0000-0000-000000000001', now(), now());
        INSERT INTO exam_versions (id, exam_id, version_no, status, duration_minutes, total_points, created_at, updated_at)
            VALUES ('40000000-0000-0000-0000-000000000005', '40000000-0000-0000-0000-000000000004',
                1, 'Draft', 45, 0.125, now(), now()),
                   ('40000000-0000-0000-0000-000000000006', '40000000-0000-0000-0000-000000000004',
                2, 'Draft', 45, 0, now(), now());
        INSERT INTO exam_version_questions (exam_version_id, sort_order, points, source_question_id, source_revision)
            VALUES ('40000000-0000-0000-0000-000000000005', 1, 0.125,
                '40000000-0000-0000-0000-000000000002', 1);
        INSERT INTO ai_usage_daily VALUES ('40000000-0000-0000-0000-000000000001', '2026-11-19', 1, 123);
        INSERT INTO audit_logs (id, actor_id, entity_type, entity_id, action, created_at)
            VALUES (gen_random_uuid(), '40000000-0000-0000-0000-000000000001', 'ExamVersion',
                '40000000-0000-0000-0000-000000000005', 'create', now());
        """;
}
