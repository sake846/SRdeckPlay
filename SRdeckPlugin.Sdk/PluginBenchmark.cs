using System.Diagnostics;

namespace SRdeckPlugin.Sdk;

public sealed record PluginBenchmarkResult(
    int Iterations,
    TimeSpan Elapsed,
    long AllocatedBytes,
    double InputDurationSeconds,
    double RealtimeFactor,
    IReadOnlyList<TimeSpan> IterationDurations,
    IReadOnlyList<long> IterationAllocatedBytes)
{
    public double AverageMilliseconds => Elapsed.TotalMilliseconds / Iterations;
    public double AllocatedBytesPerIteration => AllocatedBytes / (double)Iterations;
    public double MedianMilliseconds => PercentileMilliseconds(IterationDurations, 0.50);
    public double P95Milliseconds => PercentileMilliseconds(IterationDurations, 0.95);

    private static double PercentileMilliseconds(IReadOnlyList<TimeSpan> samples, double percentile)
    {
        if (samples.Count == 0) return 0;
        double[] sorted = samples.Select(sample => sample.TotalMilliseconds).OrderBy(value => value).ToArray();
        int index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

public static class PluginBenchmark
{
    public static PluginBenchmarkResult Run(
        Action operation,
        int iterations,
        double inputDurationSecondsPerIteration,
        int warmupIterations = 1)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (warmupIterations < 0) throw new ArgumentOutOfRangeException(nameof(warmupIterations));
        if (!double.IsFinite(inputDurationSecondsPerIteration) || inputDurationSecondsPerIteration < 0)
            throw new ArgumentOutOfRangeException(nameof(inputDurationSecondsPerIteration));

        for (int index = 0; index < warmupIterations; index++) operation();
        var durations = new TimeSpan[iterations];
        var allocations = new long[iterations];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        // Capture each iteration separately after the operation so the
        // measured values describe the operation rather than warm-up or the
        // result object construction.
        for (int index = 0; index < iterations; index++)
        {
            long iterationAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long iterationStarted = Stopwatch.GetTimestamp();
            operation();
            durations[index] = Stopwatch.GetElapsedTime(iterationStarted);
            allocations[index] = GC.GetAllocatedBytesForCurrentThread() - iterationAllocatedBefore;
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double inputDuration = inputDurationSecondsPerIteration * iterations;
        double realtimeFactor = elapsed.TotalSeconds == 0
            ? double.PositiveInfinity
            : inputDuration / elapsed.TotalSeconds;
        return new(iterations, elapsed, allocated, inputDuration, realtimeFactor,
            durations, allocations);
    }
}
