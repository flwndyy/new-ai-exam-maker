using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Verification;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Application.Tests.Ai.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ExamMaker.Application.Tests.Ai.Worker;

public sealed class AiJobProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public Harness()
        {
            Store = new FakeAiJobWorkStore(Log);
            Message = new FakeAiQueueMessage(Log);
        }

        public CallLog Log { get; } = new();
        public FakeAiJobWorkStore Store { get; }
        public FakeAiQueueMessage Message { get; }
        public FakeAiModelClient Model { get; } = new();
        public IAiModelClient? ModelOverride { get; set; }
        public FakeDuplicateKeySource Keys { get; } = new();
        public FakeTimeProvider Time { get; } = new(Now);
        public AiGenerationOptions Generation { get; } = new();
        public AiQuotaOptions Quota { get; } = new();
        public long BaseTokens { get; set; }

        public Task<AiJobOutcome> RunAsync(CancellationToken ct = default) =>
            new AiJobProcessor(Store, Keys, ModelOverride ?? Model, new LiveUsageReader(Store, BaseTokens), Quota,
                    Generation, new AiWorkerOptions(), Time, NullLogger<AiJobProcessor>.Instance)
                .ProcessAsync(FakeAiJobWorkStore.JobId, Message, ct);
    }

    private static string Stem(int i) => $"Câu hỏi số {i}?";

    private static string Payload(int i) =>
        $$"""{"options":[{"label":"A","text":"a{{i}}"},{"label":"B","text":"b{{i}}"},{"label":"C","text":"c{{i}}"},{"label":"D","text":"d{{i}}"}],"correctLabel":"B"}""";

    private static string Output(int count) =>
        "{\"questions\":[" + string.Join(",", Enumerable.Range(1, count)
            .Select(i => $$"""{"stem":"{{Stem(i)}}","payload":{{Payload(i)}},"explanation":null}""")) + "]}";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Happy_path_completes_and_returns_Delete()
    {
        var h = new Harness();
        h.Model.Returns(Output(2));

        var outcome = await h.RunAsync();

        Assert.IsType<AiJobOutcome.Delete>(outcome);
        Assert.Equal([1, 2], h.Store.CompletedQuestions!.Select(q => q.Question.Ordinal));
        Assert.Equal(0, h.Store.CompletedDropped);
        Assert.All(h.Store.CompletedQuestions!, q => Assert.Equal(AiVerificationValues.NotChecked, q.Verification.Verification));
        Assert.Single(h.Store.Writes);
    }

    [Fact]
    public async Task Passes_dropped_count_and_verification_to_complete()
    {
        var h = new Harness();
        h.Generation.VerifyAnswers = true;
        using var first = JsonDocument.Parse(Payload(1));
        h.Keys.Keys = new HashSet<string> { AiDuplicateFilter.Key("MultipleChoice", Stem(1), first.RootElement) };
        h.Model.Returns(Output(2)).Returns("""{"answers":[{"ordinal":2,"answer":"D"}]}""");

        await h.RunAsync();

        Assert.Equal(1, h.Store.CompletedDropped);
        var kept = Assert.Single(h.Store.CompletedQuestions!);
        Assert.Equal(2, kept.Question.Ordinal);
        Assert.Equal(new AiVerificationOutcome("Disagree", "Mô hình tự giải chọn D, đáp án gốc là B."), kept.Verification);
    }

    [Fact]
    public async Task Records_usage_after_every_model_call_with_current_utc_date()
    {
        var h = new Harness();
        h.Generation.VerifyAnswers = true;
        h.Model.Returns("not json").Returns(Output(2))
            .Returns("""{"answers":[{"ordinal":1,"answer":"B"},{"ordinal":2,"answer":"B"}]}""");

        await h.RunAsync();

        Assert.Equal(3, h.Model.Requests.Count);
        Assert.Equal(Enumerable.Repeat(new DateOnly(2026, 11, 23), 3), h.Store.UsageDates);
        Assert.Equal(450, h.Store.RecordedTokens);
    }

    [Fact]
    public async Task Quota_exceeded_before_generation_fails_job_with_QuotaExceeded()
    {
        var h = new Harness { BaseTokens = 100_000 };

        var outcome = await h.RunAsync();

        Assert.IsType<AiJobOutcome.Delete>(outcome);
        Assert.Empty(h.Model.Requests);
        var write = Assert.Single(h.Store.Writes);
        Assert.Equal(new StoreWrite("Fail", "QuotaExceeded",
            "Đã hết hạn mức token AI trong ngày, hãy thử lại vào ngày mai.", false), write);
    }

    [Fact]
    public async Task Quota_exceeded_before_verification_keeps_questions_as_NotChecked()
    {
        var h = new Harness();
        h.Generation.VerifyAnswers = true;
        h.Quota.TokensPerUserPerDay = 150;
        h.Model.Returns(Output(2), inputTokens: 100, outputTokens: 50);

        var outcome = await h.RunAsync();

        Assert.IsType<AiJobOutcome.Delete>(outcome);
        Assert.Single(h.Model.Requests);
        Assert.Equal(2, h.Store.CompletedQuestions!.Count);
        Assert.All(h.Store.CompletedQuestions!, q => Assert.Equal(AiVerificationValues.NotChecked, q.Verification.Verification));
    }

    [Theory]
    [InlineData(AiClaimMissReason.NotFound)]
    [InlineData(AiClaimMissReason.Finished)]
    public async Task Missing_or_finished_job_deletes_message(AiClaimMissReason reason)
    {
        var h = new Harness();
        h.Store.Claim = null;
        h.Store.Miss = new AiClaimMiss(reason, TimeSpan.Zero);

        Assert.IsType<AiJobOutcome.Delete>(await h.RunAsync());
        Assert.Empty(h.Store.Writes);
    }

    [Theory]
    [InlineData(40, 45)]
    [InlineData(298, 300)]
    public async Task Job_held_by_other_worker_retries_after_remaining_lease(int remainingSeconds, int expectedDelay)
    {
        var h = new Harness();
        h.Store.Claim = null;
        h.Store.Miss = new AiClaimMiss(AiClaimMissReason.HeldByOther, TimeSpan.FromSeconds(remainingSeconds));

        var outcome = await h.RunAsync();

        Assert.Equal(new AiJobOutcome.RetryAfter(TimeSpan.FromSeconds(expectedDelay)), outcome);
    }

    [Fact]
    public async Task Exhausted_job_is_failed_and_message_deleted()
    {
        var h = new Harness();
        h.Store.Claim = null;
        h.Store.Miss = new AiClaimMiss(AiClaimMissReason.AttemptsExhausted, TimeSpan.Zero);

        Assert.IsType<AiJobOutcome.Delete>(await h.RunAsync());

        Assert.Equal(new StoreWrite("FailExhausted", null,
            "Đã thử lại nhiều lần nhưng không xử lý được yêu cầu.", false), Assert.Single(h.Store.Writes));
    }

    [Theory]
    [InlineData("RequesterUnavailable")]
    [InlineData("InvalidRequest")]
    public async Task Load_failure_fails_job_with_its_code(string code)
    {
        var h = new Harness();
        h.Store.Load = new AiJobContextLoad(null, code);

        Assert.IsType<AiJobOutcome.Delete>(await h.RunAsync());

        Assert.Empty(h.Model.Requests);
        Assert.Equal(("Fail", code), (Assert.Single(h.Store.Writes).Method, h.Store.Writes[0].Code));
    }

    [Fact]
    public async Task Invalid_request_fields_fail_job_with_InvalidRequest()
    {
        var h = new Harness();
        h.Store.Load = new AiJobContextLoad(
            new AiJobContext(FakeAiJobWorkStore.JobId, FakeAiJobWorkStore.RequesterId, FakeAiJobWorkStore.DepartmentId,
                FakeAiJobWorkStore.Request(count: 0), "Toán", "Mệnh đề"), null);

        Assert.IsType<AiJobOutcome.Delete>(await h.RunAsync());

        Assert.Empty(h.Model.Requests);
        Assert.Equal("InvalidRequest", Assert.Single(h.Store.Writes).Code);
    }

    [Fact]
    public async Task Retryable_error_releases_job_and_returns_backoff()
    {
        var h = new Harness();
        h.Store.Claim = new AiJobClaim(FakeAiJobWorkStore.JobId, 2, 3);
        h.Model.Throws(new AiModelException(AiModelErrorKind.Throttled, "x"));

        var outcome = await h.RunAsync();

        Assert.Equal(new AiJobOutcome.RetryAfter(TimeSpan.FromSeconds(120)), outcome);
        Assert.Equal(new StoreWrite("ReleaseForRetry", "Throttled",
            "Dịch vụ AI đang quá tải, hệ thống sẽ thử lại.", false), Assert.Single(h.Store.Writes));
    }

    [Fact]
    public async Task Retryable_error_on_last_attempt_fails_job()
    {
        var h = new Harness();
        h.Store.Claim = new AiJobClaim(FakeAiJobWorkStore.JobId, 3, 3);
        h.Model.Throws(new AiModelException(AiModelErrorKind.Throttled, "x"));

        Assert.IsType<AiJobOutcome.Delete>(await h.RunAsync());

        // The code keeps the cause; the message must not promise a retry that will never happen.
        Assert.Equal(new StoreWrite("Fail", "Throttled",
            "Đã thử lại nhiều lần nhưng không xử lý được yêu cầu.", false), Assert.Single(h.Store.Writes));
    }

    [Fact]
    public async Task Permanent_error_fails_job()
    {
        var h = new Harness();
        h.Model.Returns("not json").Returns("still not json");

        Assert.IsType<AiJobOutcome.Delete>(await h.RunAsync());

        Assert.Equal(("Fail", "InvalidJson"), (Assert.Single(h.Store.Writes).Method, h.Store.Writes[0].Code));
    }

    [Fact]
    public async Task Duplicate_key_source_db_error_is_retryable()
    {
        var h = new Harness();
        h.Model.Returns(Output(2));
        h.Keys.Throws = new InvalidOperationException("db down");

        var outcome = await h.RunAsync();

        Assert.Equal(new AiJobOutcome.RetryAfter(TimeSpan.FromSeconds(30)), outcome);
        Assert.Equal(("ReleaseForRetry", "Unknown"), (Assert.Single(h.Store.Writes).Method, h.Store.Writes[0].Code));
    }

    [Fact]
    public async Task Complete_RequesterUnavailable_fails_job()
    {
        var h = new Harness();
        h.Model.Returns(Output(2));
        h.Store.CompleteResult = AiCompleteResult.RequesterUnavailable;

        Assert.IsType<AiJobOutcome.Delete>(await h.RunAsync());

        Assert.Equal(("Fail", "RequesterUnavailable"), (h.Store.Writes[1].Method, h.Store.Writes[1].Code));
    }

    [Fact]
    public async Task Complete_LeaseLost_returns_Abandon()
    {
        var h = new Harness();
        h.Model.Returns(Output(2));
        h.Store.CompleteResult = AiCompleteResult.LeaseLost;

        Assert.IsType<AiJobOutcome.Abandon>(await h.RunAsync());
        Assert.Single(h.Store.Writes);
    }

    [Fact]
    public async Task Fenced_write_returning_false_returns_Abandon()
    {
        var h = new Harness();
        h.Model.Throws(new AiModelException(AiModelErrorKind.Throttled, "x"));
        h.Store.WriteResult = false;

        Assert.IsType<AiJobOutcome.Abandon>(await h.RunAsync());
    }

    [Fact]
    public async Task Stopping_token_releases_on_shutdown_and_requeues_with_a_fresh_message()
    {
        var h = new Harness();
        var model = new BlockingModelClient(h.Model.Returns(Output(2)));
        h.ModelOverride = model;
        using var stopping = new CancellationTokenSource();

        var run = h.RunAsync(stopping.Token);
        await model.Entered;
        await stopping.CancelAsync();

        Assert.IsType<AiJobOutcome.Requeue>(await run);
        Assert.Equal(new StoreWrite("ReleaseOnShutdown", null, null, false), Assert.Single(h.Store.Writes));
    }

    [Fact]
    public async Task Job_timeout_is_treated_as_retryable_Timeout()
    {
        var h = new Harness();
        var model = new BlockingModelClient(h.Model.Returns(Output(2)));
        h.ModelOverride = model;

        var run = h.RunAsync();
        await model.Entered;
        h.Time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(new AiJobOutcome.RetryAfter(TimeSpan.FromSeconds(30)), await run);
        Assert.Equal(("ReleaseForRetry", "Timeout"), (Assert.Single(h.Store.Writes).Method, h.Store.Writes[0].Code));
    }

    [Fact]
    public async Task Heartbeat_extends_lease_then_visibility_every_60_seconds()
    {
        var h = new Harness();
        var model = new BlockingModelClient(h.Model.Returns(Output(2)));
        h.ModelOverride = model;

        var run = h.RunAsync();
        await model.Entered;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => h.Log.Entries.Count == 2);
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => h.Log.Entries.Count == 4);
        model.Release();

        Assert.IsType<AiJobOutcome.Delete>(await run);
        Assert.Equal(["ExtendLease", "ChangeVisibility(300)", "ExtendLease", "ChangeVisibility(300)"], h.Log.Entries);
    }

    [Fact]
    public async Task Lost_lease_cancels_processing_and_returns_Abandon()
    {
        var h = new Harness();
        var model = new BlockingModelClient(h.Model.Returns(Output(2)));
        h.ModelOverride = model;
        h.Store.ExtendResult = false;

        var run = h.RunAsync();
        await model.Entered;
        h.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.IsType<AiJobOutcome.Abandon>(await run);
        Assert.Empty(h.Store.Writes);
        Assert.Equal(0, h.Log.Count("ChangeVisibility"));
    }

    [Fact]
    public async Task Visibility_failure_does_not_stop_processing()
    {
        var h = new Harness();
        var model = new BlockingModelClient(h.Model.Returns(Output(2)));
        h.ModelOverride = model;
        h.Message.VisibilityFailuresLeft = 1;

        var run = h.RunAsync();
        await model.Entered;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => h.Log.Count("ChangeVisibility") == 1);
        model.Release();

        Assert.IsType<AiJobOutcome.Delete>(await run);
        Assert.Equal("Complete", Assert.Single(h.Store.Writes).Method);
    }
}
