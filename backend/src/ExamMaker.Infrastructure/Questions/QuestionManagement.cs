using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Questions;
using ExamMaker.Domain.Auditing;
using ExamMaker.Domain.Questions;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ExamMaker.Infrastructure.Questions;

public sealed class QuestionManagement(ExamMakerDbContext database, ILogger<QuestionManagement> logger) : IQuestionManagement
{
    /// <summary>Apply visibility and bank filters in SQL before paging; never expose another author's private draft.</summary>
    public async Task<QuestionPage> ListAsync(Guid actorId, QuestionQuery query, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, cancellationToken);
        ValidateQuery(query);
        var source = database.Questions.AsNoTracking().Where(x => actor.Role == Roles.Admin || x.CreatedBy == actor.Id
            || x.DepartmentId == actor.DepartmentId && x.Status == "Approved");
        source = Filter(source, query).Where(x => !query.Mine || x.CreatedBy == actor.Id);
        return await PageAsync(source.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), query.Page, query.PageSize, cancellationToken);
    }

    /// <summary>Return submitted work for eligible reviewers, excluding their own questions and other departments.</summary>
    public async Task<QuestionPage> ReviewQueueAsync(Guid actorId, QuestionReviewQuery query, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, cancellationToken);
        if (actor.Role is not (Roles.Head or Roles.Admin)) throw QuestionRules.Forbidden();
        ValidateQuery(new(query.Page, query.PageSize, SubjectId: query.SubjectId, TopicId: query.TopicId));
        var source = database.Questions.AsNoTracking().Where(x => x.Status == "Submitted" && x.CreatedBy != actor.Id
            && (actor.Role == Roles.Admin || x.DepartmentId == actor.DepartmentId)
            && (query.SubjectId == null || x.SubjectId == query.SubjectId) && (query.TopicId == null || x.TopicId == query.TopicId));
        return await PageAsync(source.OrderBy(x => x.SubmittedAt).ThenBy(x => x.Id), query.Page, query.PageSize, cancellationToken);
    }

    /// <summary>Hide inaccessible question identifiers behind the same 404 used for missing records.</summary>
    public async Task<QuestionView> GetAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(actorId, cancellationToken);
        var question = await ReadSource().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw Missing();
        if (!QuestionRules.CanRead(actor, question)) throw Missing();
        return View(question);
    }

    /// <summary>Create a manual draft with catalog-derived department and immutable author/provenance fields.</summary>
    public async Task<QuestionView> CreateAsync(Guid actorId, QuestionInput input, CancellationToken cancellationToken)
    {
        input = QuestionRules.Validate(input);
        return (await MutateAsync(actorId, async actor =>
        {
            if (actor.Role is not (Roles.Teacher or Roles.Head)) throw QuestionRules.Forbidden();
            var department = await CatalogAsync(input, cancellationToken);
            if (actor.DepartmentId != department) throw QuestionRules.Forbidden();
            var now = DateTime.UtcNow;
            var question = new Question
            {
                Id = Guid.NewGuid(),
                SubjectId = input.SubjectId!.Value,
                TopicId = input.TopicId!.Value,
                DepartmentId = department,
                Grade = input.Grade!.Value,
                Type = input.Type,
                Difficulty = input.Difficulty,
                Stem = input.Stem,
                Payload = input.Payload.GetRawText(),
                Explanation = input.Explanation,
                CreatedBy = actor.Id,
                CreatedAt = now,
                UpdatedAt = now
            };
            database.Questions.Add(question);
            Audit(actor.Id, question, "CreateQuestion", null, "Manual question; revision=1");
            return question;
        }, false, cancellationToken))!;
    }

    /// <summary>Lock the source row, preserve its department and apply pure revision/edit rules in one audited transaction.</summary>
    public async Task<QuestionView> UpdateAsync(Guid actorId, Guid id, QuestionInput input, CancellationToken cancellationToken)
    {
        return (await MutateAsync(actorId, async actor =>
        {
            var question = await LockQuestionAsync(actor, id, cancellationToken);
            QuestionRules.RequireOwner(actor, question);
            QuestionRules.RequireEditable(question);
            input = QuestionRules.Validate(input);
            if (await CatalogAsync(input, cancellationToken) != question.DepartmentId) throw QuestionRules.Invalid("subjectId");
            if (question.SubjectId != input.SubjectId || question.TopicId != input.TopicId || question.Grade != input.Grade)
                await EnsureClassificationChangeAllowedAsync(id, cancellationToken);
            var before = question.Status;
            var revision = question.Revision;
            if (QuestionRules.Edit(actor, question, input, DateTime.UtcNow))
                Audit(actor.Id, question, "UpdateQuestion", before, $"revision={revision}->{question.Revision}");
            return question;
        }, false, cancellationToken))!;
    }

    /// <summary>Serialize competing review decisions and let Application enforce the workflow and self-review rules.</summary>
    public async Task<QuestionView> TransitionAsync(Guid actorId, Guid id, QuestionAction action, string? note, CancellationToken cancellationToken) =>
        (await MutateAsync(actorId, async actor =>
        {
            var question = await LockQuestionAsync(actor, id, cancellationToken);
            var before = question.Status;
            if (QuestionRules.Transition(actor, question, action, note, DateTime.UtcNow))
                Audit(actor.Id, question, action + "Question", before,
                    action is QuestionAction.Approve or QuestionAction.Reject ? question.ReviewNote : null);
            return question;
        }, false, cancellationToken))!;

    /// <summary>Delete an owned editable question without exam references, unlinking collection membership in the same transaction.</summary>
    public async Task DeleteAsync(Guid actorId, Guid id, CancellationToken cancellationToken)
    {
        await MutateAsync(actorId, async actor =>
        {
            var question = await LockQuestionAsync(actor, id, cancellationToken);
            QuestionRules.RequireOwner(actor, question);
            if (question.Status is not ("Draft" or "Rejected")) throw QuestionRules.InvalidState();
            if (await database.ExamVersionQuestions.AnyAsync(x => x.SourceQuestionId == id, cancellationToken)) throw Referenced();
            // Collection ownership does not prevent the author deleting an eligible bank question (context section 8.1).
            // RETURNING identifies only memberships actually removed, even if a collection is edited concurrently.
            var affectedSets = await database.Database.SqlQuery<Guid>($"""
                DELETE FROM question_set_items WHERE question_id = {id}
                RETURNING question_set_id AS "Value"
                """).ToListAsync(cancellationToken);
            foreach (var setId in affectedSets.Distinct())
                database.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = actor.Id,
                    EntityType = "QuestionSet",
                    EntityId = setId,
                    Action = "RemoveQuestionFromSet",
                    Note = $"Source question deleted; question={id}; revision={question.Revision}",
                    CreatedAt = DateTime.UtcNow
                });
            Audit(actor.Id, question, "DeleteQuestion", question.Status,
                $"revision={question.Revision}; subject={question.SubjectId}; topic={question.TopicId}; removedCollectionLinks={affectedSets.Count}");
            // Execute the restricted delete in PostgreSQL; tracked collection links must not trigger client-side cascades.
            await database.Questions.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
            return null;
        }, true, cancellationToken);
    }

    /// <summary>Re-read authorization inside the management/catalog locks and commit question changes and audits atomically.</summary>
    private async Task<QuestionView?> MutateAsync(Guid actorId, Func<AuthenticatedUser, Task<Question?>> operation,
        bool deleting, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(48683401)", cancellationToken);
            var actor = await ActorAsync(actorId, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(48683402)", cancellationToken);
            var question = await operation(actor);
            await database.SaveChangesAsync(cancellationToken);
            var result = question is null ? null : View(await ReadSource().SingleAsync(x => x.Id == question.Id, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is PostgresException || exception is DbUpdateException { InnerException: PostgresException })
        {
            var postgres = (exception as PostgresException ?? exception.InnerException as PostgresException)!;
            logger.LogWarning("Question mutation failed: {Type}; SQLSTATE {Code}; stack {Stack}", exception.GetType().Name, postgres.SqlState, exception.StackTrace);
            if (deleting && postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation) throw Referenced();
            if (postgres.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.UniqueViolation)
                throw new QuestionException(409, "Conflict", "Dữ liệu đang được sử dụng hoặc thay đổi. Hãy tải lại trước khi thử lại.");
            throw new QuestionException(500, "InternalError", "Không thể lưu câu hỏi. Hãy kiểm tra dữ liệu trước khi thử lại.");
        }
    }

    /// <summary>Hold an update lock until commit and refresh tracked state before making a workflow decision.</summary>
    private async Task<Question> LockQuestionAsync(AuthenticatedUser actor, Guid id, CancellationToken cancellationToken)
    {
        var question = await database.Questions.FromSqlInterpolated($"SELECT * FROM questions WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw Missing();
        await database.Entry(question).ReloadAsync(cancellationToken);
        if (!QuestionRules.CanRead(actor, question)) throw Missing();
        return question;
    }

    /// <summary>Keep editable exam membership consistent and serialize classification changes with version status transitions.</summary>
    private async Task EnsureClassificationChangeAllowedAsync(Guid id, CancellationToken cancellationToken)
    {
        // Lock all referencing versions, including frozen ones that could be withdrawn into an editable state.
        // A competing submit locks the version before its source questions; a deadlock is safely retried as Conflict.
        var states = await database.Database.SqlQuery<string>($"""
            SELECT v.status AS "Value" FROM exam_versions v
            WHERE EXISTS (SELECT 1 FROM exam_version_questions i
                WHERE i.exam_version_id = v.id AND i.source_question_id = {id})
            ORDER BY v.id FOR SHARE OF v
            """).ToListAsync(cancellationToken);
        if (states.Any(x => x is "Draft" or "Rejected" or "Submitted"))
            throw new QuestionException(409, "QuestionReferenced", "Không thể đổi môn, chương hoặc khối của câu trong đề đang soạn.");
    }

    /// <summary>Validate topic/subject/grade together and derive the immutable department from trusted catalog data.</summary>
    private async Task<Guid> CatalogAsync(QuestionInput input, CancellationToken cancellationToken) =>
        await database.Topics.Where(x => x.Id == input.TopicId && x.SubjectId == input.SubjectId && x.Grade == input.Grade)
            .Select(x => (Guid?)x.Subject!.DepartmentId).SingleOrDefaultAsync(cancellationToken) ?? throw QuestionRules.Invalid("topicId");

    /// <summary>Read live role, activation and department instead of trusting cached token claims.</summary>
    private async Task<AuthenticatedUser> ActorAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null || !user.IsActive)
            throw new QuestionException(401, "Unauthorized", "Chưa xác thực");
        if (user.Role is not (Roles.Teacher or Roles.Head or Roles.Admin)) throw QuestionRules.Forbidden();
        return new(user.Id, user.CognitoSub, user.Email, user.FullName, user.Role, user.DepartmentId, null, true);
    }

    /// <summary>Apply optional catalog, provenance, ownership and state filters without changing the caller's visibility scope.</summary>
    private static IQueryable<Question> Filter(IQueryable<Question> source, QuestionQuery query) => source.Where(x =>
        (query.Status == null || x.Status == query.Status) && (query.SubjectId == null || x.SubjectId == query.SubjectId)
        && (query.TopicId == null || x.TopicId == query.TopicId) && (query.Grade == null || x.Grade == query.Grade)
        && (query.Type == null || x.Type == query.Type) && (query.Difficulty == null || x.Difficulty == query.Difficulty)
        && (query.AiJobId == null || x.AiJobId == query.AiJobId));

    /// <summary>Reject unsupported enum values, empty identifiers and unsafe pagination offsets before SQL execution.</summary>
    private static void ValidateQuery(QuestionQuery query)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || (long)(query.Page - 1) * query.PageSize > int.MaxValue
            || query.Status != null && !QuestionRules.States.Contains(query.Status) || query.Grade is not (null or 10 or 11 or 12)
            || query.Type != null && !QuestionRules.Types.Contains(query.Type) || query.Difficulty != null && !QuestionRules.Difficulties.Contains(query.Difficulty)
            || query.SubjectId == Guid.Empty || query.TopicId == Guid.Empty || query.AiJobId == Guid.Empty) throw QuestionRules.Invalid("query");
    }

    /// <summary>Materialize only the requested page and project payload JSON plus public author/reviewer references.</summary>
    private static async Task<QuestionPage> PageAsync(IQueryable<Question> source, int page, int size, CancellationToken cancellationToken)
    {
        var count = await source.CountAsync(cancellationToken);
        var items = await source.Include(x => x.Creator).Include(x => x.Reviewer).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new(page, size, count, (int)(((long)count + size - 1) / size), items.Select(View).ToArray());
    }

    private IQueryable<Question> ReadSource() => database.Questions.AsNoTracking().Include(x => x.Creator).Include(x => x.Reviewer);

    /// <summary>Return the OpenAPI Question shape, including a JSON object payload rather than an escaped JSON string.</summary>
    private static QuestionView View(Question q) => new(q.Id, q.SubjectId, q.TopicId, q.DepartmentId, q.Grade, q.Type, q.Difficulty,
        q.Stem, JsonSerializer.Deserialize<JsonElement>(q.Payload), q.Explanation, q.Status, q.Revision, q.Source, q.AiJobId, q.AiOrdinal,
        q.AiVerification, q.AiVerificationNote, q.AiReviewedByAuthor, new(q.CreatedBy, q.Creator!.FullName), q.SubmittedAt,
        q.ReviewedBy is Guid reviewer ? new(reviewer, q.Reviewer!.FullName) : null, q.ReviewedAt, q.ReviewNote, q.CreatedAt, q.UpdatedAt);

    /// <summary>Persist workflow history in the same transaction, including deletes without a polymorphic foreign key.</summary>
    private void Audit(Guid actorId, Question question, string action, string? from, string? note) => database.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        ActorId = actorId,
        EntityType = "Question",
        EntityId = question.Id,
        Action = action,
        FromStatus = from,
        ToStatus = action == "DeleteQuestion" ? null : question.Status,
        Note = note,
        CreatedAt = DateTime.UtcNow
    });

    private static QuestionException Missing() => new(404, "NotFound", "Không tìm thấy câu hỏi.");
    private static QuestionException Referenced() => new(409, "QuestionReferenced", "Câu hỏi đang được đề tham chiếu.");
}
