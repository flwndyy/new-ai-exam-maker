namespace ExamMaker.Application.Ai;

/// <summary>Estimates Bedrock cost from token counts. An estimate only, never an AWS bill.</summary>
public sealed class AiCostEstimator
{
    private const decimal TokensPerUnit = 1_000_000m;

    private readonly Dictionary<string, AiModelPrice> _prices = new(StringComparer.Ordinal);

    public AiCostEstimator(AiPricingOptions options)
    {
        foreach (var price in options.Models)
        {
            if (string.IsNullOrWhiteSpace(price.ModelId))
            {
                throw new InvalidOperationException("AiPricing: every model price needs a ModelId.");
            }

            if (price.InputPer1MUsd < 0 || price.OutputPer1MUsd < 0)
            {
                throw new InvalidOperationException($"AiPricing: prices for '{price.ModelId}' must not be negative.");
            }

            if (!_prices.TryAdd(price.ModelId, price))
            {
                throw new InvalidOperationException($"AiPricing: model '{price.ModelId}' is configured more than once.");
            }
        }
    }

    /// <summary>Returns the cost in USD; 0 with <paramref name="priced"/> = false when the model has no price.</summary>
    public decimal Estimate(string? modelId, long inputTokens, long outputTokens, out bool priced)
    {
        if (modelId is null || !_prices.TryGetValue(modelId, out var price))
        {
            priced = false;
            return 0m;
        }

        priced = true;
        return inputTokens / TokensPerUnit * price.InputPer1MUsd
            + outputTokens / TokensPerUnit * price.OutputPer1MUsd;
    }
}
