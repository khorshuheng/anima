using System.Collections.Concurrent;

namespace Anima.Deploy;

public sealed class InMemoryDeployRunStore : IDeployRunStore
{
    private readonly ConcurrentDictionary<string, DeployRun> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _logs = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Add(DeployRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runs[run.Id] = run;
        _logs[run.Id] = new ConcurrentQueue<string>();
    }

    public void Update(DeployRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            _runs[run.Id] = run;
        }
    }

    public void AppendLog(string runId, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var queue = _logs.GetOrAdd(runId, _ => new ConcurrentQueue<string>());
        foreach (string entry in line.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            queue.Enqueue(entry);
        }
    }

    public DeployRun? Find(string runId)
        => _runs.TryGetValue(runId, out var run) ? run : null;

    public IReadOnlyList<DeployRun> List()
        => [.. _runs.Values.OrderByDescending(run => run.QueuedAt)];

    public IReadOnlyList<string> GetLog(string runId)
        => _logs.TryGetValue(runId, out var queue) ? queue.ToArray() : [];
}
