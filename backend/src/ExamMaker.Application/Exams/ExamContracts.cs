using System.Text.Json;
using ExamMaker.Application.Questions;

namespace ExamMaker.Application.Exams;

public sealed record ExamCreateInput(string? Title, Guid? SubjectId, int? Grade);
public sealed record ExamUpdateInput(string? Title);
public sealed record ExamVersionCreateInput(Guid? FromVersionId = null);
public sealed record ExamItemInput(Guid SourceQuestionId, decimal Points);
public sealed record ExamVersionInput(int DurationMinutes, IReadOnlyList<ExamItemInput>? Items);
public sealed record ExamReviewInput(DateTimeOffset? SubmittedAt, string? Note = null);
public sealed record ExamQuery(int Page = 1, int PageSize = 20, Guid? SubjectId = null,
    int? Grade = null, bool Mine = false, bool? HasPublished = null);
public sealed record ExamReviewQuery(int Page = 1, int PageSize = 20, Guid? SubjectId = null);
public sealed record ExamPage(int Page, int PageSize, int TotalItems, int TotalPages, IReadOnlyList<ExamView> Items);
public record ExamView(Guid Id, string Title, Guid SubjectId, int Grade, Guid DepartmentId,
    QuestionUser CreatedBy, int LatestVersionNo, Guid? PublishedVersionId, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record ExamDetail : ExamView
{
    public ExamDetail(ExamView exam, IReadOnlyList<ExamVersionSummary> versions) : base(exam) => Versions = versions;
    public IReadOnlyList<ExamVersionSummary> Versions { get; init; }
}
public record ExamVersionSummary(Guid Id, Guid ExamId, string ExamTitle, Guid SubjectId, int Grade,
    Guid DepartmentId, QuestionUser CreatedBy, int VersionNo, string Status, int DurationMinutes,
    decimal TotalPoints, int QuestionCount, string? ContentHash, DateTime? SubmittedAt,
    QuestionUser? ReviewedBy, DateTime? ReviewedAt, string? ReviewNote,
    QuestionUser? PublishedBy, DateTime? PublishedAt, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record ExamItemView(int SortOrder, decimal Points, Guid SourceQuestionId, int SourceRevision,
    int? CurrentSourceRevision, string? Type, string? Difficulty, string? Stem, JsonElement? Payload, string? Explanation);
public sealed record ExamWarning(string Code, string Message, Guid? SourceQuestionId = null);
public sealed record ExamVersionView : ExamVersionSummary
{
    public ExamVersionView(ExamVersionSummary summary, IReadOnlyList<ExamItemView> items,
        IReadOnlyList<ExamWarning> warnings) : base(summary) { Items = items; Warnings = warnings; }
    public IReadOnlyList<ExamItemView> Items { get; init; }
    public IReadOnlyList<ExamWarning> Warnings { get; init; }
}
public sealed record ExamVersionPage(int Page, int PageSize, int TotalItems, int TotalPages, IReadOnlyList<ExamVersionSummary> Items);
public enum ExamAction { Submit, Withdraw, Approve, Reject, Archive, Publish, Unpublish }
public sealed class ExamException(int statusCode, string code, string message, string? field = null,
    IReadOnlyList<Guid>? invalidQuestionIds = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? Field { get; } = field;
    public IReadOnlyList<Guid>? InvalidQuestionIds { get; } = invalidQuestionIds;
}

/// <summary>Expose exam identity, editable composition and atomic version workflow independently of HTTP and EF.</summary>
public interface IExamManagement
{
    Task<ExamPage> ListAsync(Guid actor, ExamQuery query, CancellationToken cancellationToken);
    Task<ExamDetail> GetAsync(Guid actor, Guid id, CancellationToken cancellationToken);
    Task<ExamDetail> CreateAsync(Guid actor, ExamCreateInput input, CancellationToken cancellationToken);
    Task<ExamDetail> UpdateAsync(Guid actor, Guid id, ExamUpdateInput input, CancellationToken cancellationToken);
    Task<ExamVersionPage> VersionsAsync(Guid actor, Guid id, int page, int pageSize, CancellationToken cancellationToken);
    Task<ExamVersionView> GetVersionAsync(Guid actor, Guid id, CancellationToken cancellationToken);
    Task<ExamVersionView> CreateVersionAsync(Guid actor, Guid exam, ExamVersionCreateInput input, CancellationToken cancellationToken);
    Task<ExamVersionView> UpdateVersionAsync(Guid actor, Guid id, ExamVersionInput input, CancellationToken cancellationToken);
    Task<ExamVersionView> TransitionAsync(Guid actor, Guid id, ExamAction action, ExamReviewInput? input, CancellationToken cancellationToken);
    Task<ExamVersionPage> ReviewQueueAsync(Guid actor, ExamReviewQuery query, CancellationToken cancellationToken);
    Task<StudentSheet> StudentSheetAsync(Guid actor, Guid id, CancellationToken cancellationToken);
    Task<AnswerKey> AnswerKeyAsync(Guid actor, Guid id, CancellationToken cancellationToken);
}
