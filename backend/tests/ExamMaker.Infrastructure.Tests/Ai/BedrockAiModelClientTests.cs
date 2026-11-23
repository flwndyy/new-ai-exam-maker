using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Infrastructure.Ai;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class BedrockAiModelClientTests
{
    private static readonly AiModelRequest Request = new("sys",
        [new AiChatMessage(AiChatRole.User, "u1"), new AiChatMessage(AiChatRole.Assistant, "a1"), new AiChatMessage(AiChatRole.User, "u2")],
        512, 0.5);

    private sealed class FakeBedrock(Func<ConverseRequest, CancellationToken, Task<ConverseResponse>> handler)
        : AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.APSoutheast1)
    {
        public override Task<ConverseResponse> ConverseAsync(ConverseRequest request, CancellationToken cancellationToken = default) =>
            handler(request, cancellationToken);
    }

    private static BedrockAiModelClient Create(
        Func<ConverseRequest, CancellationToken, Task<ConverseResponse>> handler, int timeoutSeconds = 120) =>
        new(new FakeBedrock(handler), new BedrockOptions { ModelId = "model-x", TimeoutSeconds = timeoutSeconds });

    [Fact]
    public async Task Sends_system_messages_and_inference_config()
    {
        ConverseRequest? sent = null;
        var client = Create((r, _) =>
        {
            sent = r;
            return Task.FromResult(new ConverseResponse { StopReason = StopReason.End_turn });
        });

        await client.InvokeAsync(Request, CancellationToken.None);

        Assert.Equal("model-x", sent!.ModelId);
        Assert.Equal("sys", Assert.Single(sent.System).Text);
        Assert.Equal(["user", "assistant", "user"], sent.Messages.Select(m => m.Role.Value));
        Assert.Equal(["u1", "a1", "u2"], sent.Messages.Select(m => Assert.Single(m.Content).Text));
        Assert.Equal(512, sent.InferenceConfig.MaxTokens);
        Assert.Equal(0.5f, sent.InferenceConfig.Temperature);
    }

    [Fact]
    public async Task Maps_response_text_stop_reason_tokens_and_model()
    {
        var client = Create((_, _) => Task.FromResult(new ConverseResponse
        {
            Output = new ConverseOutput
            {
                Message = new Message { Content = [new ContentBlock { Text = "{\"a\":" }, new ContentBlock { Text = "1}" }] },
            },
            StopReason = StopReason.Max_tokens,
            Usage = new TokenUsage { InputTokens = 11, OutputTokens = 22 },
        }));

        var response = await client.InvokeAsync(Request, CancellationToken.None);

        Assert.Equal(new AiModelResponse("{\"a\":1}", AiStopReason.MaxTokens, 11, 22, "model-x"), response);
    }

    [Fact]
    public async Task Provider_error_is_mapped()
    {
        var client = Create((_, _) => throw new ThrottlingException("slow down"));

        var error = await Assert.ThrowsAsync<AiModelException>(() => client.InvokeAsync(Request, CancellationToken.None));

        Assert.Equal(AiModelErrorKind.Throttled, error.Kind);
    }

    [Fact]
    public async Task Per_call_timeout_becomes_Timeout_error()
    {
        var client = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ConverseResponse();
        }, timeoutSeconds: 1);

        var error = await Assert.ThrowsAsync<AiModelException>(() => client.InvokeAsync(Request, CancellationToken.None));

        Assert.Equal(AiModelErrorKind.Timeout, error.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var client = Create(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new ConverseResponse();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InvokeAsync(Request, cts.Token));
    }

    [Fact]
    public void Missing_model_id_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new BedrockAiModelClient(new FakeBedrock((_, _) => Task.FromResult(new ConverseResponse())), new BedrockOptions()));
    }
}
