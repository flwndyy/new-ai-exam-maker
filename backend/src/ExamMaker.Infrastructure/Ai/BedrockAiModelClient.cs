using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Infrastructure.Ai;

/// <summary>
/// Bedrock Converse adapter for <see cref="IAiModelClient"/>. The SDK client must be built without automatic retries:
/// retries go through SQS redelivery so they are counted and backed off (docs/context.md 4.1.1).
/// </summary>
public sealed class BedrockAiModelClient : IAiModelClient
{
    private readonly IAmazonBedrockRuntime _client;
    private readonly string _modelId;
    private readonly TimeSpan _timeout;

    public BedrockAiModelClient(IAmazonBedrockRuntime client, BedrockOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelId))
            throw new InvalidOperationException($"Configuration value '{BedrockOptions.SectionName}:ModelId' is not set.");
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TimeoutSeconds, 1);

        _client = client;
        _modelId = options.ModelId;
        _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    public async Task<AiModelResponse> InvokeAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        var converse = new ConverseRequest
        {
            ModelId = _modelId,
            System = [new SystemContentBlock { Text = request.System }],
            Messages = request.Messages
                .Select(m => new Message
                {
                    Role = m.Role == AiChatRole.Assistant ? ConversationRole.Assistant : ConversationRole.User,
                    Content = [new ContentBlock { Text = m.Text }],
                })
                .ToList(),
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens = request.MaxOutputTokens,
                Temperature = (float)request.Temperature,
            },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        ConverseResponse response;
        try
        {
            response = await _client.ConverseAsync(converse, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiModelException(AiModelErrorKind.Timeout, "Bedrock call timed out.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && BedrockErrorMapper.Map(ex) is { } mapped)
        {
            throw mapped;
        }

        var text = string.Concat(response.Output?.Message?.Content?.Select(c => c.Text ?? string.Empty) ?? []);
        return new AiModelResponse(
            text,
            BedrockErrorMapper.MapStopReason(response.StopReason?.Value),
            response.Usage?.InputTokens ?? 0,
            response.Usage?.OutputTokens ?? 0,
            _modelId);
    }
}
