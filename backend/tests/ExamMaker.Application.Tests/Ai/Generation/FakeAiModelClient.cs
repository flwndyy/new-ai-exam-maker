using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Tests.Ai.Generation;

/// <summary>Scripted stand-in for Bedrock: replays queued responses or exceptions and records every request.</summary>
internal sealed class FakeAiModelClient : IAiModelClient
{
    public const string ModelId = "fake-model";

    private readonly Queue<Func<AiModelResponse>> _script = new();

    public List<AiModelRequest> Requests { get; } = [];

    public List<CancellationToken> Tokens { get; } = [];

    public FakeAiModelClient Returns(string text, AiStopReason stopReason = AiStopReason.EndTurn,
        long inputTokens = 100, long outputTokens = 50)
    {
        _script.Enqueue(() => new AiModelResponse(text, stopReason, inputTokens, outputTokens, ModelId));
        return this;
    }

    public FakeAiModelClient Throws(Exception exception)
    {
        _script.Enqueue(() => throw exception);
        return this;
    }

    public Task<AiModelResponse> InvokeAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Tokens.Add(cancellationToken);
        if (_script.Count == 0) throw new InvalidOperationException("Fake model script exhausted.");
        return Task.FromResult(_script.Dequeue()());
    }
}
