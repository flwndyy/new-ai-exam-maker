using System.Text.Json;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Domain.Questions;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class EfAiDuplicateKeySourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string PayloadFor(string marker) =>
        $$"""{"options":[{"label":"A","text":"{{marker}}"},{"label":"B","text":"2"},{"label":"C","text":"3"},{"label":"D","text":"4"}],"correctLabel":"A"}""";

    private async Task<(Guid SubjectId, Guid TopicId, Guid OtherTopicId)> PickTopicsAsync()
    {
        await using var context = fixture.CreateContext();
        var topic = await context.Topics.Include(t => t.Subject).FirstAsync();
        // Same subject and grade, different chapter, so the composite FK stays satisfied.
        var other = await context.Topics.FirstAsync(
            t => t.SubjectId == topic.SubjectId && t.Grade == topic.Grade && t.Id != topic.Id);
        return (topic.SubjectId, topic.Id, other.Id);
    }

    private async Task<string> AddQuestionAsync(
        Guid userId, Guid topicId, string status, string stem, string payload)
    {
        await InsertQuestionAsync(userId, topicId, status, stem, payload);
        using var doc = JsonDocument.Parse(payload);
        return AiDuplicateFilter.Key("MultipleChoice", stem, doc.RootElement);
    }

    private async Task InsertQuestionAsync(
        Guid userId, Guid topicId, string status, string stem, string payload)
    {
        await using var context = fixture.CreateContext();
        var topic = await context.Topics.Include(t => t.Subject).SingleAsync(t => t.Id == topicId);
        context.Questions.Add(new Question
        {
            Id = Guid.NewGuid(),
            SubjectId = topic.SubjectId,
            TopicId = topic.Id,
            DepartmentId = topic.Subject!.DepartmentId,
            Grade = topic.Grade,
            Type = "MultipleChoice",
            Difficulty = "Recognition",
            Stem = stem,
            Payload = payload,
            Status = status,
            Source = "Manual",
            CreatedBy = userId,
            ReviewedBy = status == "Archived" ? userId : null,
        });
        await context.SaveChangesAsync();
    }

    private async Task<IReadOnlySet<string>> GetKeysAsync(Guid subjectId, Guid topicId)
    {
        await using var context = fixture.CreateContext();
        return await new EfAiDuplicateKeySource(context).GetKeysAsync(subjectId, topicId, CancellationToken.None);
    }

    [PostgresFact]
    public async Task Returns_keys_for_all_statuses_and_authors_in_topic()
    {
        var (subjectId, topicId, _) = await PickTopicsAsync();
        var userA = await AiTestData.CreateUserAsync(fixture);
        var userB = await AiTestData.CreateUserAsync(fixture);
        var suffix = Guid.NewGuid().ToString("N");
        var draftKey = await AddQuestionAsync(userA, topicId, "Draft", "Draft stem " + suffix, PayloadFor("d" + suffix));
        var archivedKey = await AddQuestionAsync(userB, topicId, "Archived", "Archived stem " + suffix, PayloadFor("a" + suffix));

        var keys = await GetKeysAsync(subjectId, topicId);

        Assert.Contains(draftKey, keys);
        Assert.Contains(archivedKey, keys);
    }

    [PostgresFact]
    public async Task Excludes_other_topics()
    {
        var (subjectId, topicId, otherTopicId) = await PickTopicsAsync();
        var userId = await AiTestData.CreateUserAsync(fixture);
        var suffix = Guid.NewGuid().ToString("N");
        var otherKey = await AddQuestionAsync(userId, otherTopicId, "Draft", "Other topic stem " + suffix, PayloadFor("o" + suffix));

        var keys = await GetKeysAsync(subjectId, topicId);

        Assert.DoesNotContain(otherKey, keys);
    }

    [PostgresFact]
    public async Task Skips_rows_with_malformed_payload_instead_of_throwing()
    {
        var (subjectId, topicId, _) = await PickTopicsAsync();
        var userId = await AiTestData.CreateUserAsync(fixture);
        var suffix = Guid.NewGuid().ToString("N");
        // Valid JSON but malformed payloads: no "options", a null option text, options that are not an array.
        await InsertQuestionAsync(userId, topicId, "Draft", "Broken stem " + suffix, "{}");
        await InsertQuestionAsync(userId, topicId, "Draft", "Null text stem " + suffix,
            """{"options":[{"label":"A","text":null},{"label":"B","text":"b"},{"label":"C","text":"c"},{"label":"D","text":"d"}],"correctLabel":"A"}""");
        await InsertQuestionAsync(userId, topicId, "Draft", "Object options stem " + suffix, """{"options":{"label":"A"}}""");
        var validKey = await AddQuestionAsync(userId, topicId, "Draft", "Valid stem " + suffix, PayloadFor("v" + suffix));

        var keys = await GetKeysAsync(subjectId, topicId);

        Assert.Contains(validKey, keys);
    }
}
