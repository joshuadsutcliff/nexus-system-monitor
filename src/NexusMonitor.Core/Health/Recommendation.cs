namespace NexusMonitor.Core.Health;

public enum RecommendationSeverity { Info, Warning, Critical }

public enum RecommendationAction
{
    None,
    EnableAutoBalance,
    EnableMemoryReclaim,
    EnableGamingMode,
    ReviewProcesses,
    CheckDiskSpace,
    CheckTemperatures,
    InvestigateMemoryLeak,
}

public record Recommendation
{
    public string Title { get; init; } = string.Empty;
    public string Body  { get; init; } = string.Empty;
    public RecommendationSeverity Severity { get; init; }
    public RecommendationAction   Action   { get; init; }
}
