using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Tests.Ai.Generation;

public sealed class AiQuestionGeneratorTests
{
    private const string Valid = """{"questions":[{"stem":"Câu 1","payload":{"options":[{"label":"A","text":"1"},{"label":"B","text":"2"},{"label":"C","text":"3"},{"label":"D","text":"4"}],"correctLabel":"C"},"explanation":"Giải thích"}]}""";
    private const string WrongSchema = """{"questions":[{"stem":"Câu 1","payload":{"options":[],"correctLabel":"C"}}]}""";

    private readonly FakeAiModelClient _model = new();
    private readonly AiGenerationOptions _options = new() { MaxRepairAttempts = 1, MaxOutputTokens = 4000, Temperature = 0.3 };
    private readonly List<AiModelResponse> _calls = [];

    private Task<AiGenerationResult> Generate(CancellationToken cancellationToken = default) =>
        new AiQuestionGenerator(_model, _options).GenerateAsync(AiGenerationSpecs.Sample(), (response, _) =>
        {
            _calls.Add(response);
            return Task.CompletedTask;
        }, cancellationToken);

    [Fact]
    public async Task Valid_first_response_returns_questions_with_provenance()
    {
        _model.Returns(Valid, inputTokens: 120, outputTokens: 80);

        var result = await Generate();

        Assert.Equal("Câu 1", Assert.Single(result.Questions).Stem);
        Assert.Equal(AiPromptBuilder.Version, result.PromptVersion);
        Assert.Equal(FakeAiModelClient.ModelId, result.ModelId);
        Assert.Equal((120L, 80L, 1), (result.InputTokens, result.OutputTokens, result.ModelCalls));
    }

    [Fact]
    public async Task Request_uses_versioned_prompt_and_configured_limits()
    {
        _model.Returns(Valid);

        await Generate();

        var request = Assert.Single(_model.Requests);
        var prompt = AiPromptBuilder.Build(AiGenerationSpecs.Sample());
        Assert.Equal(prompt.System, request.System);
        Assert.Equal(new AiChatMessage(AiChatRole.User, prompt.User), Assert.Single(request.Messages));
        Assert.Equal(4000, request.MaxOutputTokens);
        Assert.Equal(0.3, request.Temperature);
    }

    [Theory]
    [InlineData("không phải JSON")]
    [InlineData(WrongSchema)]
    // One repair round is cheaper than failing the job and asking the teacher to spend quota again.
    public async Task Invalid_output_is_repaired_in_the_same_conversation(string first)
    {
        _model.Returns(first, inputTokens: 100, outputTokens: 40).Returns(Valid, inputTokens: 200, outputTokens: 60);

        var result = await Generate();

        Assert.Single(result.Questions);
        Assert.Equal((300L, 100L, 2), (result.InputTokens, result.OutputTokens, result.ModelCalls));
        var repair = _model.Requests[1].Messages;
        Assert.Equal(3, repair.Count);
        Assert.Equal(new AiChatMessage(AiChatRole.Assistant, first), repair[1]);
        Assert.Equal(AiChatRole.User, repair[2].Role);
        Assert.Contains("rejected", repair[2].Text);
    }

    [Theory]
    [InlineData("không phải JSON", AiErrorCodes.InvalidJson)]
    [InlineData(WrongSchema, AiErrorCodes.SchemaViolation)]
    public async Task Output_still_invalid_after_repairs_fails_permanently(string text, string code)
    {
        _model.Returns(text).Returns(text);

        var error = await Assert.ThrowsAsync<AiOutputException>(() => Generate());

        Assert.Equal(code, error.Code);
        Assert.Equal(2, _model.Requests.Count);
        Assert.False(AiErrorClassifier.Classify(error).IsRetryable);
    }

    [Fact]
    public async Task Zero_repair_attempts_fails_after_one_call()
    {
        _options.MaxRepairAttempts = 0;
        _model.Returns("không phải JSON");

        await Assert.ThrowsAsync<AiOutputException>(() => Generate());
        Assert.Single(_model.Requests);
    }

    [Fact]
    // A truncated answer would be truncated again at the same limit, so repairing only wastes tokens.
    public async Task Truncated_output_fails_without_repair()
    {
        _model.Returns("{\"questions\":[", AiStopReason.MaxTokens);

        var error = await Assert.ThrowsAsync<AiOutputException>(() => Generate());

        Assert.Equal(AiErrorCodes.OutputTruncated, error.Code);
        Assert.Single(_model.Requests);
    }

    [Fact]
    public async Task Filtered_output_fails_as_content_refused_without_repair()
    {
        _model.Returns("", AiStopReason.ContentFiltered);

        var error = await Assert.ThrowsAsync<AiOutputException>(() => Generate());

        Assert.Equal(AiErrorCodes.ContentRefused, error.Code);
        Assert.Single(_model.Requests);
    }

    [Fact]
    // Section 4.1.1: tokens are recorded per call so retried and rejected calls are still counted.
    public async Task Every_completed_call_is_reported_even_when_output_is_rejected()
    {
        _model.Returns("không phải JSON", outputTokens: 11).Returns("vẫn sai", outputTokens: 22);

        await Assert.ThrowsAsync<AiOutputException>(() => Generate());

        Assert.Equal([11L, 22L], _calls.Select(x => x.OutputTokens));
    }

    [Fact]
    // A callback failure (for example the daily token quota being exceeded) stops further model calls.
    public async Task Callback_exception_stops_generation()
    {
        _model.Returns("không phải JSON").Returns(Valid);
        var generator = new AiQuestionGenerator(_model, _options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync(AiGenerationSpecs.Sample(),
            (_, _) => throw new InvalidOperationException("quota"), default));

        Assert.Single(_model.Requests);
    }

    [Fact]
    // The worker classifies provider exceptions itself; the generator must not swallow or rewrap them.
    public async Task Model_exception_propagates_unchanged()
    {
        var exception = new AiModelException(AiModelErrorKind.Throttled, "slow down");
        _model.Throws(exception);

        Assert.Same(exception, await Assert.ThrowsAsync<AiModelException>(() => Generate()));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Cancellation_token_reaches_the_model_client()
    {
        using var source = new CancellationTokenSource();
        _model.Returns(Valid);

        await Generate(source.Token);

        Assert.Equal(source.Token, Assert.Single(_model.Tokens));
    }

    [Fact]
    public void Negative_repair_attempts_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiQuestionGenerator(_model, new AiGenerationOptions { MaxRepairAttempts = -1 }));
}
