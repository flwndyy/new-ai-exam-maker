using ExamMaker.Application.Users;
using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using ExamMaker.Infrastructure.Persistence;

namespace ExamMaker.Infrastructure.Tests;

public sealed class UserManagementTests : IAsyncLifetime
{
    private readonly PostgresFixture fixture = new();
    private readonly FakeCognitoUserAdmin cognito = new();
    private readonly Guid actorId = Guid.NewGuid();
    private static readonly Guid DepartmentId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    private UserManagementService Service(ExamMaker.Infrastructure.Persistence.ExamMakerDbContext database) =>
        new(database, cognito, NullLogger<UserManagementService>.Instance);

    private async Task SeedAdminAsync()
    {
        await using var database = fixture.CreateContext();
        database.Users.Add(new User { Id = actorId, CognitoSub = "admin-sub", Email = "admin@example.invalid", FullName = "Admin", Role = "admin" });
        await database.SaveChangesAsync();
        cognito.Accounts["admin-sub"] = new("admin-sub", "admin@example.invalid", "Admin", true, true, ["admin"]);
    }

    private async Task<AdminUser> CreateTeacherAsync()
    {
        await using var database = fixture.CreateContext();
        return await Service(database).CreateAsync(actorId, new(" Teacher@Example.Invalid ", " Teacher ", "teacher", DepartmentId), default);
    }

    [PostgresFact]
    // Verify migration defaults, case-insensitive uniqueness and local/remote provisioning.
    public async Task Creation_persists_timestamp_and_managed_role_and_rejects_duplicate_email()
    {
        await SeedAdminAsync();
        var user = await CreateTeacherAsync();
        Assert.Equal("teacher@example.invalid", user.Email);
        Assert.True(user.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(new[] { "teacher" }, cognito.Accounts[user.CognitoSub].Groups);
        await using var database = fixture.CreateContext();
        Assert.Contains(await database.AuditLogs.ToListAsync(), x => x.EntityId == user.Id && x.ActorId == actorId && x.Action == "CreateUser");
        Assert.Equal(409, (await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(actorId,
            new("TEACHER@example.invalid", "Duplicate", "teacher", DepartmentId), default))).StatusCode);
        Assert.Equal(1, cognito.Creates);
        database.Users.Add(new User { Id = Guid.NewGuid(), CognitoSub = "duplicate-sub", Email = " TEACHER@EXAMPLE.INVALID ", FullName = "Duplicate", Role = "admin" });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task List_filters_and_pagination_use_the_real_database_query()
    {
        await SeedAdminAsync();
        var user = await CreateTeacherAsync();
        await using var database = fixture.CreateContext();
        var service = Service(database);
        var page = await service.ListAsync(new(1, 1, "teacher", DepartmentId, true, "TEACH"), default);
        Assert.Equal(1, page.TotalItems);
        Assert.Equal(user.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.TotalPages);
        Assert.Empty((await service.ListAsync(new(2, 1, "teacher"), default)).Items);
        Assert.Equal(400, (await Assert.ThrowsAsync<UserOperationException>(() => service.ListAsync(new(0), default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserOperationException>(() => service.GetAsync(Guid.NewGuid(), default))).StatusCode);
    }

    [PostgresFact]
    public async Task Busy_global_lock_fails_before_any_aws_mutation()
    {
        await SeedAdminAsync();
        await using var holder = fixture.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48683401)");
        await using var database = fixture.CreateContext();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(actorId,
            new("busy@example.invalid", "Busy", "admin", null), watchdog.Token));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(0, cognito.Creates);
    }

    [PostgresFact(Timeout = 45000)]
    public async Task Operation_deadline_compensates_a_stalled_provisioning_without_caller_cancellation()
    {
        await SeedAdminAsync();
        cognito.StallRoleAssignment = true;
        await using var database = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(actorId,
            new("timeout@example.invalid", "Timeout", "admin", null), CancellationToken.None));
        Assert.Equal(500, error.StatusCode);
        Assert.Equal(1, cognito.Deletes);
        Assert.False(cognito.CleanupWasCancelled);
        Assert.False(await database.Users.AnyAsync(x => x.Email == "timeout@example.invalid"));
    }

    [PostgresFact]
    // Delete only the account created by this failed operation, even after request cancellation.
    public async Task Provisioning_failure_compensates_with_an_independent_cancellation_token()
    {
        await SeedAdminAsync();
        using var request = new CancellationTokenSource();
        cognito.FailNextMutation = () => { request.Cancel(); throw new OperationCanceledException(request.Token); };
        await using var database = fixture.CreateContext();
        await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(actorId,
            new("teacher@example.invalid", "Teacher", "teacher", DepartmentId), request.Token));
        Assert.Equal(1, cognito.Deletes);
        Assert.False(cognito.CleanupWasCancelled);
        Assert.False(await database.Users.AnyAsync(x => x.Role == "teacher"));
        Assert.Single(cognito.Accounts);
    }

    [PostgresFact]
    // A transport failure after commit must not delete the Cognito account of an already committed user.
    public async Task Ambiguous_commit_does_not_destroy_a_remote_account()
    {
        await SeedAdminAsync();
        var options = new DbContextOptionsBuilder<ExamMakerDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new FailAfterCommitInterceptor()).Options;
        await using (var database = new ExamMakerDbContext(options))
            Assert.Equal(500, (await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(actorId,
                new("committed@example.invalid", "Committed", "admin", null), default))).StatusCode);
        await using var stored = fixture.CreateContext();
        var user = await stored.Users.SingleAsync(x => x.Email == "committed@example.invalid");
        Assert.Contains(user.CognitoSub, cognito.Accounts.Keys);
        Assert.Equal(0, cognito.Deletes);
    }

    [PostgresFact]
    public async Task Failed_cleanup_reports_an_error_and_leaves_an_orphan_for_reconciliation()
    {
        await SeedAdminAsync();
        cognito.FailDelete = true;
        cognito.FailNextMutation = () => throw new InvalidOperationException("Synthetic profile failure.");
        await using var database = fixture.CreateContext();
        Assert.Equal(500, (await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(actorId,
            new("orphan@example.invalid", "Orphan", "admin", null), default))).StatusCode);
        var report = await Service(database).ReconcileAsync(default);
        Assert.Contains(report.Mismatches, x => x.Email == "orphan@example.invalid" && x.Issue == "MissingInDb");
    }

    [PostgresFact]
    public async Task Preexisting_cognito_accounts_are_never_deleted_on_a_create_conflict()
    {
        await SeedAdminAsync();
        cognito.Accounts["existing"] = new("existing", "teacher@example.invalid", "Existing", true, true, ["teacher"]);
        await using var database = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(actorId,
            new("teacher@example.invalid", "Teacher", "teacher", DepartmentId), default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(0, cognito.Deletes);
        Assert.Contains("existing", cognito.Accounts.Keys);
    }

    [PostgresFact]
    public async Task Invalid_actor_or_department_never_provisions_an_aws_account()
    {
        await SeedAdminAsync();
        await using var database = fixture.CreateContext();
        Assert.Equal(403, (await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(Guid.NewGuid(),
            new("teacher@example.invalid", "Teacher", "teacher", DepartmentId), default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<UserOperationException>(() => Service(database).CreateAsync(actorId,
            new("teacher@example.invalid", "Teacher", "teacher", Guid.NewGuid()), default))).StatusCode);
        Assert.Equal(0, cognito.Creates);
    }

    [PostgresFact]
    public async Task Update_syncs_role_email_and_department_then_restores_snapshot_on_failure()
    {
        await SeedAdminAsync();
        var user = await CreateTeacherAsync();
        await using (var database = fixture.CreateContext())
        {
            var changed = await Service(database).UpdateAsync(actorId, user.Id,
                new("head@example.invalid", "Head", "head", Guid.Parse("10000000-0000-0000-0000-000000000002"), true), default);
            Assert.Equal("head", changed.Role);
            Assert.Equal(new[] { "head" }, cognito.Accounts[user.CognitoSub].Groups);
            var audits = await database.AuditLogs.Where(x => x.EntityId == user.Id && x.Action == "UpdateUser").ToListAsync();
            Assert.Equal(2, audits.Count);
            Assert.Contains(audits, x => x.DepartmentId == DepartmentId);
            Assert.Contains(audits, x => x.DepartmentId == Guid.Parse("10000000-0000-0000-0000-000000000002"));
            Assert.All(audits, audit =>
            {
                Assert.Contains("role teacher->head", audit.Note);
                Assert.Contains($"department {DepartmentId}->10000000-0000-0000-0000-000000000002", audit.Note);
                Assert.DoesNotContain("@", audit.Note);
            });
        }
        cognito.FailNextMutation = () => throw new InvalidOperationException("Synthetic AWS failure after a partial change.");
        await using (var database = fixture.CreateContext())
            await Assert.ThrowsAsync<UserOperationException>(() => Service(database).UpdateAsync(actorId, user.Id,
                new("admin2@example.invalid", "Changed", "admin", null, true), default));
        await using (var database = fixture.CreateContext())
        {
            var stored = await database.Users.SingleAsync(x => x.Id == user.Id);
            Assert.Equal("head", stored.Role);
            Assert.Equal("head@example.invalid", stored.Email);
            Assert.True(stored.IsActive);
        }
        Assert.Equal("head@example.invalid", cognito.Accounts[user.CognitoSub].Email);
        Assert.Equal(new[] { "head" }, cognito.Accounts[user.CognitoSub].Groups);
    }

    [PostgresFact]
    public async Task Disable_failure_keeps_local_access_denied_and_retry_revokes_sessions()
    {
        await SeedAdminAsync();
        var user = await CreateTeacherAsync();
        cognito.FailSignOut = true;
        await using (var database = fixture.CreateContext())
            await Assert.ThrowsAsync<UserOperationException>(() => Service(database).DeactivateAsync(actorId, user.Id, default));
        await using (var database = fixture.CreateContext()) Assert.False((await database.Users.SingleAsync(x => x.Id == user.Id)).IsActive);
        Assert.False(cognito.Accounts[user.CognitoSub].Enabled);
        cognito.FailSignOut = false;
        await using (var database = fixture.CreateContext()) await Service(database).DeactivateAsync(actorId, user.Id, default);
        Assert.Equal(2, cognito.SignOuts);
        await using (var database = fixture.CreateContext())
            Assert.True((await Service(database).UpdateAsync(actorId, user.Id,
                new(user.Email, user.FullName, user.Role, user.DepartmentId, true), default)).IsActive);
        Assert.True(cognito.Accounts[user.CognitoSub].Enabled);
    }

    [PostgresFact]
    public async Task Failed_profile_update_with_disable_never_restores_local_access()
    {
        await SeedAdminAsync();
        var user = await CreateTeacherAsync();
        cognito.FailNextMutation = () => throw new InvalidOperationException("Synthetic failure.");
        await using (var database = fixture.CreateContext())
            await Assert.ThrowsAsync<UserOperationException>(() => Service(database).UpdateAsync(actorId, user.Id,
                new(user.Email, "Changed", "head", DepartmentId, false), default));
        await using (var database = fixture.CreateContext()) Assert.False((await database.Users.SingleAsync(x => x.Id == user.Id)).IsActive);
        Assert.False(cognito.Accounts[user.CognitoSub].Enabled);
        Assert.Equal(new[] { "teacher" }, cognito.Accounts[user.CognitoSub].Groups);
    }

    [PostgresFact]
    public async Task Self_lockout_is_rejected_and_bootstrap_is_single_use()
    {
        await using (var database = fixture.CreateContext())
        {
            var admin = await new AdminBootstrapper(Service(database)).BootstrapAsync("admin@example.invalid", "Admin", default);
            Assert.Equal("admin", admin.Role);
            Assert.Null(admin.DepartmentId);
            Assert.Equal(409, (await Assert.ThrowsAsync<UserOperationException>(() => Service(database).DeactivateAsync(admin.Id, admin.Id, default))).StatusCode);
        }
        await using (var database = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<UserOperationException>(() => new AdminBootstrapper(Service(database)).BootstrapAsync("other@example.invalid", "Other", default))).StatusCode);
        Assert.Equal(1, cognito.Creates);
    }

    [PostgresFact]
    // PostgreSQL locking prevents concurrent requests from creating two remote users for the same email.
    public async Task Concurrent_duplicate_provisioning_only_creates_one_remote_account()
    {
        await SeedAdminAsync();
        async Task<int> Attempt()
        {
            await using var database = fixture.CreateContext();
            try { await Service(database).CreateAsync(actorId, new("same@example.invalid", "Same", "admin", null), default); return 201; }
            catch (UserOperationException exception) { return exception.StatusCode; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Equal(new[] { 201, 409 }, results.Order());
        Assert.Equal(1, cognito.Creates);
    }

    [PostgresFact]
    public async Task Reconcile_reports_all_contract_mismatch_types_and_does_not_mutate_accounts()
    {
        await SeedAdminAsync();
        var user = await CreateTeacherAsync();
        cognito.Accounts[user.CognitoSub] = cognito.Accounts[user.CognitoSub] with { Groups = ["admin"], Enabled = false };
        cognito.Accounts["orphan"] = new("orphan", "orphan@example.invalid", "Orphan", true, true, ["teacher"]);
        cognito.Accounts.Remove("admin-sub");
        var before = cognito.Accounts.ToDictionary(x => x.Key, x => x.Value);
        await using var database = fixture.CreateContext();
        var report = await Service(database).ReconcileAsync(default);
        Assert.Equal(2, report.TotalUsers);
        Assert.Contains(report.Mismatches, x => x.Issue == "RoleMismatch" && x.UserId == user.Id);
        Assert.Contains(report.Mismatches, x => x.Issue == "DisabledMismatch" && x.UserId == user.Id);
        Assert.Contains(report.Mismatches, x => x.Issue == "MissingInCognito" && x.UserId == actorId);
        Assert.Contains(report.Mismatches, x => x.Issue == "MissingInDb" && x.UserId is null);
        Assert.Equal(before, cognito.Accounts);
        Assert.True((await database.Users.SingleAsync(x => x.Id == user.Id)).IsActive);
    }
}

public sealed class FakeCognitoUserAdmin : ICognitoUserAdmin
{
    public Dictionary<string, CognitoAccount> Accounts { get; } = [];
    public int Creates { get; private set; }
    public int Deletes { get; private set; }
    public int RoleAssignments { get; private set; }
    public bool StallRoleAssignment { get; set; }
    public int SignOuts { get; private set; }
    public Action? FailNextMutation { get; set; }
    public bool FailSignOut { get; set; }
    public bool FailDelete { get; set; }
    public bool CleanupWasCancelled { get; private set; }
    public Task<CognitoAccount> CreateAsync(string email, string fullName, CancellationToken cancellationToken)
    {
        if (Accounts.Values.Any(x => x.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            throw new UserOperationException(409, "Conflict", "Email đã tồn tại.");
        Creates++;
        var account = new CognitoAccount(Guid.NewGuid().ToString(), email, fullName, true, true, []);
        Accounts.Add(account.Subject, account);
        return Task.FromResult(account);
    }
    public Task<CognitoAccount?> FindAsync(string subject, CancellationToken cancellationToken) => Task.FromResult(Accounts.GetValueOrDefault(subject));
    public Task<IReadOnlyList<CognitoAccount>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CognitoAccount>>(Accounts.Values.ToArray());
    public async Task AssignRoleAsync(string subject, string role, CancellationToken cancellationToken)
    {
        RoleAssignments++;
        if (StallRoleAssignment) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Accounts[subject] = Accounts[subject] with { Groups = [role] };
        var fault = FailNextMutation;
        FailNextMutation = null;
        fault?.Invoke();
    }
    public Task SetProfileAsync(CognitoAccount profile, CancellationToken cancellationToken)
    {
        Accounts[profile.Subject] = profile with { Enabled = Accounts[profile.Subject].Enabled };
        var fault = FailNextMutation;
        FailNextMutation = null;
        fault?.Invoke();
        return Task.CompletedTask;
    }
    public Task SetEnabledAsync(string subject, bool enabled, CancellationToken cancellationToken)
    { Accounts[subject] = Accounts[subject] with { Enabled = enabled }; return Task.CompletedTask; }
    public Task GlobalSignOutAsync(string subject, CancellationToken cancellationToken)
    { SignOuts++; if (FailSignOut) throw new InvalidOperationException("Synthetic sign-out failure."); return Task.CompletedTask; }
    public Task DeleteAsync(string subject, CancellationToken cancellationToken)
    {
        CleanupWasCancelled = cancellationToken.IsCancellationRequested;
        if (FailDelete) throw new InvalidOperationException("Synthetic cleanup failure.");
        Deletes++; Accounts.Remove(subject); return Task.CompletedTask;
    }
}

public sealed class FailAfterCommitInterceptor : DbTransactionInterceptor
{
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default) => throw new IOException("Synthetic lost commit acknowledgement.");
}
