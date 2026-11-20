using System.Text.Json;
using System.Text.Json.Nodes;
using ExamMaker.Application.Authentication;
using ExamMaker.Domain.Questions;

namespace ExamMaker.Application.Questions;

public static class QuestionRules
{
    public static readonly string[] States = ["Draft", "Submitted", "Approved", "Rejected", "Archived"];
    public static readonly string[] Types = ["MultipleChoice", "TrueFalse", "ShortAnswer", "Essay"];
    public static readonly string[] Difficulties = ["Recognition", "Understanding", "Application", "HighApplication"];

    /// <summary>Apply the documented visibility matrix, including a head's submitted review queue.</summary>
    public static bool CanRead(AuthenticatedUser actor, Question question) => actor.IsActive &&
        (actor.Role == Roles.Admin || AccessRules.IsOwner(actor, Resource(question))
         || actor.DepartmentId == question.DepartmentId &&
         (actor.Role is Roles.Teacher or Roles.Head && question.Status == "Approved"
          || actor.Role == Roles.Head && question.Status == "Submitted"));

    /// <summary>Reject missing or malformed content before checking database catalog relationships.</summary>
    public static QuestionInput Validate(QuestionInput input)
    {
        if (input.SubjectId is null || input.SubjectId == Guid.Empty) throw Invalid("subjectId");
        if (input.TopicId is null || input.TopicId == Guid.Empty) throw Invalid("topicId");
        if (input.Grade is not (10 or 11 or 12)) throw Invalid("grade");
        if (!Types.Contains(input.Type)) throw Invalid("type");
        if (!Difficulties.Contains(input.Difficulty)) throw Invalid("difficulty");
        var stem = input.Stem?.Trim() ?? "";
        if (stem.Length is < 1 or > 10000 || stem.Contains('\0')) throw Invalid("stem");
        if (input.Explanation?.Length > 10000 || input.Explanation?.Contains('\0') == true) throw Invalid("explanation");
        QuestionPayloadRules.Validate(input.Type, input.Payload);
        return input with { Stem = stem };
    }

    /// <summary>Require a live teacher/head owner before editing, deleting or submitting content.</summary>
    public static void RequireOwner(AuthenticatedUser actor, Question question)
    {
        if (!AccessRules.IsOwner(actor, Resource(question))) throw Forbidden();
    }

    /// <summary>Update content, reopen approved revisions and invalidate AI review flags only when content changes.</summary>
    public static bool Edit(AuthenticatedUser actor, Question question, QuestionInput input, DateTime now)
    {
        RequireOwner(actor, question);
        if (question.Status is not ("Draft" or "Rejected" or "Approved")) throw InvalidState();
        var changed = question.SubjectId != input.SubjectId || question.TopicId != input.TopicId || question.Grade != input.Grade
            || question.Type != input.Type || question.Difficulty != input.Difficulty || question.Stem != input.Stem
            || question.Explanation != input.Explanation
            || !JsonNode.DeepEquals(JsonNode.Parse(question.Payload), JsonNode.Parse(input.Payload.GetRawText()));
        if (!changed) return false;
        if (question.Status == "Approved")
        {
            if (question.Revision == int.MaxValue) throw new QuestionException(409, "Conflict", "Không thể tăng phiên bản câu hỏi.");
            question.Status = "Draft";
            question.Revision++;
            ClearReview(question);
            question.SubmittedAt = null;
        }
        question.SubjectId = input.SubjectId!.Value;
        question.TopicId = input.TopicId!.Value;
        question.Grade = input.Grade!.Value;
        question.Type = input.Type;
        question.Difficulty = input.Difficulty;
        question.Stem = input.Stem;
        question.Payload = input.Payload.GetRawText();
        question.Explanation = input.Explanation;
        if (question.Source == "AI")
        {
            question.AiReviewedByAuthor = false;
            question.AiVerification = "NotChecked";
            question.AiVerificationNote = null;
        }
        question.UpdatedAt = now;
        return true;
    }

    /// <summary>Authorize and apply one workflow transition; persistence and audit belong to the enclosing transaction.</summary>
    public static void Transition(AuthenticatedUser actor, Question question, QuestionAction action, string? note, DateTime now)
    {
        if (action is QuestionAction.Approve or QuestionAction.Reject)
        {
            var access = AccessRules.Review(actor, Resource(question));
            if (access == ReviewAccess.SelfReview) throw new QuestionException(403, "SelfReview", "Không được tự duyệt câu hỏi.");
            if (access != ReviewAccess.Allowed) throw Forbidden();
        }
        else if (action == QuestionAction.Archive)
        {
            if (!actor.IsActive || !(actor.Role == Roles.Admin || actor.Role == Roles.Head && actor.DepartmentId == question.DepartmentId
                || actor.Role == Roles.Teacher && AccessRules.IsOwner(actor, Resource(question)))) throw Forbidden();
        }
        else RequireOwner(actor, question);

        switch (action)
        {
            case QuestionAction.Submit:
                if (question.Status is not ("Draft" or "Rejected")) throw InvalidState();
                if (question.Source == "AI" && !question.AiReviewedByAuthor)
                    throw new QuestionException(409, "AiReviewRequired", "Cần rà soát câu hỏi AI trước khi gửi duyệt.");
                // Revalidate worker output before it enters the review workflow.
                Validate(new(question.SubjectId, question.TopicId, question.Grade, question.Type, question.Difficulty,
                    question.Stem, JsonSerializer.Deserialize<JsonElement>(question.Payload), question.Explanation));
                question.Status = "Submitted";
                question.SubmittedAt = now;
                ClearReview(question);
                break;
            case QuestionAction.Withdraw:
                if (question.Status != "Submitted") throw InvalidState();
                question.Status = "Draft";
                question.SubmittedAt = null;
                ClearReview(question);
                break;
            case QuestionAction.Approve:
            case QuestionAction.Reject:
                if (question.Status != "Submitted") throw InvalidState();
                note = note?.Trim();
                if (note?.Length > 2000 || note?.Contains('\0') == true
                    || action == QuestionAction.Reject && string.IsNullOrWhiteSpace(note)) throw Invalid("note");
                question.Status = action == QuestionAction.Approve ? "Approved" : "Rejected";
                question.ReviewedBy = actor.Id;
                question.ReviewedAt = now;
                question.ReviewNote = string.IsNullOrEmpty(note) ? null : note;
                break;
            case QuestionAction.Archive:
                if (question.Status != "Approved") throw InvalidState();
                question.Status = "Archived";
                break;
            case QuestionAction.MarkReviewed:
                if (question.Source != "AI" || question.Status is not ("Draft" or "Rejected")) throw InvalidState();
                question.AiReviewedByAuthor = true;
                break;
            default: throw InvalidState();
        }
        question.UpdatedAt = now;
    }

    /// <summary>Clear reviewer metadata when a review round is withdrawn, reopened or resubmitted.</summary>
    private static void ClearReview(Question question)
    {
        question.ReviewedBy = null;
        question.Reviewer = null;
        question.ReviewedAt = null;
        question.ReviewNote = null;
    }

    private static ContentAccessResource Resource(Question question) => new(question.CreatedBy, question.DepartmentId);
    public static QuestionException Forbidden() => new(403, "Forbidden", "Không có quyền thực hiện thao tác này.");
    public static QuestionException InvalidState() => new(409, "InvalidStateTransition", "Trạng thái câu hỏi không cho phép thao tác này.");
    public static QuestionException Invalid(string field) => new(400, "ValidationFailed", "Thông tin câu hỏi không hợp lệ.", field);
}
