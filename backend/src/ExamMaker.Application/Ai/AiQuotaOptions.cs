namespace ExamMaker.Application.Ai;

/// <summary>
/// AI quota and input limits. Bound from Parameter Store configuration (section "AiQuota").
/// Defaults match the decisions recorded in docs/context.md section 14.
/// </summary>
public sealed class AiQuotaOptions
{
    public const string SectionName = "AiQuota";

    public int JobsPerUserPerDay { get; set; } = 10;

    public int MaxQuestionsPerJob { get; set; } = 10;

    public long TokensPerUserPerDay { get; set; } = 100_000;

    public long SystemTokensPerDay { get; set; } = 500_000;

    public int MaxNoteLength { get; set; } = 1000;

    /// <summary>Must equal the SQS redrive maxReceiveCount (docs/context.md section 4.1.1).</summary>
    public int MaxAttempts { get; set; } = 3;
}
