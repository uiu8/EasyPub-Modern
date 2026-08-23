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
    double Fraction);
