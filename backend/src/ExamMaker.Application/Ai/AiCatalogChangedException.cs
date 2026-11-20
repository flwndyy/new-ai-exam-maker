namespace ExamMaker.Application.Ai;

/// <summary>Signal that catalog metadata changed between initial validation and transactional job creation.</summary>
public sealed class AiCatalogChangedException() : Exception("AI catalog changed before job creation.");
