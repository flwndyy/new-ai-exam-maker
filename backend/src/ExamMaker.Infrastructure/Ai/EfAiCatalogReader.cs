using ExamMaker.Application.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Ai;

public sealed class EfAiCatalogReader(ExamMakerDbContext db) : IAiCatalogReader
{
    public Task<Guid?> FindSubjectDepartmentAsync(Guid subjectId, CancellationToken ct) =>
        db.Subjects
            .AsNoTracking()
            .Where(s => s.Id == subjectId)
            .Select(s => (Guid?)s.DepartmentId)
            .SingleOrDefaultAsync(ct);

    public Task<AiTopicInfo?> FindTopicAsync(Guid topicId, CancellationToken ct) =>
        db.Topics
            .AsNoTracking()
            .Where(t => t.Id == topicId)
            .Select(t => new AiTopicInfo(t.SubjectId, t.Grade))
            .SingleOrDefaultAsync(ct);
}
