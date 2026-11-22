namespace ExamMaker.Application.Ai.Generation;

/// <summary>Generation tuning, bound from configuration section "AiGeneration".</summary>
public sealed class AiGenerationOptions
{
    public const string SectionName = "AiGeneration";

    public int MaxRepairAttempts { get; set; } = 1;

    public int MaxOutputTokens { get; set; } = 8192;

    public double Temperature { get; set; } = 0.7;
}

public sealed record AiGenerationResult(
    IReadOnlyList<AiGeneratedQuestion> Questions,
    string PromptVersion,
    string ModelId,
    long InputTokens,
    long OutputTokens,
    int ModelCalls);

/// <summary>
/// Runs one generation attempt: versioned prompt, model call, validation and up to
/// <see cref="AiGenerationOptions.MaxRepairAttempts"/> repair rounds in the same conversation.
/// Provider exceptions propagate unchanged for <see cref="AiErrorClassifier"/>; unusable output throws
/// <see cref="AiOutputException"/> (non-retryable).
/// </summary>
public sealed class AiQuestionGenerator
{
    private readonly IAiModelClient _client;
    private readonly AiGenerationOptions _options;

    public AiQuestionGenerator(IAiModelClient client, AiGenerationOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRepairAttempts);
        _client = client;
        _options = options;
    }

    /// <param name="onModelCall">
    /// Invoked after every completed call (including rejected output) so the worker can persist token usage per call.
    /// Throwing from it, for example when the daily token quota is exceeded, stops generation.
    /// </param>
    public async Task<AiGenerationResult> GenerateAsync(AiGenerationSpec spec,
        Func<AiModelResponse, CancellationToken, Task>? onModelCall, CancellationToken cancellationToken)
    {
        var prompt = AiPromptBuilder.Build(spec);
        var messages = new List<AiChatMessage> { new(AiChatRole.User, prompt.User) };
        long inputTokens = 0, outputTokens = 0;
        var calls = 0;

        while (true)
        {
            var request = new AiModelRequest(prompt.System, messages.ToArray(), _options.MaxOutputTokens, _options.Temperature);
            var response = await _client.InvokeAsync(request, cancellationToken);
            calls++;
            inputTokens += response.InputTokens;
            outputTokens += response.OutputTokens;
            if (onModelCall is not null) await onModelCall(response, cancellationToken);

            switch (response.StopReason)
            {
                case AiStopReason.ContentFiltered:
                    throw new AiOutputException(AiErrorCodes.ContentRefused, "Model output was filtered.");
                case AiStopReason.MaxTokens:
                    // The same limit would truncate a repaired answer again.
                    throw new AiOutputException(AiErrorCodes.OutputTruncated, "Model output hit the token limit.");
            }

            try
            {
                var questions = AiOutputParser.Parse(response.Text, spec);
                return new AiGenerationResult(questions, prompt.Version, response.ModelId, inputTokens, outputTokens, calls);
            }
            catch (AiOutputException exception) when (calls <= _options.MaxRepairAttempts)
            {
                messages.Add(new AiChatMessage(AiChatRole.Assistant, response.Text));
                messages.Add(new AiChatMessage(AiChatRole.User, AiPromptBuilder.Repair(exception.Message)));
            }
        }
    }
}
