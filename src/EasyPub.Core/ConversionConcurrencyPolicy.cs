namespace EasyPub.Core;

public static class ConversionConcurrencyPolicy
{
    public const int Auto = 0;
    public const int Maximum = 32;

    public static int Resolve(int requested, IEnumerable<ConversionRequest> requests, int? logicalProcessors = null)
    {
        if (requested is < 0 or > Maximum)
            throw new ArgumentOutOfRangeException(nameof(requested), $"并发任务数必须为 0（自动）或 1–{Maximum}。");
        if (requested > 0) return requested;

        var jobs = requests as IReadOnlyCollection<ConversionRequest> ?? requests.ToArray();
        var processors = Math.Max(1, logicalProcessors ?? Environment.ProcessorCount);
        var hasMobi = jobs.Any(job => string.Equals(Path.GetExtension(job.OutputPath), ".mobi", StringComparison.OrdinalIgnoreCase));
        var recommended = hasMobi
            ? Math.Clamp((processors + 1) / 2, 2, 8)
            : Math.Clamp(processors, 2, 16);
        return Math.Min(recommended, Math.Max(1, jobs.Count));
    }
}

public sealed class BatchExecutionControl
{
    private volatile TaskCompletionSource _resumeSignal = CompletedSignal();

    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;
        _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _resumeSignal.TrySetResult();
    }

    public Task WaitIfPausedAsync(CancellationToken cancellationToken = default) =>
        IsPaused ? _resumeSignal.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
