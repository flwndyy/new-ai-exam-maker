using ExamMaker.Application.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Ai;

public sealed class EfAiCatalogReader(ExamMakerDbContext db) : IAiCatalogReader
{
    public Task<bool> SubjectExistsAsync(Guid subjectId, CancellationToken ct) =>
        db.Subjects.AsNoTracking().AnyAsync(s => s.Id == subjectId, ct);

    public Task<AiTopicInfo?> FindTopicAsync(Guid topicId, CancellationToken ct) =>
        db.Topics
            .AsNoTracking()
            .Where(t => t.Id == topicId)
            .Select(t => new AiTopicInfo(t.SubjectId, t.Grade))
            .SingleOrDefaultAsync(ct);
}
