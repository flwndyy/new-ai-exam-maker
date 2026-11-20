using ExamMaker.Application.Authentication;
using ExamMaker.Application.Users;
using ExamMaker.Domain.Users;
using ExamMaker.Domain.Auditing;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ExamMaker.Infrastructure.Users;

public sealed class UserManagementService(ExamMakerDbContext database, ICognitoUserAdmin cognito,
    ILogger<UserManagementService> logger) : IUserManagement
{
    /// <summary>Filter and paginate local profiles with deterministic name and identifier ordering.</summary>
    public async Task<AdminUserPage> ListAsync(UserListQuery query, CancellationToken cancellationToken)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || query.Q?.Length > 100
            || query.Role is not (null or Roles.Teacher or Roles.Head or Roles.Admin)
            || (long)(query.Page - 1) * query.PageSize > int.MaxValue)
            throw Invalid();
        var users = database.Users.AsNoTracking();
        if (query.Role is not null) users = users.Where(x => x.Role == query.Role);
        if (query.DepartmentId is Guid department) users = users.Where(x => x.DepartmentId == department);
        if (query.IsActive is bool active) users = users.Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim().ToLowerInvariant();
            users = users.Where(x => x.Email.ToLower().Contains(term) || x.FullName.ToLower().Contains(term));
        }
        var total = await users.CountAsync(cancellationToken);
        var items = await users.OrderBy(x => x.FullName).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new AdminUser(x.Id, x.CognitoSub, x.Email, x.FullName, x.Role, x.DepartmentId, x.IsActive, x.CreatedAt))
            .ToListAsync(cancellationToken);
        return new(query.Page, query.PageSize, total, (int)Math.Ceiling(total / (double)query.PageSize), items);
    }

    /// <summary>Return one local profile without contacting Cognito.</summary>
    public async Task<AdminUser> GetAsync(Guid id, CancellationToken cancellationToken) =>
        View(await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw Missing());

    /// <summary>Create a user with an invitation, managed group and durable local profile.</summary>
    public async Task<AdminUser> CreateAsync(Guid actorId, CreateUserInput input, CancellationToken cancellationToken)
    {
        using var budget = UserManagementDeadlines.Start(cancellationToken);
        return await CreateCoreAsync(actorId, input, false, budget.Token);
    }

    /// <summary>Provision the first administrator through a private CLI, under the same mutation lock.</summary>
    internal async Task<AdminUser> ProvisionFirstAdminAsync(string email, string fullName, CancellationToken cancellationToken)
    {
        using var budget = UserManagementDeadlines.Start(cancellationToken);
        return await CreateCoreAsync(null, new(email, fullName, Roles.Admin, null), true, budget.Token);
    }

    /// <summary>Share the provisioning transaction between authenticated administration and private bootstrap.</summary>
    private async Task<AdminUser> CreateCoreAsync(Guid? actorId, CreateUserInput input, bool bootstrap, CancellationToken cancellationToken)
    {
        input = UserInputRules.Normalize(input);
        await using var transaction = await LockAsync(cancellationToken);
        if (bootstrap)
        {
            if (await database.Users.AnyAsync(x => x.Role == Roles.Admin, cancellationToken))
                throw new UserOperationException(409, "Conflict", "Admin đã tồn tại; không chạy lại bootstrap.");
        }
        else await RequireAdminAsync(actorId!.Value, cancellationToken);
        await ValidateCatalogAndEmailAsync(input, null, cancellationToken);
        CognitoAccount? created = null;
        var commitStarted = false;
        try
        {
            created = await cognito.CreateAsync(input.Email, input.FullName, cancellationToken);
            await cognito.AssignRoleAsync(created.Subject, input.Role, cancellationToken);
            var user = new User
            {
                Id = Guid.NewGuid(),
                CognitoSub = created.Subject,
                Email = input.Email,
                FullName = input.FullName,
                Role = input.Role,
                DepartmentId = input.DepartmentId,
                IsActive = true
            };
            database.Users.Add(user);
            Audit(actorId, user.Id, "CreateUser", null, "Active", bootstrap ? "Bootstrap administrator CLI." : "Administrator provisioned an invited account.");
            await database.SaveChangesAsync(cancellationToken);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken);
            return View(user);
        }
        catch (Exception exception)
        {
            // A commit transport failure is ambiguous: deleting the remote account could break a committed row.
            var cleanupFailed = false;
            if (created is not null && !commitStarted)
                cleanupFailed = !await CompensateAsync(token => cognito.DeleteAsync(created.Subject, token));
            database.ChangeTracker.Clear();
            throw Translate(exception, commitStarted || cleanupFailed);
        }
    }

    /// <summary>Update local and remote profiles, keeping disable requests fail-closed on partial failure.</summary>
    public async Task<AdminUser> UpdateAsync(Guid actorId, Guid id, UpdateUserInput input, CancellationToken cancellationToken)
    {
        using var budget = UserManagementDeadlines.Start(cancellationToken);
        cancellationToken = budget.Token;
        if (input.IsActive is null) throw Invalid();
        var normalized = UserInputRules.Normalize(new(input.Email, input.FullName, input.Role, input.DepartmentId));
        UserInputRules.EnsureNotSelfLockout(actorId, id, normalized.Role, input.IsActive.Value);
        if (!input.IsActive.Value)
            await PersistDisabledAsync(actorId, id, normalized, cancellationToken);
        return await UpdateCoreAsync(actorId, id, normalized, input.IsActive.Value, cancellationToken);
    }

    /// <summary>Soft-delete by denying local access first, then disabling Cognito and revoking sessions.</summary>
    public async Task DeactivateAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    {
        using var budget = UserManagementDeadlines.Start(cancellationToken);
        cancellationToken = budget.Token;
        UserInputRules.EnsureNotSelfLockout(actorId, id, Roles.Admin, false);
        await PersistDisabledAsync(actorId, id, null, cancellationToken);
        await using var transaction = await LockAsync(cancellationToken);
        await RequireAdminAsync(actorId, cancellationToken);
        var user = await database.Users.SingleAsync(x => x.Id == id, cancellationToken);
        if (user.IsActive) throw new UserOperationException(409, "Conflict", "Tài khoản vừa được mở khóa bởi thao tác khác; hãy tải lại trước khi thử lại.");
        try
        {
            await cognito.SetEnabledAsync(user.CognitoSub, false, cancellationToken);
            await cognito.GlobalSignOutAsync(user.CognitoSub, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) { throw Translate(exception, false); }
    }

    /// <summary>Persist access denial independently so later AWS failures cannot undo a requested disable.</summary>
    private async Task PersistDisabledAsync(Guid actorId, Guid id, CreateUserInput? input, CancellationToken cancellationToken)
    {
        await using var transaction = await LockAsync(cancellationToken);
        await RequireAdminAsync(actorId, cancellationToken);
        var user = await database.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw Missing();
        if (input is not null) await ValidateCatalogAndEmailAsync(input, id, cancellationToken);
        if (user.IsActive) Audit(actorId, user.Id, "DisableUser", "Active", "Inactive", "Local access denied before Cognito session revocation.");
        user.IsActive = false;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    /// <summary>Hold the write lock while synchronizing a verified remote account and committing its local profile.</summary>
    private async Task<AdminUser> UpdateCoreAsync(Guid actorId, Guid id, CreateUserInput input, bool active, CancellationToken cancellationToken)
    {
        await using var transaction = await LockAsync(cancellationToken);
        await RequireAdminAsync(actorId, cancellationToken);
        var user = await database.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw Missing();
        await ValidateCatalogAndEmailAsync(input, id, cancellationToken);
        if (!active && user.IsActive)
            throw new UserOperationException(409, "Conflict", "Tài khoản vừa được mở khóa bởi thao tác khác; hãy tải lại trước khi thử lại.");
        CognitoAccount? previous = null;
        var commitStarted = false;
        try
        {
            previous = await cognito.FindAsync(user.CognitoSub, cancellationToken)
                ?? throw new InvalidOperationException("The remote account is missing; reconciliation is required.");
            if (previous.Subject != user.CognitoSub) throw new InvalidOperationException("Cognito subject mismatch.");
            await cognito.SetProfileAsync(previous with
            { Email = input.Email, FullName = input.FullName, EmailVerified = true, Groups = [input.Role] }, cancellationToken);
            await cognito.SetEnabledAsync(user.CognitoSub, active, cancellationToken);
            if (!active) await cognito.GlobalSignOutAsync(user.CognitoSub, cancellationToken);
            var permissionChanges = new List<string>();
            if (user.Role != input.Role) permissionChanges.Add($"role {user.Role}->{input.Role}");
            if (user.DepartmentId != input.DepartmentId)
                permissionChanges.Add($"department {user.DepartmentId?.ToString() ?? "none"}->{input.DepartmentId?.ToString() ?? "none"}");
            var auditNote = permissionChanges.Count == 0 ? "User profile and managed role synchronized." : string.Join("; ", permissionChanges);
            Audit(actorId, user.Id, "UpdateUser", user.IsActive ? "Active" : "Inactive", active ? "Active" : "Inactive", auditNote);
            user.Email = input.Email;
            user.FullName = input.FullName;
            user.Role = input.Role;
            user.DepartmentId = input.DepartmentId;
            user.IsActive = active;
            await database.SaveChangesAsync(cancellationToken);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken);
            return View(user);
        }
        catch (Exception exception)
        {
            var cleanupFailed = false;
            if (previous is not null && !commitStarted)
            {
                cleanupFailed = !await CompensateAsync(async token =>
                {
                    await cognito.SetProfileAsync(previous, token);
                    // Never undo an explicit disable request, even when restoring its profile.
                    await cognito.SetEnabledAsync(previous.Subject, active && previous.Enabled, token);
                });
            }
            database.ChangeTracker.Clear();
            throw Translate(exception, commitStarted || cleanupFailed);
        }
    }

    /// <summary>Report drift in both directions without changing either store.</summary>
    public async Task<ReconcileReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        using var budget = UserManagementDeadlines.Start(cancellationToken);
        cancellationToken = budget.Token;
        var local = await database.Users.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        IReadOnlyList<CognitoAccount> remote;
        try { remote = await cognito.ListAsync(cancellationToken); }
        catch (Exception exception) { throw Translate(exception, false); }
        var bySubject = remote.ToDictionary(x => x.Subject);
        var localSubjects = local.Select(x => x.CognitoSub).ToHashSet();
        var mismatches = new List<UserMismatch>();
        foreach (var user in local)
        {
            if (!bySubject.TryGetValue(user.CognitoSub, out var account))
                mismatches.Add(new(user.Id, user.Email, user.Role, [], "MissingInCognito"));
            else
            {
                var roles = account.Groups.Where(x => x is Roles.Teacher or Roles.Head or Roles.Admin).ToArray();
                if (roles.Length != 1 || roles[0] != user.Role)
                    mismatches.Add(new(user.Id, user.Email, user.Role, account.Groups, "RoleMismatch"));
                if (account.Enabled != user.IsActive)
                    mismatches.Add(new(user.Id, user.Email, user.Role, account.Groups, "DisabledMismatch"));
            }
        }
        foreach (var account in remote.Where(x => !localSubjects.Contains(x.Subject)))
            mismatches.Add(new(null, account.Email, null, account.Groups, "MissingInDb"));
        return new(DateTimeOffset.UtcNow, local.Count, mismatches);
    }

    /// <summary>Serialize management writes across processes, including bootstrap and email checks.</summary>
    private async Task<IDbContextTransaction> LockAsync(CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48683401)", cancellationToken);
            return transaction;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await transaction.DisposeAsync();
            UserFailureDiagnostics.Log(logger, exception, "Lock", false);
            throw new UserOperationException(409, "Conflict", "Thao tác quản lý user đang bận. Hãy tải lại và thử sau.");
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    /// <summary>Recheck database authorization after acquiring the mutation lock to reject stale administrator access.</summary>
    private async Task RequireAdminAsync(Guid actorId, CancellationToken cancellationToken)
    {
        if (!await database.Users.AsNoTracking().AnyAsync(x => x.Id == actorId && x.IsActive && x.Role == Roles.Admin, cancellationToken))
            throw new UserOperationException(403, "Forbidden", "Tài khoản không còn quyền quản trị.");
    }

    /// <summary>Check the department and normalized email uniqueness before remote side effects.</summary>
    private async Task ValidateCatalogAndEmailAsync(CreateUserInput input, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (input.DepartmentId is Guid department
            && !await database.Departments.AnyAsync(x => x.Id == department, cancellationToken)) throw Invalid();
        if (await database.Users.AnyAsync(x => x.Id != exceptId && x.Email.Trim().ToLower() == input.Email, cancellationToken))
            throw new UserOperationException(409, "Conflict", "Email đã tồn tại.");
    }

    /// <summary>Attempt bounded cleanup even if the HTTP request was cancelled.</summary>
    private async Task<bool> CompensateAsync(Func<CancellationToken, Task> cleanup)
    {
        using var timeout = new CancellationTokenSource(UserManagementDeadlines.Compensation);
        try { await cleanup(timeout.Token); return true; }
        catch (Exception exception)
        {
            UserFailureDiagnostics.Log(logger, exception, "Compensation", true);
            return false;
        }
    }

    /// <summary>Translate expected conflicts and synchronization failures into safe API errors.</summary>
    private UserOperationException Translate(Exception exception, bool ambiguousCommit)
    {
        if (!ambiguousCommit && exception is UserOperationException known) return known;
        if (!ambiguousCommit && exception is DbUpdateException { InnerException: PostgresException { SqlState: "23505" } })
            return new(409, "Conflict", "Email hoặc tài khoản đã tồn tại.");
        UserFailureDiagnostics.Log(logger, exception, "Synchronization", ambiguousCommit);
        var diagnostic = UserFailureDiagnostics.Describe(exception);
        return new(500, "InternalError", "Không thể đồng bộ tài khoản. Kiểm tra đối soát trước khi thử lại; tài khoản bị khóa vẫn giữ khóa trong DB.", diagnostic.ExceptionType, diagnostic.ServiceCode);
    }

    private static AdminUser View(User user) => new(user.Id, user.CognitoSub, user.Email, user.FullName,
        user.Role, user.DepartmentId, user.IsActive, user.CreatedAt);

    /// <summary>Persist user-management audit events in the same database transaction as the local change.</summary>
    private void Audit(Guid? actorId, Guid id, string action, string? from, string to, string note) =>
        database.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            EntityType = "User",
            EntityId = id,
            Action = action,
            FromStatus = from,
            ToStatus = to,
            Note = note
        });
    private static UserOperationException Invalid() => new(400, "ValidationFailed", "Thông tin người dùng không hợp lệ.");
    private static UserOperationException Missing() => new(404, "NotFound", "Không tìm thấy người dùng.");
}
