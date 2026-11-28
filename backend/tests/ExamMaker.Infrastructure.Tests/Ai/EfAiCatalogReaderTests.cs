using ExamMaker.Application.Ai;
using ExamMaker.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Tests.Ai;

// Runs against the catalog seeded by the A2 migrations.
public sealed class EfAiCatalogReaderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [PostgresFact]
    public async Task Seeded_subject_returns_its_department()
    {
        await using var context = fixture.CreateContext();
        var subject = await context.Subjects.AsNoTracking().FirstAsync();

        Assert.Equal(
            subject.DepartmentId,
            await new EfAiCatalogReader(context).FindSubjectDepartmentAsync(subject.Id, CancellationToken.None));
    }

    [PostgresFact]
    public async Task Unknown_subject_has_no_department()
    {
        await using var context = fixture.CreateContext();

        Assert.Null(await new EfAiCatalogReader(context).FindSubjectDepartmentAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [PostgresFact]
    public async Task Seeded_topic_returns_its_subject_and_grade()
    {
        await using var context = fixture.CreateContext();
        var topic = await context.Topics.AsNoTracking().OrderBy(t => t.Grade).LastAsync();

        var info = await new EfAiCatalogReader(context).FindTopicAsync(topic.Id, CancellationToken.None);

        Assert.Equal(new AiTopicInfo(topic.SubjectId, topic.Grade), info);
    }

    [PostgresFact]
    public async Task Unknown_topic_is_null()
    {
        await using var context = fixture.CreateContext();

        Assert.Null(await new EfAiCatalogReader(context).FindTopicAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
