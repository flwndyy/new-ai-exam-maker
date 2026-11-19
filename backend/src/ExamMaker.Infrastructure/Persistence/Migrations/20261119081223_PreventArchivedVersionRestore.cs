using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreventArchivedVersionRestore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Archived versions are terminal in the MVP, including writes made outside Application.
            migrationBuilder.Sql("""
                CREATE FUNCTION guard_archived_exam_version() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                BEGIN
                    IF OLD.status = 'Archived' AND NEW.status <> 'Archived' THEN
                        RAISE EXCEPTION 'Archived versions cannot be restored'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_exam_version_archived_terminal';
                    END IF;
                    RETURN NEW;
                END;
                $body$;

                CREATE TRIGGER tr_guard_archived_exam_version
                BEFORE UPDATE OF status ON exam_versions
                FOR EACH ROW EXECUTE FUNCTION guard_archived_exam_version();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER tr_guard_archived_exam_version ON exam_versions;
                DROP FUNCTION guard_archived_exam_version();
                """);
        }
    }
}
