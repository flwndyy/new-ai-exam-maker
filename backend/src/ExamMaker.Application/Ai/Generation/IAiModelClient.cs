namespace ExamMaker.Application.Ai.Generation;

public enum AiChatRole
{
    User,
    Assistant,
}

public sealed record AiChatMessage(AiChatRole Role, string Text);

public sealed record AiModelRequest(string System, IReadOnlyList<AiChatMessage> Messages, int MaxOutputTokens, double Temperature);

public enum AiStopReason
{
    EndTurn,
    MaxTokens,
    ContentFiltered,
    Other,
}

/// <summary>One model call's output and usage; <see cref="ModelId"/> is the configured model that served it.</summary>
public sealed record AiModelResponse(string Text, AiStopReason StopReason, long InputTokens, long OutputTokens, string ModelId);

/// <summary>
/// Provider-neutral text model. The Bedrock implementation (B6) maps SDK failures to <see cref="AiModelException"/>;
/// tests use a scripted fake so CI never calls Bedrock.
/// </summary>
public interface IAiModelClient
{
    Task<AiModelResponse> InvokeAsync(AiModelRequest request, CancellationToken cancellationToken);
}
