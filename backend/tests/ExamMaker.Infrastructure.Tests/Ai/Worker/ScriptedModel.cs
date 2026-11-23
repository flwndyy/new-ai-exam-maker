using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Infrastructure.Tests.Ai.Worker;

/// <summary>
/// Fake Bedrock that plays one scripted step per call. A call beyond the script throws, so a stray call can never pass
/// silently (the answer verifier swallows model errors, which would otherwise hide it as NotChecked).
/// </summary>
internal sealed class ScriptedModel(params Func<AiModelRequest, Task<AiModelResponse>>[] steps) : IAiModelClient
{
    public const int TokensPerCall = 20;

    private int calls;

    public int Calls => Volatile.Read(ref calls);

    public Task<AiModelResponse> InvokeAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref calls) - 1;
        if (index >= steps.Length) throw new InvalidOperationException("Unexpected model call");
        return steps[index](request);
    }

    /// <summary>Valid output with <paramref name="count"/> MultipleChoice questions; 10 input + 10 output tokens.</summary>
    public static Func<AiModelRequest, Task<AiModelResponse>> Valid(int count) =>
        _ => Task.FromResult(new AiModelResponse(AiTestData.ModelOutput(count), AiStopReason.EndTurn, 10, 10, "fake-model"));

    public static Func<AiModelRequest, Task<AiModelResponse>> Fail(AiModelErrorKind kind) =>
        _ => throw new AiModelException(kind, "Simulated Bedrock failure.");

    /// <summary>Signals <see cref="ModelGate.Entered"/>, then waits for <see cref="ModelGate.Release"/> before answering.</summary>
    public static Func<AiModelRequest, Task<AiModelResponse>> Gated(ModelGate gate, int count) => async request =>
    {
        gate.SignalEntered();
        await gate.Released;
        return await Valid(count)(request);
    };
}

internal sealed class ModelGate
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => entered.Task;

    internal Task Released => released.Task;

    internal void SignalEntered() => entered.TrySetResult();

    public void Release() => released.TrySetResult();
}
