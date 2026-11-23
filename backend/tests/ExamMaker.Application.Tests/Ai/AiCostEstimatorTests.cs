using ExamMaker.Application.Ai;

namespace ExamMaker.Application.Tests.Ai;

public sealed class AiCostEstimatorTests
{
    private const string Haiku = "global.anthropic.claude-haiku-4-5-20251001-v1:0";
    private const string Sonnet = "global.anthropic.claude-sonnet-4-6";

    private static AiCostEstimator Defaults() =>
        new(new AiPricingOptions { Models = [.. AiPricingOptions.Defaults] });

    [Fact]
    public void Haiku_cost_uses_input_and_output_prices()
    {
        var cost = Defaults().Estimate(Haiku, 1_000_000, 1_000_000, out var priced);

        Assert.Equal(6.00m, cost);
        Assert.True(priced);
    }

    [Fact]
    public void Sonnet_cost()
    {
        var cost = Defaults().Estimate(Sonnet, 4000, 4000, out var priced);

        Assert.Equal(0.072m, cost);
        Assert.True(priced);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("global.anthropic.unknown-model")]
    public void Unknown_or_null_model_costs_zero_and_is_unpriced(string? modelId)
    {
        var cost = Defaults().Estimate(modelId, 4000, 4000, out var priced);

        Assert.Equal(0m, cost);
        Assert.False(priced);
    }

    [Fact]
    public void Negative_price_throws()
    {
        var options = new AiPricingOptions
        {
            Models = [new AiModelPrice { ModelId = Haiku, InputPer1MUsd = -1m, OutputPer1MUsd = 5m }],
        };

        Assert.Throws<InvalidOperationException>(() => new AiCostEstimator(options));
    }

    [Fact]
    public void Duplicate_model_id_throws()
    {
        var options = new AiPricingOptions
        {
            Models =
            [
                new AiModelPrice { ModelId = Haiku, InputPer1MUsd = 1m, OutputPer1MUsd = 5m },
                new AiModelPrice { ModelId = Haiku, InputPer1MUsd = 2m, OutputPer1MUsd = 6m },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => new AiCostEstimator(options));
    }

    [Fact]
    public void Blank_model_id_throws()
    {
        var options = new AiPricingOptions
        {
            Models = [new AiModelPrice { ModelId = " ", InputPer1MUsd = 1m, OutputPer1MUsd = 5m }],
        };

        Assert.Throws<InvalidOperationException>(() => new AiCostEstimator(options));
    }

    [Fact]
    public void Defaults_match_context_md_section_6()
    {
        Assert.Collection(
            AiPricingOptions.Defaults,
            haiku =>
            {
                Assert.Equal(Haiku, haiku.ModelId);
                Assert.Equal(1.00m, haiku.InputPer1MUsd);
                Assert.Equal(5.00m, haiku.OutputPer1MUsd);
            },
            sonnet =>
            {
                Assert.Equal(Sonnet, sonnet.ModelId);
                Assert.Equal(3.00m, sonnet.InputPer1MUsd);
                Assert.Equal(15.00m, sonnet.OutputPer1MUsd);
            });
    }
}
