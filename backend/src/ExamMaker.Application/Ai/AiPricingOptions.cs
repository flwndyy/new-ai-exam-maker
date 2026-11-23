namespace ExamMaker.Application.Ai;

/// <summary>
/// Price of one Bedrock model in USD per one million tokens.
/// </summary>
public sealed class AiModelPrice
{
    public string ModelId { get; set; } = string.Empty;

    public decimal InputPer1MUsd { get; set; }

    public decimal OutputPer1MUsd { get; set; }
}

/// <summary>
/// Model price table for the admin AI usage report (section "AiPricing"). Bound as a list because
/// model ids contain ':' which is the .NET configuration key separator:
/// AiPricing:Models:{i}:ModelId / InputPer1MUsd / OutputPer1MUsd.
/// </summary>
public sealed class AiPricingOptions
{
    public const string SectionName = "AiPricing";

    /// <summary>Empty by default: the binder appends to existing items, so defaults are applied after binding.</summary>
    public List<AiModelPrice> Models { get; set; } = [];

    /// <summary>On-demand prices recorded in docs/context.md section 6.</summary>
    public static IReadOnlyList<AiModelPrice> Defaults { get; } =
    [
        new() { ModelId = "global.anthropic.claude-haiku-4-5-20251001-v1:0", InputPer1MUsd = 1.00m, OutputPer1MUsd = 5.00m },
        new() { ModelId = "global.anthropic.claude-sonnet-4-6", InputPer1MUsd = 3.00m, OutputPer1MUsd = 15.00m },
    ];
}
