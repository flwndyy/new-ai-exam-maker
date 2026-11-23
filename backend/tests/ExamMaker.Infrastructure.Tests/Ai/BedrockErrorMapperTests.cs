using Amazon.BedrockRuntime.Model;
using ExamMaker.Application.Ai.Generation;
using ExamMaker.Infrastructure.Ai;

namespace ExamMaker.Infrastructure.Tests.Ai;

public sealed class BedrockErrorMapperTests
{
    private const string Secret = "secret-detail";

    public static TheoryData<Exception, AiModelErrorKind> ProviderErrors => new()
    {
        { new ThrottlingException(Secret), AiModelErrorKind.Throttled },
        { new ModelTimeoutException(Secret), AiModelErrorKind.Timeout },
        { new ServiceUnavailableException(Secret), AiModelErrorKind.ServiceUnavailable },
        { new InternalServerException(Secret), AiModelErrorKind.ServiceUnavailable },
        { new ModelNotReadyException(Secret), AiModelErrorKind.ServiceUnavailable },
        { new AccessDeniedException(Secret), AiModelErrorKind.AccessDenied },
        { new ValidationException(Secret), AiModelErrorKind.InvalidRequest },
        { new ResourceNotFoundException(Secret), AiModelErrorKind.InvalidRequest },
        { new HttpRequestException(Secret), AiModelErrorKind.Network },
        { new IOException(Secret), AiModelErrorKind.Network },
    };

    [Theory]
    [MemberData(nameof(ProviderErrors))]
    public void Maps_provider_errors_to_kinds(Exception exception, AiModelErrorKind kind)
    {
        var mapped = BedrockErrorMapper.Map(exception);

        Assert.NotNull(mapped);
        Assert.Equal(kind, mapped.Kind);
    }

    [Theory]
    [MemberData(nameof(ProviderErrors))]
    public void Mapped_message_does_not_contain_the_provider_message(Exception exception, AiModelErrorKind _)
    {
        Assert.DoesNotContain(Secret, BedrockErrorMapper.Map(exception)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_provider_errors_are_not_mapped()
    {
        Assert.Null(BedrockErrorMapper.Map(new InvalidOperationException(Secret)));
    }

    [Theory]
    [InlineData("end_turn", AiStopReason.EndTurn)]
    [InlineData("stop_sequence", AiStopReason.EndTurn)]
    [InlineData("max_tokens", AiStopReason.MaxTokens)]
    [InlineData("content_filtered", AiStopReason.ContentFiltered)]
    [InlineData("guardrail_intervened", AiStopReason.ContentFiltered)]
    [InlineData("tool_use", AiStopReason.Other)]
    [InlineData(null, AiStopReason.Other)]
    public void Maps_stop_reasons(string? stopReason, AiStopReason expected)
    {
        Assert.Equal(expected, BedrockErrorMapper.MapStopReason(stopReason));
    }
}
