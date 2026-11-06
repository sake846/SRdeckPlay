using System.Collections.Concurrent;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Sdk;

public interface IPluginMetricsRegistry
{
    IPluginMetrics GetOrCreate(string pluginId);
    PluginMetricsSnapshot GetSnapshot(string pluginId);
}

public sealed class PluginMetricsRegistry(TimeProvider timeProvider) : IPluginMetricsRegistry
{
    private readonly ConcurrentDictionary<string, MetricStore> _stores =
        new(StringComparer.Ordinal);

    public IPluginMetrics GetOrCreate(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _stores.GetOrAdd(pluginId, id => new MetricStore(id, timeProvider));
    }

    public PluginMetricsSnapshot GetSnapshot(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _stores.TryGetValue(pluginId, out MetricStore? store)
            ? store.GetSnapshot()
            : new(pluginId, timeProvider.GetUtcNow(), []);
    }

    private sealed class MetricStore(string pluginId, TimeProvider timeProvider) : IPluginMetrics
    {
        private readonly object _gate = new();
        private readonly Dictionary<(PluginProcessingStage Stage, string Name, PluginMetricKind Kind), MutableMetric> _values = [];

        public void AddCounter(PluginProcessingStage stage, string name, long delta = 1, string unit = "count")
        {
            Validate(name, unit, delta);
            lock (_gate)
            {
                MutableMetric metric = GetOrCreate(stage, name, PluginMetricKind.Counter, unit);
                metric.Value += delta;
                metric.UpdateCount++;
            }
        }

        public void SetGauge(PluginProcessingStage stage, string name, double value, string unit)
        {
            Validate(name, unit, value);
            lock (_gate)
            {
                MutableMetric metric = GetOrCreate(stage, name, PluginMetricKind.Gauge, unit);
                metric.Value = value;
                metric.UpdateCount++;
            }
        }

        public void RecordDuration(PluginProcessingStage stage, string name, TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
            ValidateName(name);
            lock (_gate)
            {
                MutableMetric metric = GetOrCreate(stage, name, PluginMetricKind.Duration, "ms");
                metric.Value += elapsed.TotalMilliseconds;
                metric.UpdateCount++;
            }
        }

        public PluginMetricsSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                PluginMetricValue[] values = _values
                    .OrderBy(item => item.Key.Stage)
                    .ThenBy(item => item.Key.Name, StringComparer.Ordinal)
                    .Select(item => new PluginMetricValue(
                        item.Key.Stage, item.Key.Name, item.Key.Kind,
                        item.Value.Value, item.Value.Unit, item.Value.UpdateCount))
                    .ToArray();
                return new(pluginId, timeProvider.GetUtcNow(), values);
            }
        }

        private MutableMetric GetOrCreate(PluginProcessingStage stage, string name,
            PluginMetricKind kind, string unit)
        {
            var key = (stage, name, kind);
            if (_values.TryGetValue(key, out MutableMetric? existing))
            {
                if (!string.Equals(existing.Unit, unit, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Metric '{name}' cannot change unit from '{existing.Unit}' to '{unit}'.");
                return existing;
            }
            var created = new MutableMetric(unit);
            _values.Add(key, created);
            return created;
        }

        private static void Validate(string name, string unit, double value)
        {
            ValidateName(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(unit);
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        }

        private static void ValidateName(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name.Length > 96) throw new ArgumentOutOfRangeException(nameof(name));
            foreach (char character in name)
                if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
                    throw new ArgumentException("Metric names must contain only ASCII letters, digits, '.', '-' or '_'.", nameof(name));
        }

        private sealed class MutableMetric(string unit)
        {
            public string Unit { get; } = unit;
            public double Value { get; set; }
            public long UpdateCount { get; set; }
        }
    }
}
