using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Domain.Exams;
using ExamMaker.Domain.Questions;

namespace ExamMaker.Application.Exams;

public static class ExamRules
{
    public static readonly string[] States = ["Draft", "Submitted", "Approved", "Rejected", "Published", "Archived"];

    /// <summary>Restrict private versions to the author, same-department heads and administrators; peers see published metadata.</summary>
    public static bool CanRead(AuthenticatedUser actor, Exam exam, string status) => actor.IsActive && (actor.Role == Roles.Admin
        || AccessRules.IsOwner(actor, Resource(exam))
        || actor.DepartmentId == exam.DepartmentId && (actor.Role == Roles.Head || actor.Role == Roles.Teacher && status == "Published"));

    /// <summary>Protect answer-bearing detail separately from published exam metadata.</summary>
    public static bool CanReadAnswers(AuthenticatedUser actor, Exam exam) => actor.IsActive && (actor.Role == Roles.Admin
        || AccessRules.IsOwner(actor, Resource(exam)) || actor.Role == Roles.Head && actor.DepartmentId == exam.DepartmentId);

    /// <summary>Require creator authorship for composition and submission, including after a department transfer.</summary>
    public static void RequireOwner(AuthenticatedUser actor, Exam exam)
    {
        if (!AccessRules.IsOwner(actor, Resource(exam))) throw Forbidden();
    }

    /// <summary>Normalize exam titles and reject empty, oversized or control-character input before persistence.</summary>
    public static string Title(string? title)
    {
        title = title?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrEmpty(title) || title.Length > 300 || title.Any(char.IsControl)) throw Invalid("title");
        return title;
    }

    /// <summary>Bound composition size, unique source identifiers, duration and the decimal points sum.</summary>
    public static decimal ValidateComposition(ExamVersionInput input)
    {
        if (input.DurationMinutes is < 1 or > 600) throw Invalid("durationMinutes");
        if (input.Items is null || input.Items.Count > 200 || input.Items.Any(x => x is null || x.SourceQuestionId == Guid.Empty || x.Points <= 0)
            || input.Items.Select(x => x.SourceQuestionId).Distinct().Count() != input.Items.Count) throw Invalid("items");
        try { return input.Items.Sum(x => x.Points); }
        catch (OverflowException) { throw Invalid("items"); }
    }

    /// <summary>Only editable versions may change their question links, duration or snapshots.</summary>
    public static void RequireEditable(ExamVersion version)
    {
        if (version.Status is not ("Draft" or "Rejected")) throw InvalidState();
    }

    /// <summary>Authorize an action and its source state before inspecting a review token, note or source content.</summary>
    public static void RequireAction(AuthenticatedUser actor, Exam exam, ExamVersion version, ExamAction action)
    {
        if (action is ExamAction.Approve or ExamAction.Reject)
        {
            var access = AccessRules.Review(actor, Resource(exam));
            if (access == ReviewAccess.SelfReview) throw new ExamException(403, "SelfReview", "Không được tự duyệt đề.");
            if (access != ReviewAccess.Allowed) throw Forbidden();
        }
        else if (action is ExamAction.Publish or ExamAction.Unpublish)
        {
            if (actor.Role != Roles.Admin) throw Forbidden();
        }
        else if (action == ExamAction.Archive)
        {
            if (!(actor.Role == Roles.Admin || actor.Role == Roles.Head && actor.DepartmentId == exam.DepartmentId
                || AccessRules.IsOwner(actor, Resource(exam)))) throw Forbidden();
        }
        else RequireOwner(actor, exam);
        var valid = action switch
        {
            ExamAction.Submit => version.Status is "Draft" or "Rejected",
            ExamAction.Withdraw or ExamAction.Approve or ExamAction.Reject => version.Status == "Submitted",
            ExamAction.Archive => version.Status is "Approved" or "Rejected",
            ExamAction.Publish => version.Status == "Approved",
            ExamAction.Unpublish => version.Status == "Published",
            _ => false
        };
        if (!valid) throw InvalidState();
    }

    /// <summary>Apply workflow metadata using millisecond submission tokens and PostgreSQL-safe increasing update times.</summary>
    public static void Transition(AuthenticatedUser actor, Exam exam, ExamVersion version, ExamAction action,
        ExamReviewInput? input, DateTime now)
    {
        RequireAction(actor, exam, version, action);
        if (action is ExamAction.Approve or ExamAction.Reject)
        {
            if (input?.SubmittedAt is null) throw Invalid("submittedAt");
            if (version.SubmittedAt is not DateTime submitted || submitted.Ticks / TimeSpan.TicksPerMillisecond
                != input.SubmittedAt.Value.UtcTicks / TimeSpan.TicksPerMillisecond)
                throw new ExamException(409, "Conflict", "Lượt gửi duyệt đã thay đổi. Hãy đọc lại nội dung.");
            var note = input.Note?.Trim();
            if (note?.Length > 2000 || note?.Contains('\0') == true || action == ExamAction.Reject && string.IsNullOrWhiteSpace(note)) throw Invalid("note");
            version.ReviewNote = string.IsNullOrEmpty(note) ? null : note;
            version.ReviewedBy = actor.Id; version.ReviewedAt = now;
        }
        switch (action)
        {
            case ExamAction.Submit:
                now = new DateTime(Math.Max(now.Ticks / TimeSpan.TicksPerMillisecond,
                    version.UpdatedAt.Ticks / TimeSpan.TicksPerMillisecond + 1) * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
                version.Status = "Submitted"; version.SubmittedAt = now; ClearReview(version); break;
            case ExamAction.Withdraw:
                version.Status = "Draft"; version.SubmittedAt = null; version.ContentHash = null; ClearReview(version); break;
            case ExamAction.Approve: version.Status = "Approved"; break;
            case ExamAction.Reject: version.Status = "Rejected"; break;
            case ExamAction.Archive: version.Status = "Archived"; break;
            case ExamAction.Publish:
                version.Status = "Published"; version.PublishedBy = actor.Id; version.PublishedAt = now; break;
            case ExamAction.Unpublish: version.Status = "Approved"; break;
        }
        version.UpdatedAt = NextTimestamp(version.UpdatedAt, now);
    }

    /// <summary>Keep source selection rules identical for copying, composing and resubmitting an exam.</summary>
    public static bool CanSelect(AuthenticatedUser actor, Exam exam, Question question) => question.Status != "Archived"
        && question.SubjectId == exam.SubjectId && question.Grade == exam.Grade && question.DepartmentId == exam.DepartmentId
        && (question.CreatedBy == actor.Id || question.Status == "Approved" && question.DepartmentId == exam.DepartmentId);

    /// <summary>Hash deterministic content only; JSON object property order and decimal scale cannot change the digest.</summary>
    public static string ContentHash(ExamVersion version)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("durationMinutes", version.DurationMinutes);
            writer.WriteStartArray("items");
            foreach (var item in version.Questions.OrderBy(x => x.SortOrder))
            {
                writer.WriteStartObject(); writer.WriteNumber("sortOrder", item.SortOrder);
                writer.WritePropertyName("points"); writer.WriteRawValue(item.Points.ToString("G29", CultureInfo.InvariantCulture));
                writer.WriteString("sourceQuestionId", item.SourceQuestionId); writer.WriteNumber("sourceRevision", item.SourceRevision);
                writer.WriteString("type", item.Type); writer.WriteString("difficulty", item.Difficulty);
                writer.WriteString("stem", item.StemSnapshot); writer.WriteString("explanation", item.ExplanationSnapshot);
                writer.WritePropertyName("payload");
                using var payload = JsonDocument.Parse(item.PayloadSnapshot!); WriteCanonical(writer, payload.RootElement);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>Sort object keys recursively while preserving semantically ordered arrays and storage-safe numeric tokens.</summary>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    /// <summary>Advance every real change by at least one persisted microsecond, including under backward clocks.</summary>
    public static DateTime NextTimestamp(DateTime previous, DateTime now) => new(Math.Max(
        now.Ticks / TimeSpan.TicksPerMicrosecond, previous.Ticks / TimeSpan.TicksPerMicrosecond + 1) * TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);

    private static void ClearReview(ExamVersion version) { version.ReviewedBy = null; version.ReviewedAt = null; version.ReviewNote = null; }
    private static ContentAccessResource Resource(Exam exam) => new(exam.CreatedBy, exam.DepartmentId);
    public static ExamException Invalid(string field) => new(400, "ValidationFailed", "Thông tin đề không hợp lệ.", field);
    public static ExamException Forbidden() => new(403, "Forbidden", "Không có quyền thực hiện thao tác này.");
    public static ExamException Missing() => new(404, "NotFound", "Không tìm thấy đề hoặc phiên bản.");
    public static ExamException InvalidState() => new(409, "InvalidStateTransition", "Trạng thái phiên bản không cho phép thao tác này.");
}
