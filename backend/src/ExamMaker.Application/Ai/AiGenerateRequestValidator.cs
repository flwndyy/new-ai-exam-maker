namespace ExamMaker.Application.Ai;

internal static class AiGenerateRequestValidator
{
    private static readonly HashSet<string> QuestionTypes =
        ["MultipleChoice", "TrueFalse", "ShortAnswer", "Essay"];

    private static readonly HashSet<string> Difficulties =
        ["Recognition", "Understanding", "Application", "HighApplication"];

    public static Dictionary<string, string[]> Validate(AiGenerateRequest request, AiQuotaOptions options)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.SubjectId == Guid.Empty)
        {
            errors["subjectId"] = [AiMessages.SubjectRequired];
        }

        if (request.TopicId == Guid.Empty)
        {
            errors["topicId"] = [AiMessages.TopicRequired];
        }

        if (request.Grade is < 10 or > 12)
        {
            errors["grade"] = [AiMessages.GradeInvalid];
        }

        if (!QuestionTypes.Contains(request.Type))
        {
            errors["type"] = [AiMessages.TypeInvalid];
        }

        if (!Difficulties.Contains(request.Difficulty))
        {
            errors["difficulty"] = [AiMessages.DifficultyInvalid];
        }

        if (request.Count < 1 || request.Count > options.MaxQuestionsPerJob)
        {
            errors["count"] = [AiMessages.CountOutOfRange(options.MaxQuestionsPerJob)];
        }

        if (request.Note is { } note && note.Length > options.MaxNoteLength)
        {
            errors["note"] = [AiMessages.NoteTooLong(options.MaxNoteLength)];
        }

        return errors;
    }
}
