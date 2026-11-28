using ExamMaker.Application.Ai;

namespace ExamMaker.Application.Tests.Ai;

public sealed class AiUsageReportQueriesTests
{
    private readonly FakeReader _reader = new();

    private Task<AiUsageReportResult> ListAsync(
        int page = 1, int pageSize = 20, Guid? userId = null, string? from = null, string? to = null) =>
        new AiUsageReportQueries(_reader).ListAsync(page, pageSize, userId, from, to, CancellationToken.None);

    [Fact]
    public async Task Page_below_one_is_invalid()
    {
        var invalid = Assert.IsType<AiUsageReportResult.Invalid>(await ListAsync(page: 0));

        Assert.Contains("page", invalid.Errors.Keys);
        Assert.Null(_reader.LastFilter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Page_size_outside_one_to_hundred_is_invalid(int pageSize)
    {
        var invalid = Assert.IsType<AiUsageReportResult.Invalid>(await ListAsync(pageSize: pageSize));

        Assert.Contains("pageSize", invalid.Errors.Keys);
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("2026/11/01")]
    [InlineData("2026-11-1")]
    [InlineData("2026-11-01T00:00:00Z")]
    public async Task From_must_be_an_exact_yyyy_mm_dd_date(string from)
    {
        var invalid = Assert.IsType<AiUsageReportResult.Invalid>(await ListAsync(from: from));

        Assert.Contains("from", invalid.Errors.Keys);
    }

    [Fact]
    public async Task To_must_be_an_exact_date()
    {
        var invalid = Assert.IsType<AiUsageReportResult.Invalid>(await ListAsync(to: "tomorrow"));

        Assert.Contains("to", invalid.Errors.Keys);
    }

    [Fact]
    public async Task From_after_to_is_invalid()
    {
        var invalid = Assert.IsType<AiUsageReportResult.Invalid>(await ListAsync(from: "2026-11-24", to: "2026-11-23"));

        Assert.Contains("to", invalid.Errors.Keys);
    }

    [Fact]
    public async Task Valid_query_passes_the_parsed_filter_to_the_reader()
    {
        var userId = Guid.NewGuid();

        var ok = Assert.IsType<AiUsageReportResult.Ok>(
            await ListAsync(page: 2, pageSize: 50, userId: userId, from: "2026-11-01", to: "2026-11-23"));

        Assert.Same(_reader.Page, ok.Page);
        Assert.Equal(
            new AiUsageFilter(userId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 23), 2, 50),
            _reader.LastFilter);
    }

    [Fact]
    public async Task Same_day_range_is_valid()
    {
        Assert.IsType<AiUsageReportResult.Ok>(await ListAsync(from: "2026-11-23", to: "2026-11-23"));
    }

    private sealed class FakeReader : IAiUsageReportReader
    {
        public AiUsagePage Page { get; } = new([], 1, 20, 0, 0, new AiUsageTotals(0, 0, 0m));

        public AiUsageFilter? LastFilter { get; private set; }

        public Task<AiUsagePage> ReadAsync(AiUsageFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            return Task.FromResult(Page);
        }
    }
}
