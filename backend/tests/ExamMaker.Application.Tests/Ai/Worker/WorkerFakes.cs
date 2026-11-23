using ExamMaker.Application.Ai;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Application.Ai.Worker;
using ExamMaker.Application.Tests.Ai.Generation;

namespace ExamMaker.Application.Tests.Ai.Worker;

/// <summary>Thread-safe ordered record of calls made by the processor and its heartbeat.</summary>
internal sealed class CallLog
{
    private readonly List<string> _entries = [];

    public void Add(string entry)
    {
        lock (_entries) _entries.Add(entry);
    }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public int Count(string prefix) => Entries.Count(e => e.StartsWith(prefix, StringComparison.Ordinal));
}

internal sealed record StoreWrite(string Method, string? Code, string? Message, bool TokenCancelled);

internal sealed class FakeAiJobWorkStore(CallLog log) : IAiJobWorkStore
{
    public static readonly Guid JobId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid RequesterId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid DepartmentId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    public static AiGenerateRequest Request(int count = 2, string type = "MultipleChoice") => new(
        AiGenerationSpecs.SubjectId, AiGenerationSpecs.TopicId, 10, type, "Understanding", count, null);

    public AiJobClaim? Claim { get; set; } = new(JobId, 1, 3);
    public AiClaimMiss Miss { get; set; } = new(AiClaimMissReason.NotFound, TimeSpan.Zero);
    public AiJobContextLoad Load { get; set; } =
        new(new AiJobContext(JobId, RequesterId, DepartmentId, Request(), "Toán", "Mệnh đề"), null);
    public bool ExtendResult { get; set; } = true;
    public bool WriteResult { get; set; } = true;
    public AiCompleteResult CompleteResult { get; set; } = AiCompleteResult.Completed;

    public long RecordedTokens { get; private set; }
    public List<DateOnly> UsageDates { get; } = [];
    public List<StoreWrite> Writes { get; } = [];
    public IReadOnlyList<AiStoredQuestion>? CompletedQuestions { get; private set; }
    public int? CompletedDropped { get; private set; }

    public Task<AiJobClaim?> TryClaimAsync(Guid jobId, TimeSpan lease, string promptVersion, CancellationToken ct) =>
        Task.FromResult(Claim);

    public Task<AiClaimMiss> DescribeMissAsync(Guid jobId, CancellationToken ct) => Task.FromResult(Miss);

    public Task<AiJobContextLoad> LoadContextAsync(AiJobClaim claim, CancellationToken ct) => Task.FromResult(Load);

    public Task<bool> ExtendLeaseAsync(AiJobClaim claim, TimeSpan lease, CancellationToken ct)
    {
        log.Add("ExtendLease");
        return Task.FromResult(ExtendResult);
    }

    public Task RecordUsageAsync(Guid jobId, Guid userId, DateOnly usageDate, AiModelResponse response, CancellationToken ct)
    {
        lock (UsageDates)
        {
            UsageDates.Add(usageDate);
            RecordedTokens += response.InputTokens + response.OutputTokens;
        }

        return Task.CompletedTask;
    }

    public Task<bool> ReleaseForRetryAsync(AiJobClaim claim, string code, string message, CancellationToken ct) =>
        Write("ReleaseForRetry", code, message, ct);

    public Task<bool> ReleaseOnShutdownAsync(AiJobClaim claim, CancellationToken ct) =>
        Write("ReleaseOnShutdown", null, null, ct);

    public Task<bool> FailAsync(AiJobClaim claim, string code, string message, CancellationToken ct) =>
        Write("Fail", code, message, ct);

    public Task FailExhaustedAsync(Guid jobId, string message, CancellationToken ct) =>
        Write("FailExhausted", null, message, ct);

    public Task<AiCompleteResult> CompleteAsync(AiJobClaim claim, AiJobContext context,
        IReadOnlyList<AiStoredQuestion> questions, int duplicatesDropped, CancellationToken ct)
    {
        Writes.Add(new StoreWrite("Complete", null, null, ct.IsCancellationRequested));
        CompletedQuestions = questions;
        CompletedDropped = duplicatesDropped;
        return Task.FromResult(CompleteResult);
    }

    private Task<bool> Write(string method, string? code, string? message, CancellationToken ct)
    {
        Writes.Add(new StoreWrite(method, code, message, ct.IsCancellationRequested));
        return Task.FromResult(WriteResult);
    }
}

internal sealed class FakeAiQueueMessage(CallLog log, string body = "") : IAiQueueMessage
{
    public string Body { get; } = body;
    public int VisibilityFailuresLeft { get; set; }
    public bool ThrowOnDelete { get; set; }
    public List<(TimeSpan Visibility, bool TokenCancelled)> VisibilityChanges { get; } = [];
    public List<bool> Deletes { get; } = [];

    public Task ChangeVisibilityAsync(TimeSpan visibility, CancellationToken ct)
    {
        log.Add($"ChangeVisibility({visibility.TotalSeconds})");
        lock (VisibilityChanges) VisibilityChanges.Add((visibility, ct.IsCancellationRequested));
        if (VisibilityFailuresLeft > 0)
        {
            VisibilityFailuresLeft--;
            throw new InvalidOperationException("sqs down");
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        Deletes.Add(ct.IsCancellationRequested);
        if (ThrowOnDelete) throw new InvalidOperationException("sqs down");
        return Task.CompletedTask;
    }
}

/// <summary>Usage reader whose token count follows what the fake store has recorded, plus a fixed offset.</summary>
internal sealed class LiveUsageReader(FakeAiJobWorkStore store, long baseTokens = 0) : IAiUsageReader
{
    public Task<AiUsage> GetUserUsageAsync(Guid userId, DateOnly date, CancellationToken ct) =>
        Task.FromResult(new AiUsage(1, baseTokens + store.RecordedTokens));

    public Task<long> GetSystemTokensAsync(DateOnly date, CancellationToken ct) =>
        Task.FromResult(baseTokens + store.RecordedTokens);
}

internal sealed class FakeDuplicateKeySource : IAiDuplicateKeySource
{
    public IReadOnlySet<string> Keys { get; set; } = new HashSet<string>();
    public Exception? Throws { get; set; }

    public Task<IReadOnlySet<string>> GetKeysAsync(Guid subjectId, Guid topicId, string type, CancellationToken ct) =>
        Throws is null ? Task.FromResult(Keys) : Task.FromException<IReadOnlySet<string>>(Throws);
}

/// <summary>Model whose first call blocks until <see cref="Release"/> (or cancellation), then defers to the script.</summary>
internal sealed class BlockingModelClient(FakeAiModelClient inner) : IAiModelClient
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public async Task<AiModelResponse> InvokeAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return await inner.InvokeAsync(request, cancellationToken);
    }
}
