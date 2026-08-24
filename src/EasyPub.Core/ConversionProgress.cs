namespace EasyPub.Core;

public sealed record ConversionProgress(
    string InputPath,
    double Fraction,
    string Stage);

public sealed record BatchConversionProgress(
    int TotalCount,
    int CompletedCount,
    int FailedCount,
    int CancelledCount,
    string? CurrentInputPath,
    string Stage,
    double Fraction,
    BookTaskStage ItemStage = BookTaskStage.Waiting,
    ArtifactValidationReport? Validation = null,
    double ItemFraction = 0);

public enum BookTaskStage
{
    Waiting,
    Checking,
    GeneratingEpub,
    GeneratingMobi,
    Validating,
    Completed,
    Warning,
    Failed,
    Cancelled,
}
