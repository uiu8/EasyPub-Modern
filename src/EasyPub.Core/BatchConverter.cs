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

        void Report(int index, string? inputPath, string stage)
        {
            lock (sync)
            {
                var overall = jobs.Length == 0 ? 1 : fractions.Sum() / jobs.Length;
                progress?.Report(new BatchConversionProgress(
                    jobs.Length, completed, failed, cancelled, inputPath, stage, overall));
            }
        }

        var tasks = jobs.Select(async (job, index) =>
        {
            var entered = false;
            try
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                Report(index, job.InputPath, "开始转换");
                var itemProgress = new Progress<ConversionProgress>(value =>
                {
                    lock (sync)
                    {
                        fractions[index] = Math.Clamp(value.Fraction, 0, 1);
                        stages[index] = value.Stage;
                    }
                    Report(index, value.InputPath, value.Stage);
                });
                var result = await Task.Run(
                    () => converter.ConvertAsync(job, itemProgress, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                outcomes[index] = new BatchJobOutcome(job, result, null, false);
                lock (sync)
                {
                    fractions[index] = 1;
                    completed++;
                }
                Report(index, job.InputPath, "转换完成");
            }
            catch (OperationCanceledException)
            {
                outcomes[index] = new BatchJobOutcome(job, null, "已取消", true);
                lock (sync)
                {
                    fractions[index] = 1;
                    cancelled++;
                }
                Report(index, job.InputPath, "已取消");
            }
            catch (Exception exception)
            {
                outcomes[index] = new BatchJobOutcome(job, null, exception.Message, false);
                lock (sync)
                {
                    fractions[index] = 1;
                    failed++;
                }
                Report(index, job.InputPath, "转换失败");
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
}

public sealed record BatchJobOutcome(
    ConversionRequest Request,
    ConversionResult? Result,
    string? ErrorMessage,
    bool Cancelled = false)
{
    public bool Succeeded => Result is not null;
}
