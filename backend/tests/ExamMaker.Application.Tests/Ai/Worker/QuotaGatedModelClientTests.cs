using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Application.Tests.Ai.Generation;
using Microsoft.Extensions.Time.Testing;

namespace ExamMaker.Application.Tests.Ai.Worker;

public sealed class QuotaGatedModelClientTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly AiModelRequest Request = new("system", [new AiChatMessage(AiChatRole.User, "hi")], 100, 0.7);

    private static (QuotaGatedModelClient Client, FakeAiModelClient Inner, FakeUsageReader Usage) Create(
        long userTokens, long systemTokens, DateTimeOffset? now = null)
    {
        var inner = new FakeAiModelClient().Returns("{}");
        var usage = new FakeUsageReader { UserTokens = userTokens, SystemTokens = systemTokens };
        var time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero));
        return (new QuotaGatedModelClient(inner, usage, new AiQuotaOptions(), time, UserId), inner, usage);
    }

    [Fact]
    public async Task Calls_inner_when_under_both_limits()
    {
        var (client, inner, _) = Create(userTokens: 99_999, systemTokens: 499_999);

        var response = await client.InvokeAsync(Request, CancellationToken.None);

        Assert.Single(inner.Requests);
        Assert.Equal("{}", response.Text);
        Assert.Equal(FakeAiModelClient.ModelId, response.ModelId);
    }

    [Fact]
    public async Task Throws_without_calling_inner_when_user_tokens_reach_limit()
    {
        var (client, inner, _) = Create(userTokens: 100_000, systemTokens: 0);

        await Assert.ThrowsAsync<AiQuotaExceededException>(() => client.InvokeAsync(Request, CancellationToken.None));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Throws_without_calling_inner_when_system_tokens_reach_limit()
    {
        var (client, inner, _) = Create(userTokens: 0, systemTokens: 500_000);

        await Assert.ThrowsAsync<AiQuotaExceededException>(() => client.InvokeAsync(Request, CancellationToken.None));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Reads_usage_for_the_current_utc_day()
    {
        var (client, _, usage) = Create(0, 0, new DateTimeOffset(2026, 11, 24, 0, 0, 5, TimeSpan.Zero));

        await client.InvokeAsync(Request, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 11, 24), usage.UserDate);
        Assert.Equal(UserId, usage.UserIdAsked);
        Assert.Equal(new DateOnly(2026, 11, 24), usage.SystemDate);
    }

    private sealed class FakeUsageReader : IAiUsageReader
    {
        public long UserTokens { get; init; }
        public long SystemTokens { get; init; }
        public DateOnly? UserDate { get; private set; }
        public DateOnly? SystemDate { get; private set; }
        public Guid? UserIdAsked { get; private set; }

        public Task<AiUsage> GetUserUsageAsync(Guid userId, DateOnly date, CancellationToken ct)
        {
            UserIdAsked = userId;
            UserDate = date;
            return Task.FromResult(new AiUsage(0, UserTokens));
        }

        public Task<long> GetSystemTokensAsync(DateOnly date, CancellationToken ct)
        {
            SystemDate = date;
            return Task.FromResult(SystemTokens);
        }
    }
}
