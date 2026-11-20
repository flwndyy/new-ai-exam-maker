using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ExamMaker.Application.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DepartmentInput([Required, StringLength(200, MinimumLength = 1)] string Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubjectInput([Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required] Guid? DepartmentId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TopicInput([Required] Guid? SubjectId, [Required, Range(10, 12)] int? Grade,
    [Required, StringLength(300, MinimumLength = 1)] string Name,
    [Required, Range(0, int.MaxValue)] int? SortOrder);

public sealed record DepartmentView(Guid Id, string Name);
public sealed record SubjectView(Guid Id, string Name, Guid DepartmentId);
public sealed record TopicView(Guid Id, Guid SubjectId, int Grade, string Name, int SortOrder);
public sealed record CatalogQuery(int Page = 1, int PageSize = 20, Guid? DepartmentId = null,
    Guid? SubjectId = null, int? Grade = null);
public sealed record CatalogPage<T>(int Page, int PageSize, int TotalItems, int TotalPages, IReadOnlyList<T> Items);

public sealed class CatalogException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>Expose catalog use cases without coupling controllers to EF entities or database transport.</summary>
public interface ICatalogManagement
{
    Task<CatalogPage<DepartmentView>> DepartmentsAsync(CatalogQuery query, CancellationToken cancellationToken);
    Task<CatalogPage<SubjectView>> SubjectsAsync(CatalogQuery query, CancellationToken cancellationToken);
    Task<CatalogPage<TopicView>> TopicsAsync(CatalogQuery query, CancellationToken cancellationToken);
    Task<DepartmentView> SaveDepartmentAsync(Guid actorId, Guid? id, DepartmentInput input, CancellationToken cancellationToken);
    Task<SubjectView> SaveSubjectAsync(Guid actorId, Guid? id, SubjectInput input, CancellationToken cancellationToken);
    Task<TopicView> SaveTopicAsync(Guid actorId, Guid? id, TopicInput input, CancellationToken cancellationToken);
    Task DeleteDepartmentAsync(Guid actorId, Guid id, CancellationToken cancellationToken);
    Task DeleteSubjectAsync(Guid actorId, Guid id, CancellationToken cancellationToken);
    Task DeleteTopicAsync(Guid actorId, Guid id, CancellationToken cancellationToken);
}

public static class CatalogInputRules
{
    /// <summary>Trim labels and reject blank, oversized or PostgreSQL-incompatible names before persistence.</summary>
    public static string Name(string? value, int maximum)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0 || name.Length > maximum || name.Contains('\0')) throw Invalid();
        return name;
    }

    /// <summary>Require an explicit nonempty parent identifier for create and full replacement requests.</summary>
    public static Guid Parent(Guid? value) => value is Guid id && id != Guid.Empty ? id : throw Invalid();

    /// <summary>Reject invalid filters and pagination offsets before executing database queries.</summary>
    public static void Query(CatalogQuery query)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > 100
            || (long)(query.Page - 1) * query.PageSize > int.MaxValue
            || query.DepartmentId == Guid.Empty || query.SubjectId == Guid.Empty
            || query.Grade is not (null or 10 or 11 or 12)) throw Invalid();
    }

    /// <summary>Create the shared validation error exposed by catalog endpoints.</summary>
    public static CatalogException Invalid() => new(400, "ValidationFailed", "Thông tin danh mục không hợp lệ.");
}
