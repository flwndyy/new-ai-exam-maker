using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>
/// Loads every question of the subject and topic into memory and computes its duplicate key (fine at MVP scale).
/// </summary>
public sealed class EfAiDuplicateKeySource(ExamMakerDbContext db) : IAiDuplicateKeySource
{
    public async Task<IReadOnlySet<string>> GetKeysAsync(Guid subjectId, Guid topicId, CancellationToken ct)
    {
        var rows = await db.Questions
            .AsNoTracking()
            .Where(q => q.SubjectId == subjectId && q.TopicId == topicId)
            .Select(q => new { q.Type, q.Stem, q.Payload })
            .ToListAsync(ct);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.Payload);
                keys.Add(AiDuplicateFilter.Key(row.Type, row.Stem, document.RootElement));
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Payloads are validated in the application layer and the DB only enforces NOT NULL, so a malformed
                // row can exist. Skip it: one bad row must not block generation for the whole topic.
            }
        }

        return keys;
    }
}
