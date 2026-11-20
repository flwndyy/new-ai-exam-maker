using ExamMaker.Application.Ai;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamMaker.Infrastructure.Ai;

public sealed class EfAiUsageReader(ExamMakerDbContext db) : IAiUsageReader
{
    public async Task<AiUsage> GetUserUsageAsync(Guid userId, DateOnly date, CancellationToken ct)
    {
        var usage = await db.AiUsageDaily
            .AsNoTracking()
            .Where(u => u.UserId == userId && u.Date == date)
            .Select(u => new AiUsage(u.Jobs, u.Tokens))
            .SingleOrDefaultAsync(ct);

        return usage ?? new AiUsage(0, 0);
    }

    public Task<long> GetSystemTokensAsync(DateOnly date, CancellationToken ct) =>
        db.AiUsageDaily
            .AsNoTracking()
            .Where(u => u.Date == date)
            .SumAsync(u => u.Tokens, ct);
}
