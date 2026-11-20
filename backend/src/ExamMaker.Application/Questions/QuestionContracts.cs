using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExamMaker.Application.Questions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuestionInput([Required] Guid? SubjectId, [Required] Guid? TopicId,
    [Required] int? Grade, [Required] string Type, [Required] string Difficulty,
    [Required] string Stem, JsonElement Payload, string? Explanation = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuestionReviewInput(string? Note = null);
public sealed record QuestionQuery(int Page = 1, int PageSize = 20, string? Status = null,
    Guid? SubjectId = null, Guid? TopicId = null, int? Grade = null, string? Type = null,
    string? Difficulty = null, bool Mine = false, Guid? AiJobId = null);
public sealed record QuestionReviewQuery(int Page = 1, int PageSize = 20, Guid? SubjectId = null, Guid? TopicId = null);
public sealed record QuestionUser(Guid Id, string FullName);
public sealed record QuestionView(Guid Id, Guid SubjectId, Guid TopicId, Guid DepartmentId,
    int Grade, string Type, string Difficulty, string Stem, JsonElement Payload, string? Explanation,
    string Status, int Revision, string Source, Guid? AiJobId, int? AiOrdinal,
    string AiVerification, string? AiVerificationNote, bool AiReviewedByAuthor,
    QuestionUser CreatedBy, DateTime? SubmittedAt, QuestionUser? ReviewedBy, DateTime? ReviewedAt,
    string? ReviewNote, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record QuestionPage(int Page, int PageSize, int TotalItems, int TotalPages, IReadOnlyList<QuestionView> Items);

public enum QuestionAction { Submit, Withdraw, Approve, Reject, Archive, MarkReviewed }

public sealed class QuestionException(int statusCode, string code, string message,
    string? field = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? Field { get; } = field;
}

/// <summary>Expose bank queries and atomic question mutations without leaking persistence entities to HTTP.</summary>
public interface IQuestionManagement
{
    Task<QuestionPage> ListAsync(Guid actorId, QuestionQuery query, CancellationToken cancellationToken);
    Task<QuestionPage> ReviewQueueAsync(Guid actorId, QuestionReviewQuery query, CancellationToken cancellationToken);
    Task<QuestionView> GetAsync(Guid actorId, Guid id, CancellationToken cancellationToken);
    Task<QuestionView> CreateAsync(Guid actorId, QuestionInput input, CancellationToken cancellationToken);
    Task<QuestionView> UpdateAsync(Guid actorId, Guid id, QuestionInput input, CancellationToken cancellationToken);
    Task<QuestionView> TransitionAsync(Guid actorId, Guid id, QuestionAction action, string? note, CancellationToken cancellationToken);
    Task DeleteAsync(Guid actorId, Guid id, CancellationToken cancellationToken);
}
