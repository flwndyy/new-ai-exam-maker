using System.Text.Json;
using System.Linq.Expressions;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Exams;
using ExamMaker.Application.Questions;
using ExamMaker.Domain.Auditing;
using ExamMaker.Domain.Exams;
using ExamMaker.Domain.Questions;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ExamMaker.Infrastructure.Exams;

public sealed class ExamManagement(ExamMakerDbContext database, ILogger<ExamManagement> logger) : IExamManagement
{
    /// <summary>List visible exam identities without leaking private versions to department peers.</summary>
    public async Task<ExamPage> ListAsync(Guid actorId, ExamQuery query, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, false, cancellationToken);
        ValidatePage(query.Page, query.PageSize);
        if (query.SubjectId == Guid.Empty || query.Grade is not (null or 10 or 11 or 12)) throw ExamRules.Invalid("query");
        var source = VisibleExams(actor).Where(e => (!query.Mine || e.CreatedBy == actor.Id)
            && (query.SubjectId == null || e.SubjectId == query.SubjectId) && (query.Grade == null || e.Grade == query.Grade)
            && (query.HasPublished == null || e.Versions.Any(v => v.Status == "Published") == query.HasPublished));
        var count = await source.CountAsync(cancellationToken);
        var exams = await source.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Include(x => x.Creator).Include(x => x.Versions).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        return new(query.Page, query.PageSize, count, Pages(count, query.PageSize), exams.Select(e => ExamView(e, actor)).ToArray());
    }

    /// <summary>Return only version summaries the caller can see, independently of answer access.</summary>
    public async Task<ExamDetail> GetAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, false, cancellationToken);
        return await DetailAsync(actor, id, cancellationToken);
    }

    /// <summary>Create an authored identity and its first empty draft together with their audit history.</summary>
    public Task<ExamDetail> CreateAsync(Guid actorId, ExamCreateInput input, CancellationToken cancellationToken) =>
        MutateAsync(actorId, async actor =>
        {
            if (actor.Role is not (Roles.Teacher or Roles.Head)) throw ExamRules.Forbidden();
            var title = ExamRules.Title(input.Title);
            if (input.SubjectId is null || input.SubjectId == Guid.Empty) throw ExamRules.Invalid("subjectId");
            if (input.Grade is not (10 or 11 or 12)) throw ExamRules.Invalid("grade");
            var subject = await database.Subjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.SubjectId, cancellationToken);
            if (subject is null || subject.DepartmentId != actor.DepartmentId) throw ExamRules.Invalid("subjectId");
            var now = DateTime.UtcNow;
            var exam = new Exam
            {
                Id = Guid.NewGuid(),
                Title = title,
                SubjectId = subject.Id,
                Grade = input.Grade.Value,
                DepartmentId = subject.DepartmentId,
                CreatedBy = actor.Id,
                CreatedAt = now,
                UpdatedAt = now
            };
            var version = NewVersion(exam.Id, 1, 45, now);
            database.Exams.Add(exam); database.ExamVersions.Add(version);
            Audit(actor.Id, "Exam", exam.Id, exam.DepartmentId, "CreateExam", null, null);
            Audit(actor.Id, "ExamVersion", version.Id, exam.DepartmentId, "CreateExamVersion", null, "Draft");
            await database.SaveChangesAsync(cancellationToken);
            return await DetailAsync(actor, exam.Id, cancellationToken);
        }, cancellationToken);

    /// <summary>Change only the owner's title; classification stays fixed across all versions.</summary>
    public Task<ExamDetail> UpdateAsync(Guid actorId, Guid id, ExamUpdateInput input, CancellationToken cancellationToken) =>
        MutateAsync(actorId, async actor =>
        {
            var exam = await LockExamAsync(actor, id, cancellationToken); ExamRules.RequireOwner(actor, exam);
            var title = ExamRules.Title(input.Title);
            if (exam.Title != title)
            {
                exam.Title = title; Touch(exam);
                Audit(actor.Id, "Exam", id, exam.DepartmentId, "UpdateExam", null, null);
                await database.SaveChangesAsync(cancellationToken);
            }
            return await DetailAsync(actor, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Page visible version metadata while excluding unpublished private versions from peer responses.</summary>
    public async Task<ExamVersionPage> VersionsAsync(Guid actorId, Guid id, int page, int pageSize, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, false, cancellationToken); ValidatePage(page, pageSize);
        if (!await VisibleExams(actor).AnyAsync(x => x.Id == id, cancellationToken)) throw ExamRules.Missing();
        return await PageVersionsAsync(VisibleVersions(actor).Where(v => v.ExamId == id).OrderByDescending(v => v.VersionNo), page, pageSize, cancellationToken);
    }

    /// <summary>Return current editable source content or frozen snapshots, after checking answer access.</summary>
    public async Task<ExamVersionView> GetVersionAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, false, cancellationToken);
        return await VersionViewAsync(actor, id, [], cancellationToken);
    }

    /// <summary>Return the student sheet built only from frozen snapshot columns with whitelisted payload fields.</summary>
    public async Task<StudentSheet> StudentSheetAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    {
        var (header, items) = await LoadExportAsync(actorId, id, false, cancellationToken);
        return ExamExport.BuildStudentSheet(header, items);
    }

    /// <summary>Return the teacher answer key from frozen snapshot columns after enforcing answer access.</summary>
    public async Task<AnswerKey> AnswerKeyAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    {
        var (header, items) = await LoadExportAsync(actorId, id, true, cancellationToken);
        return ExamExport.BuildAnswerKey(header, items);
    }

    /// <summary>Read a version with its snapshot rows only; live source questions are never loaded for exports.</summary>
    private async Task<(SheetHeader Header, IReadOnlyList<ExportSnapshotItem> Items)> LoadExportAsync(Guid actorId, Guid id, bool answerKey, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, false, cancellationToken);
        var v = await database.ExamVersions.AsNoTracking()
            .Include(x => x.Exam).ThenInclude(e => e!.Subject)
            .Include(x => x.Exam).ThenInclude(e => e!.Department)
            .Include(x => x.Questions)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw ExamRules.Missing();
        var exam = v.Exam!;
        ExamExport.Authorize(actor, exam, v.Status, answerKey);
        var header = new SheetHeader(exam.Title, exam.Subject!.Name, exam.Department?.Name, exam.Grade, v.VersionNo, v.DurationMinutes, v.TotalPoints);
        var items = v.Questions.Select(i => new ExportSnapshotItem(i.SortOrder, i.Points, i.Type, i.Difficulty, i.StemSnapshot,
            i.PayloadSnapshot, i.ExplanationSnapshot)).ToArray();
        return (header, items);
    }

    /// <summary>Allocate the next version number under the exam lock and copy currently selectable source links.</summary>
    public Task<ExamVersionView> CreateVersionAsync(Guid actorId, Guid examId, ExamVersionCreateInput input, CancellationToken cancellationToken) =>
        MutateAsync(actorId, async actor =>
        {
            var exam = await LockExamAsync(actor, examId, cancellationToken); ExamRules.RequireOwner(actor, exam);
            if (input.FromVersionId == Guid.Empty) throw ExamRules.Invalid("fromVersionId");
            var versions = database.ExamVersions.AsNoTracking().Where(v => v.ExamId == examId);
            if (await versions.AnyAsync(v => v.Status == "Draft", cancellationToken)) throw Conflict("Đề đang có phiên bản Draft.");
            var source = await versions.Where(v => input.FromVersionId == null || v.Id == input.FromVersionId)
                .Include(v => v.Questions).OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(cancellationToken) ?? throw ExamRules.Missing();
            var latest = await versions.MaxAsync(v => v.VersionNo, cancellationToken);
            if (latest == int.MaxValue) throw Conflict("Không thể tăng số phiên bản.");
            var sources = await LockSourcesAsync(source.Questions.Select(i => i.SourceQuestionId), cancellationToken);
            var version = NewVersion(examId, latest + 1, source.DurationMinutes, DateTime.UtcNow);
            var warnings = new List<ExamWarning>();
            foreach (var item in source.Questions.OrderBy(i => i.SortOrder))
            {
                if (!sources.TryGetValue(item.SourceQuestionId, out var question) || !ExamRules.CanSelect(actor, exam, question))
                { warnings.Add(new("SourceOmitted", "Câu nguồn không còn phù hợp và đã được bỏ khỏi bản mới.", item.SourceQuestionId)); continue; }
                version.Questions.Add(Link(version.Id, version.Questions.Count + 1, item.Points, question));
            }
            version.TotalPoints = Sum(version.Questions); database.ExamVersions.Add(version); Touch(exam);
            Audit(actor.Id, "ExamVersion", version.Id, exam.DepartmentId, "CreateExamVersion", null, "Draft", $"fromVersion={source.Id}; omitted={warnings.Count}");
            await database.SaveChangesAsync(cancellationToken);
            return await VersionViewAsync(actor, version.Id, warnings, cancellationToken);
        }, cancellationToken);

    /// <summary>Replace a whole editable composition under source share locks and the parent version lock.</summary>
    public Task<ExamVersionView> UpdateVersionAsync(Guid actorId, Guid id, ExamVersionInput input, CancellationToken cancellationToken) =>
        MutateAsync(actorId, async actor =>
        {
            var version = await LockVersionAsync(actor, id, cancellationToken); var exam = version.Exam!;
            ExamRules.RequireOwner(actor, exam); ExamRules.RequireEditable(version);
            var total = ExamRules.ValidateComposition(input);
            var sources = await LockSourcesAsync(input.Items!.Select(i => i.SourceQuestionId), cancellationToken);
            if (input.Items!.Any(i => !sources.TryGetValue(i.SourceQuestionId, out var q) || !ExamRules.CanSelect(actor, exam, q))) throw ExamRules.Invalid("items");
            // ExecuteDelete is intentional: positional primary keys are replaced rather than reordered in place.
            await database.ExamVersionQuestions.Where(i => i.ExamVersionId == id).ExecuteDeleteAsync(cancellationToken);
            foreach (var (item, index) in input.Items!.Select((item, index) => (item, index)))
                database.ExamVersionQuestions.Add(Link(id, index + 1, item.Points, sources[item.SourceQuestionId]));
            version.DurationMinutes = input.DurationMinutes; version.TotalPoints = total; version.ContentHash = null;
            version.UpdatedAt = ExamRules.NextTimestamp(version.UpdatedAt, DateTime.UtcNow); Touch(exam);
            Audit(actor.Id, "ExamVersion", id, exam.DepartmentId, "UpdateExamVersion", version.Status, version.Status);
            await database.SaveChangesAsync(cancellationToken);
            return await VersionViewAsync(actor, id, [], cancellationToken);
        }, cancellationToken);

    /// <summary>Snapshot, review and publish under one transaction so failed decisions never leave successful audit rows.</summary>
    public Task<ExamVersionView> TransitionAsync(Guid actorId, Guid id, ExamAction action, ExamReviewInput? input, CancellationToken cancellationToken) =>
        MutateAsync(actorId, async actor =>
        {
            var version = await LockVersionAsync(actor, id, cancellationToken); var exam = version.Exam!;
            ExamRules.RequireAction(actor, exam, version, action); var before = version.Status;
            if (action == ExamAction.Publish && await database.ExamVersions.AnyAsync(v => v.ExamId == exam.Id && v.Status == "Published" && v.Id != id, cancellationToken))
                throw new ExamException(409, "PublishedVersionExists", "Đề đã có phiên bản Published. Hãy thu hồi bản cũ trước.");
            if (action == ExamAction.Submit)
            {
                await SnapshotAsync(actor, exam, version, cancellationToken);
                // Snapshot items must be saved while their parent is still editable; the database trigger freezes them afterwards.
                await database.SaveChangesAsync(cancellationToken);
            }
            var hash = version.ContentHash;
            ExamRules.Transition(actor, exam, version, action, input, DateTime.UtcNow);
            if (action == ExamAction.Withdraw)
            {
                // The frozen metadata trigger also applies to withdrawal: open Draft first, then clear the hash and snapshots.
                version.ContentHash = hash; await database.SaveChangesAsync(cancellationToken); version.ContentHash = null;
                await database.ExamVersionQuestions.Where(i => i.ExamVersionId == id).ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Type, (string?)null).SetProperty(i => i.Difficulty, (string?)null)
                    .SetProperty(i => i.StemSnapshot, (string?)null).SetProperty(i => i.PayloadSnapshot, (string?)null)
                    .SetProperty(i => i.ExplanationSnapshot, (string?)null), cancellationToken);
            }
            Touch(exam);
            Audit(actor.Id, "ExamVersion", id, exam.DepartmentId, action + "ExamVersion", before, version.Status,
                action is ExamAction.Approve or ExamAction.Reject ? version.ReviewNote : null);
            await database.SaveChangesAsync(cancellationToken);
            return await VersionViewAsync(actor, id, [], cancellationToken);
        }, cancellationToken);

    /// <summary>Show submitted versions to eligible non-author reviewers in submission order.</summary>
    public async Task<ExamVersionPage> ReviewQueueAsync(Guid actorId, ExamReviewQuery query, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, false, cancellationToken);
        if (actor.Role is not (Roles.Head or Roles.Admin)) throw ExamRules.Forbidden();
        ValidatePage(query.Page, query.PageSize); if (query.SubjectId == Guid.Empty) throw ExamRules.Invalid("subjectId");
        return await PageVersionsAsync(VisibleVersions(actor).Where(v => v.Status == "Submitted" && v.Exam!.CreatedBy != actor.Id
            && (query.SubjectId == null || v.Exam.SubjectId == query.SubjectId)).OrderBy(v => v.SubmittedAt).ThenBy(v => v.Id),
            query.Page, query.PageSize, cancellationToken);
    }

    /// <summary>Protect the live actor and catalog, then commit the use case result and all audit rows atomically.</summary>
    private async Task<T> MutateAsync<T>(Guid actorId, Func<AuthenticatedUser, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
            var actor = await ActorAsync(actorId, true, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(48683402)", cancellationToken);
            var result = await operation(actor); await transaction.CommitAsync(cancellationToken); return result;
        }
        catch (Exception exception) when (exception is PostgresException || exception is DbUpdateException { InnerException: PostgresException })
        {
            var postgres = (exception as PostgresException ?? exception.InnerException as PostgresException)!;
            logger.LogWarning("Exam mutation failed: {Type}; SQLSTATE {Code}; stack {Stack}", exception.GetType().Name, postgres.SqlState, exception.StackTrace);
            if (postgres.ConstraintName == "ux_exam_versions_one_published") throw new ExamException(409, "PublishedVersionExists", "Đề đã có phiên bản Published.");
            if (postgres.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation) throw Conflict("Dữ liệu đang được sử dụng hoặc thay đổi. Hãy đọc lại trước khi thử lại.");
            throw new ExamException(500, "InternalError", "Không thể lưu đề. Hãy kiểm tra dữ liệu trước khi thử lại.");
        }
    }

    /// <summary>Check visibility before waiting for a row, then lock the identity that serializes version allocation and publication.</summary>
    private async Task<Exam> LockExamAsync(AuthenticatedUser actor, Guid id, CancellationToken cancellationToken)
    {
        if (!await VisibleExams(actor).AnyAsync(e => e.Id == id, cancellationToken)) throw ExamRules.Missing();
        var exam = await database.Exams.FromSqlInterpolated($"SELECT * FROM exams WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw ExamRules.Missing();
        await database.Entry(exam).ReloadAsync(cancellationToken);
        if (!await VisibleExams(actor).AnyAsync(e => e.Id == id, cancellationToken)) throw ExamRules.Missing();
        return exam;
    }

    /// <summary>Acquire exam then version in a consistent order and recheck visibility against the locked state.</summary>
    private async Task<ExamVersion> LockVersionAsync(AuthenticatedUser actor, Guid id, CancellationToken cancellationToken)
    {
        var visible = await database.ExamVersions.AsNoTracking().Include(v => v.Exam).SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (visible is null || !ExamRules.CanRead(actor, visible.Exam!, visible.Status)) throw ExamRules.Missing();
        var exam = await LockExamAsync(actor, visible.ExamId, cancellationToken);
        var version = await database.ExamVersions.FromSqlInterpolated($"SELECT * FROM exam_versions WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw ExamRules.Missing();
        await database.Entry(version).ReloadAsync(cancellationToken); version.Exam = exam;
        if (!ExamRules.CanRead(actor, exam, version.Status)) throw ExamRules.Missing();
        return version;
    }

    /// <summary>Prevent source edits/deletion between eligibility checks and insertion or submission of exam items.</summary>
    private async Task<Dictionary<Guid, Question>> LockSourcesAsync(IEnumerable<Guid> sourceIds, CancellationToken cancellationToken)
    {
        var ids = sourceIds.Distinct().OrderBy(x => x).ToArray();
        return await database.Questions.FromSqlInterpolated($"SELECT * FROM questions WHERE id = ANY({ids}) ORDER BY id FOR SHARE")
            .AsNoTracking().ToDictionaryAsync(q => q.Id, cancellationToken);
    }

    /// <summary>Refresh each source revision and copy every answer-bearing field before freezing the parent version.</summary>
    private async Task SnapshotAsync(AuthenticatedUser actor, Exam exam, ExamVersion version, CancellationToken cancellationToken)
    {
        var items = await database.ExamVersionQuestions.Where(i => i.ExamVersionId == version.Id).OrderBy(i => i.SortOrder).ToListAsync(cancellationToken);
        if (items.Count == 0) throw ExamRules.Invalid("items");
        var sources = await LockSourcesAsync(items.Select(i => i.SourceQuestionId), cancellationToken);
        var invalid = items.Where(i => !sources.TryGetValue(i.SourceQuestionId, out var q)
            || q.Status != "Approved" || !ExamRules.CanSelect(actor, exam, q)).Select(i => i.SourceQuestionId).ToArray();
        if (invalid.Length > 0) throw new ExamException(409, "QuestionsNotApproved", "Mọi câu trong đề phải được duyệt và đúng phân loại.", invalidQuestionIds: invalid);
        foreach (var item in items)
        {
            var q = sources[item.SourceQuestionId];
            item.SourceRevision = q.Revision; item.Type = q.Type; item.Difficulty = q.Difficulty;
            item.StemSnapshot = q.Stem; item.PayloadSnapshot = q.Payload; item.ExplanationSnapshot = q.Explanation;
        }
        version.Questions = items; version.TotalPoints = Sum(items); version.ContentHash = ExamRules.ContentHash(version);
    }

    /// <summary>Read active authorization directly from users; a revoked account challenges the existing bearer session.</summary>
    private async Task<AuthenticatedUser> ActorAsync(Guid id, bool locking, CancellationToken cancellationToken)
    {
        var users = locking ? database.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {id} FOR SHARE") : database.Users;
        var user = await users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null || !user.IsActive) throw new ExamException(401, "Unauthorized", "Chưa xác thực.");
        if (user.Role is not (Roles.Admin or Roles.Teacher or Roles.Head) || user.Role != Roles.Admin && user.DepartmentId == null)
            throw new ExamException(401, "Unauthorized", "Hồ sơ xác thực không hợp lệ.");
        return new(user.Id, user.CognitoSub, user.Email, user.FullName, user.Role, user.DepartmentId, null, true);
    }

    private IQueryable<Exam> VisibleExams(AuthenticatedUser a) => database.Exams.AsNoTracking().Where(e => a.Role == Roles.Admin
        || e.CreatedBy == a.Id || e.DepartmentId == a.DepartmentId && (a.Role == Roles.Head || e.Versions.Any(v => v.Status == "Published")));
    private IQueryable<ExamVersion> VisibleVersions(AuthenticatedUser a) => database.ExamVersions.AsNoTracking().Where(v => a.Role == Roles.Admin
        || v.Exam!.CreatedBy == a.Id || v.Exam.DepartmentId == a.DepartmentId && (a.Role == Roles.Head || v.Status == "Published"));

    /// <summary>Materialize exam metadata and filter every summary before computing visible version counters.</summary>
    private async Task<ExamDetail> DetailAsync(AuthenticatedUser actor, Guid id, CancellationToken cancellationToken)
    {
        var exam = await VisibleExams(actor).Include(e => e.Creator).Include(e => e.Versions)
            .SingleOrDefaultAsync(e => e.Id == id, cancellationToken) ?? throw ExamRules.Missing();
        var summaries = await VisibleVersions(actor).Where(v => v.ExamId == id).OrderByDescending(v => v.VersionNo)
            .Select(SummaryProjection).ToListAsync(cancellationToken);
        return new(ExamView(exam, actor), summaries);
    }

    /// <summary>Build answer-bearing version responses from snapshots unless the version is explicitly editable.</summary>
    private async Task<ExamVersionView> VersionViewAsync(AuthenticatedUser actor, Guid id, IReadOnlyList<ExamWarning> warnings, CancellationToken cancellationToken)
    {
        var v = await VersionSource().Include(x => x.Questions).ThenInclude(x => x.SourceQuestion)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw ExamRules.Missing();
        if (!ExamRules.CanRead(actor, v.Exam!, v.Status)) throw ExamRules.Missing();
        if (!ExamRules.CanReadAnswers(actor, v.Exam!)) throw ExamRules.Forbidden();
        var editable = v.Status is "Draft" or "Rejected";
        var resultWarnings = warnings.ToList();
        var items = v.Questions.OrderBy(i => i.SortOrder).Select(i =>
        {
            var q = i.SourceQuestion!;
            // Exam-level answer access must never grant access to another author's live private question.
            var canReadSource = QuestionRules.CanRead(actor, q)
                || q.Status == "Approved" && v.Exam!.CreatedBy == actor.Id && ExamRules.CanSelect(actor, v.Exam, q);
            var unavailable = editable ? !canReadSource
                : i.Type is null || i.Difficulty is null || i.StemSnapshot is null || i.PayloadSnapshot is null;
            if (unavailable)
            {
                resultWarnings.Add(new(editable ? "SourceContentUnavailable" : "SnapshotUnavailable",
                    "Nội dung câu không khả dụng trong phiên bản này.", i.SourceQuestionId));
                return new ExamItemView(i.SortOrder, i.Points, i.SourceQuestionId, i.SourceRevision, null, null, null, null, null, null);
            }
            if (editable && i.SourceRevision != q.Revision) resultWarnings.Add(new("SourceRevisionChanged", "Câu nguồn đã đổi phiên bản.", q.Id));
            return new ExamItemView(i.SortOrder, i.Points, i.SourceQuestionId, i.SourceRevision, editable ? q.Revision : null,
                editable ? q.Type : i.Type!, editable ? q.Difficulty : i.Difficulty!, editable ? q.Stem : i.StemSnapshot!,
                JsonSerializer.Deserialize<JsonElement>(editable ? q.Payload : i.PayloadSnapshot!), editable ? q.Explanation : i.ExplanationSnapshot);
        }).ToArray();
        return new(Summarize(v), items, resultWarnings);
    }

    /// <summary>Project summaries without reading payload content or serializing persistence navigations.</summary>
    private async Task<ExamVersionPage> PageVersionsAsync(IQueryable<ExamVersion> source, int page, int size, CancellationToken cancellationToken)
    {
        var count = await source.CountAsync(cancellationToken);
        var summaries = await source.Skip((page - 1) * size).Take(size).Select(SummaryProjection).ToListAsync(cancellationToken);
        return new(page, size, count, Pages(count, size), summaries);
    }

    private IQueryable<ExamVersion> VersionSource() => database.ExamVersions.AsNoTracking()
        .Include(v => v.Exam).ThenInclude(e => e!.Creator).Include(v => v.Reviewer).Include(v => v.Publisher);
    // This expression is translated to SQL: summaries count items without loading their stems, answers or payload snapshots.
    private static readonly Expression<Func<ExamVersion, ExamVersionSummary>> SummaryProjection = v => new(v.Id, v.ExamId,
        v.Exam!.Title, v.Exam.SubjectId, v.Exam.Grade, v.Exam.DepartmentId,
        new(v.Exam.CreatedBy, v.Exam.Creator!.FullName), v.VersionNo, v.Status, v.DurationMinutes, v.TotalPoints, v.Questions.Count,
        v.Status != "Draft" && v.Status != "Rejected" && v.Questions.All(i => i.Type != null && i.Difficulty != null && i.StemSnapshot != null && i.PayloadSnapshot != null)
            ? v.ContentHash : null, v.SubmittedAt,
        v.ReviewedBy != null ? new(v.ReviewedBy.Value, v.Reviewer!.FullName) : null, v.ReviewedAt, v.ReviewNote,
        v.PublishedBy != null ? new(v.PublishedBy.Value, v.Publisher!.FullName) : null, v.PublishedAt, v.CreatedAt, v.UpdatedAt);
    private static readonly Func<ExamVersion, ExamVersionSummary> Summarize = SummaryProjection.Compile();
    private static ExamView ExamView(Exam e, AuthenticatedUser actor)
    {
        var visible = e.Versions.Where(v => ExamRules.CanRead(actor, e, v.Status)).ToArray();
        // VisibleExams mirrors ExamRules.CanRead and every exam keeps v1, so `visible` is never empty.
        var latest = visible.MaxBy(v => v.VersionNo)!;
        var published = visible.SingleOrDefault(v => v.Status == "Published");
        return new(e.Id, e.Title, e.SubjectId, e.Grade, e.DepartmentId, new(e.CreatedBy, e.Creator!.FullName),
            latest.VersionNo, published?.Id, latest.Id, latest.Status, published?.VersionNo, e.CreatedAt, e.UpdatedAt);
    }
    private static ExamVersion NewVersion(Guid examId, int number, int duration, DateTime now) => new()
    { Id = Guid.NewGuid(), ExamId = examId, VersionNo = number, DurationMinutes = duration, CreatedAt = now, UpdatedAt = now };
    private static ExamVersionQuestion Link(Guid versionId, int order, decimal points, Question q) => new()
    { ExamVersionId = versionId, SortOrder = order, Points = points, SourceQuestionId = q.Id, SourceRevision = q.Revision };
    private static decimal Sum(IEnumerable<ExamVersionQuestion> items)
    {
        try { return items.Sum(i => i.Points); } catch (OverflowException) { throw ExamRules.Invalid("items"); }
    }
    private static void Touch(Exam exam) => exam.UpdatedAt = ExamRules.NextTimestamp(exam.UpdatedAt, DateTime.UtcNow);
    private static int Pages(int count, int size) => (int)(((long)count + size - 1) / size);
    private static void ValidatePage(int page, int size)
    {
        if (page < 1 || size is < 1 or > 100 || (long)(page - 1) * size > int.MaxValue) throw ExamRules.Invalid("query");
    }
    /// <summary>Record mutation history inside the transaction; no emails or question content enter audit notes.</summary>
    private void Audit(Guid actor, string entity, Guid id, Guid departmentId, string action, string? from, string? to, string? note = null) =>
        database.AuditLogs.Add(AuditLog.Create(actor, entity, id, departmentId, action, from, to, note));
    private static ExamException Conflict(string message) => new(409, "Conflict", message);
}
