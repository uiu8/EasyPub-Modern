using System.Collections.Concurrent;

namespace EasyPub.Core;

public sealed class BatchConverter(EasyPubConverter converter)
{
    public async Task<IReadOnlyList<ConversionResult>> ConvertAsync(
        IEnumerable<ConversionRequest> requests,
        int maxParallelism = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelism, 1);

        var jobs = requests.ToArray();
        EnsureUniqueOutputs(jobs);
        var results = new ConcurrentDictionary<int, ConversionResult>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, jobs.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelism,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                results[index] = await converter.ConvertAsync(jobs[index], token);
            });

        return Enumerable.Range(0, jobs.Length).Select(index => results[index]).ToArray();
    }

    public async Task<IReadOnlyList<BatchJobOutcome>> ConvertWithReportAsync(
        IEnumerable<ConversionRequest> requests,
        int maxParallelism = 1,
        IProgress<BatchConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelism, 1);

        var jobs = requests.ToArray();
        EnsureUniqueOutputs(jobs);
        var outcomes = new BatchJobOutcome[jobs.Length];
        var fractions = new double[jobs.Length];
        var stages = new string[jobs.Length];
        var completed = 0;
        var failed = 0;
        var cancelled = 0;
        var sync = new object();
        using var semaphore = new SemaphoreSlim(maxParallelism, maxParallelism);

        void Report(int index, string? inputPath, string stage, BookTaskStage itemStage, ArtifactValidationReport? validation = null)
        {
            lock (sync)
            {
                var overall = jobs.Length == 0 ? 1 : fractions.Sum() / jobs.Length;
                progress?.Report(new BatchConversionProgress(
                    jobs.Length, completed, failed, cancelled, inputPath, stage, overall, itemStage, validation, fractions[index]));
            }
        }

        for (var index = 0; index < jobs.Length; index++)
            Report(index, jobs[index].InputPath, "等待转换", BookTaskStage.Waiting);

        var tasks = jobs.Select(async (job, index) =>
        {
            var entered = false;
            try
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                fractions[index] = 0.02;
                Report(index, job.InputPath, "正在检查", BookTaskStage.Checking);
                var itemProgress = new InlineProgress<ConversionProgress>(value =>
                {
                    lock (sync)
                    {
                        fractions[index] = Math.Clamp(value.Fraction, 0, 1);
                        stages[index] = value.Stage;
                    }
                    var convertingStage = string.Equals(Path.GetExtension(job.OutputPath), ".mobi", StringComparison.OrdinalIgnoreCase)
                        ? BookTaskStage.GeneratingMobi
                        : BookTaskStage.GeneratingEpub;
                    Report(index, value.InputPath, value.Stage, convertingStage);
                });
                var result = await Task.Run(
                    () => converter.ConvertAsync(job, itemProgress, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                lock (sync) fractions[index] = 0.94;
                Report(index, job.InputPath, "正在验收成品", BookTaskStage.Validating);
                var validation = await new ArtifactValidationService()
                    .ValidateAndSaveAsync(job, cancellationToken).ConfigureAwait(false);
                outcomes[index] = new BatchJobOutcome(job, result, null, false, validation);
                lock (sync)
                {
                    fractions[index] = 1;
                    completed++;
                }
                var finalStage = validation.StructurePassed && validation.WarningCount == 0
                    ? BookTaskStage.Completed
                    : BookTaskStage.Warning;
                Report(index, job.InputPath, validation.ResultLabel, finalStage, validation);
            }
            catch (OperationCanceledException)
            {
                outcomes[index] = new BatchJobOutcome(job, null, "已取消", true);
                lock (sync)
                {
                    fractions[index] = 1;
                    cancelled++;
                }
                Report(index, job.InputPath, "已取消", BookTaskStage.Cancelled);
            }
            catch (Exception exception)
            {
                outcomes[index] = new BatchJobOutcome(job, null, exception.Message, false);
                lock (sync)
                {
                    fractions[index] = 1;
                    failed++;
                }
                Report(index, job.InputPath, "转换失败", BookTaskStage.Failed);
            }
            finally
            {
                if (entered) semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return outcomes;
    }

    private static void EnsureUniqueOutputs(IReadOnlyList<ConversionRequest> jobs)
    {
        var duplicate = jobs
            .GroupBy(job => Path.GetFullPath(job.OutputPath), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Multiple jobs target the same output: {duplicate.Key}");
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

public sealed record BatchJobOutcome(
    ConversionRequest Request,
    ConversionResult? Result,
    string? ErrorMessage,
    bool Cancelled = false,
    ArtifactValidationReport? Validation = null)
{
    public bool Succeeded => Result is not null;
}
