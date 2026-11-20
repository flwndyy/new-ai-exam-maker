# Exam Maker backend

From the repository root, open PowerShell and run:

```powershell
Set-Location backend
dotnet build ExamMaker.slnx
dotnet run --project src/ExamMaker.Api --urls http://localhost:5080
```

In a second terminal, run `Invoke-WebRequest http://localhost:5080/health`.
The health endpoint returns HTTP 200 when the API is running.

Start the worker with `dotnet run --project src/ExamMaker.Worker` from
`backend/`. The host starts and waits for shutdown. Job processing will
be registered when the AI pipeline is implemented.

## Configuration

Both hosts read application settings and environment variables. Local development
should leave `ParameterStore__Path` unset and provide configuration via local
environment variables instead (see `docs/a3-auth.md` and `docs/runbooks/aws-local-cli.md`).
`ParameterStore__Path` is designed for EC2 (`O3`) or administrative identities with
permissions across the entire `/exammaker/<env>` hierarchy; scoped DEV identities
with least-privilege policies will receive an `AccessDeniedException` if attempting
to query the root prefix. Environment variables override values loaded from Parameter Store.

## Project layout

Keep HTTP mapping in API controllers and put each use case in Application.
Domain owns business rules. Infrastructure implements integrations.
The API and Worker are separate entry points. Add backend test projects
under `tests/`.

## Database

The API reads the PostgreSQL connection string from `ConnectionStrings__ExamMaker`
(environment variable or Parameter Store); never commit it. Create or update a
migration from `backend/`:

```powershell
dotnet ef migrations add <Name> --project src/ExamMaker.Infrastructure --output-dir Persistence/Migrations
dotnet ef migrations script --idempotent --project src/ExamMaker.Infrastructure
```

Generating migrations and scripts does not need a running database.

The A2 migrations create all 13 tables from section 8 and seed four departments,
four subjects and 82 topics. The reviewed catalog input is in
`docs/catalog-seed-draft.md` at the repository root. Keep applied migrations in
order and add a new migration for subsequent schema changes.

From `backend/`, with `ConnectionStrings__ExamMaker` set in the current terminal
to the intended development database, run:

```powershell
dotnet test ExamMaker.slnx
dotnet ef database update --project src/ExamMaker.Infrastructure
```

Verify a fresh database with:

```sql
SELECT 'departments' AS table_name, COUNT(*) FROM departments
UNION ALL SELECT 'subjects', COUNT(*) FROM subjects
UNION ALL SELECT 'topics', COUNT(*) FROM topics;
```

Expected counts are 4, 4 and 82. `exam_versions` has a unique filtered index
for one `Published` version per exam. All foreign keys use `RESTRICT`, and
points use `numeric`. Composite foreign keys enforce catalog consistency.
The `HardenA2Constraints` migration adds system audit actors, state metadata
checks, and PostgreSQL triggers that validate and freeze submitted snapshots.
Application still creates snapshots/hashes, authorizes workflow transitions,
writes audit events and updates quota atomically.

### PostgreSQL integration tests

CI starts PostgreSQL 16 and runs the database tests on every PR. Locally,
provide `A2_TEST_PG_ADMIN` with a connection string to a disposable PostgreSQL
server whose account has CREATEDB permission. To reuse your configured local
server credentials (the configured account must have CREATEDB), run:

```powershell
$env:A2_TEST_PG_ADMIN = $env:ConnectionStrings__ExamMaker
dotnet test ExamMaker.slnx -m:1
```

The fixture creates a random `exammaker_a2_test_*` database and drops only
that database afterwards. It does not modify the development database.
Without the variable, local database tests are explicitly skipped; CI fails
if it is missing. The thirteen A2 database integration tests cover migrations and seed,
idempotent SQL, rollback/reapply, unique/partial indexes, JSONB, CHECK/FK,
system audit, catalog consistency, snapshot freezing, concurrent Submit/item
writes, transactional reordering, Rejected archival with incomplete/stale snapshots,
metadata preservation on withdraw/reject, and rejection of Archived restoration. Normal model and API tests also run.

Upgrade with `dotnet ef database update --project src/ExamMaker.Infrastructure`
using the intended database connection. Existing invalid data causes the
migration to fail atomically; correct the data before retrying. Downgrade to
the old audit schema is refused if system audit rows or Exam/QuestionSet audit
rows exist, so audit history is never silently discarded or assigned a fake user.

## Authentication (A3)

See `docs/a3-auth.md` at the repository root for Cognito environment variables,
JWT validation, database-backed permissions, `/api/v1/me`, and local testing.
A3 adds two PostgreSQL user-directory tests plus signed-token HTTP and policy
tests. Only display fields are cached; authorization fields are read on every
request. A real Cognito smoke test still requires deployment configuration and
a matching internal account.

## User administration (A4)

See `docs/a4-user-management.md` for administrator endpoints, migration,
Cognito permissions, failure compensation, read-only reconciliation and the
private `tools/ExamMaker.Bootstrap` command. Never run bootstrap in CI.

A4 review hardening: shared lazy Cognito client, bounded async/operation/lock
waits, permission-change audit details and sanitized server diagnostics.
Bootstrap capability is registered only by the private CLI.

## Catalog administration (A5)

See `docs/a5-catalog.md` for catalog endpoints, administrator permissions,
validation, restricted deletion and atomic audits. A5 reuses the existing
catalog schema and seed; no additional migration or AWS permissions are needed.
Run `dotnet test ExamMaker.slnx -c Release -m:1 --filter FullyQualifiedName~Catalog`
with `A2_TEST_PG_ADMIN` configured to include the real PostgreSQL catalog tests.
