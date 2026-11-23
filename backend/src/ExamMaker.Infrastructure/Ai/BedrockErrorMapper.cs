using Amazon.BedrockRuntime.Model;
using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>
/// Maps Bedrock SDK failures and stop reasons onto the provider-neutral types of B3. The provider message is never
/// copied (it may echo request content), only the exception type name.
/// </summary>
public static class BedrockErrorMapper
{
    /// <summary>Returns null for exceptions that are not provider errors; the caller rethrows those unchanged.</summary>
    public static AiModelException? Map(Exception exception)
    {
        AiModelErrorKind? kind = exception switch
        {
            ThrottlingException => AiModelErrorKind.Throttled,
            ModelTimeoutException => AiModelErrorKind.Timeout,
            ServiceUnavailableException or InternalServerException or ModelNotReadyException => AiModelErrorKind.ServiceUnavailable,
            AccessDeniedException => AiModelErrorKind.AccessDenied,
            ValidationException or ResourceNotFoundException => AiModelErrorKind.InvalidRequest,
            HttpRequestException or IOException => AiModelErrorKind.Network,
            _ => null,
        };

        return kind is { } value
            ? new AiModelException(value, $"Bedrock call failed ({exception.GetType().Name}).")
            : null;
    }

    public static AiStopReason MapStopReason(string? stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => AiStopReason.EndTurn,
        "max_tokens" => AiStopReason.MaxTokens,
        "content_filtered" or "guardrail_intervened" => AiStopReason.ContentFiltered,
        _ => AiStopReason.Other,
    };
}
