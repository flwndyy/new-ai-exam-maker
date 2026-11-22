using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Tests.Ai.Generation;

internal static class AiGenerationSpecs
{
    public static readonly Guid SubjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid TopicId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static AiGenerationSpec Sample(string type = "MultipleChoice", string difficulty = "Understanding",
        int count = 1, string? note = null) => new(
        SubjectId, TopicId, Grade: 10, type, difficulty, count,
        SubjectName: "Toán", TopicName: "Mệnh đề và tập hợp", note);
}
