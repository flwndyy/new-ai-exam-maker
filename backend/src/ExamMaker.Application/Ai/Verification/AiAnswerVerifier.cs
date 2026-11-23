using System.Text.Json;
using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Ai.Verification;

/// <summary>
/// Optional second model call that solves a batch of generated questions blind and compares the result
/// with the stored answers (docs/context.md 4.1.2). Verification is advisory: any failure of the model call and
/// any unusable output just leave the questions unverified. Only cancellation of the caller's token and
/// exceptions from <c>onModelCall</c> (for example quota exceeded) propagate.
/// </summary>
public sealed class AiAnswerVerifier(IAiModelClient client, AiGenerationOptions options)
{
    /// <param name="onModelCall">
    /// Invoked after the call completes (even when its output is rejected) so the worker can record token usage.
    /// Exceptions from it propagate, for example when the daily token quota is exceeded.
    /// </param>
    /// <returns>An outcome for every input ordinal.</returns>
    public async Task<IReadOnlyDictionary<int, AiVerificationOutcome>> VerifyAsync(string type,
        IReadOnlyList<AiGeneratedQuestion> questions, Func<AiModelResponse, CancellationToken, Task>? onModelCall,
        CancellationToken cancellationToken)
    {
        if (!options.VerifyAnswers || type == "Essay" || questions.Count == 0)
            return All(questions, new AiVerificationOutcome(AiVerificationValues.NotChecked, null));

        var unavailable = new AiVerificationOutcome(AiVerificationValues.NotChecked, AiVerificationNotes.Unavailable);
        var prompt = AiVerificationPromptBuilder.Build(type, questions);
        var request = new AiModelRequest(prompt.System, [new AiChatMessage(AiChatRole.User, prompt.User)],
            options.VerifyMaxOutputTokens, 0);

        AiModelResponse response;
        try
        {
            response = await client.InvokeAsync(request, cancellationToken);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A shutdown that an adapter mapped to another exception type must still stop the worker.
            cancellationToken.ThrowIfCancellationRequested();
            return All(questions, unavailable);
        }

        if (onModelCall is not null) await onModelCall(response, cancellationToken);

        if (response.StopReason is AiStopReason.MaxTokens or AiStopReason.ContentFiltered)
            return All(questions, unavailable);

        IReadOnlyDictionary<int, JsonElement> answers;
        try
        {
            answers = AiVerificationParser.Parse(response.Text, type, questions.Select(q => q.Ordinal).ToArray());
        }
        catch (AiOutputException)
        {
            return All(questions, unavailable);
        }

        var outcomes = new Dictionary<int, AiVerificationOutcome>();
        foreach (var question in questions)
        {
            outcomes[question.Ordinal] = answers.TryGetValue(question.Ordinal, out var answer)
                ? AiAnswerComparer.Compare(type, question.Payload, answer)
                : new AiVerificationOutcome(AiVerificationValues.NotChecked, AiVerificationNotes.Unanswered);
        }

        return outcomes;
    }

    private static IReadOnlyDictionary<int, AiVerificationOutcome> All(IReadOnlyList<AiGeneratedQuestion> questions,
        AiVerificationOutcome outcome) => questions.ToDictionary(q => q.Ordinal, _ => outcome);
}
