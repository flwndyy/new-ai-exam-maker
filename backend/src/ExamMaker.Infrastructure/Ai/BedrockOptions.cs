namespace ExamMaker.Infrastructure.Ai;

/// <summary>Bedrock settings, bound from configuration section "Bedrock" (Parameter Store in AWS).</summary>
public sealed class BedrockOptions
{
    public const string SectionName = "Bedrock";

    /// <summary>Model or inference profile id, e.g. a Claude Haiku id for dev and a Sonnet id for the demo.</summary>
    public string? ModelId { get; set; }

    public int TimeoutSeconds { get; set; } = 120;
}
