using System.Diagnostics;

namespace PS3HddTool.Core.Migration;

/// <summary>Phase-local measurements. Copy bytes count newly allocated tree bytes,
/// including directories, attributes and indirect tables, not raw device traffic.
/// Hash bytes include logical sparse holes. Completion of a copy phase is not commit.</summary>
public sealed record MigrationProgress(string Phase, string Stage, long? Completed = null,
    long? Total = null, string? Unit = null, long? Inode = null, string? Path = null,
    long? NodesCompleted = null, long? NodesTotal = null);

internal static class MigrationTelemetry
{
    // Observers cannot interrupt a commit or rollback. Cancellation is a separate
    // explicit token. Legacy string callbacks retain their fault-injection semantics.
    internal static void Send(Action<MigrationProgress>? observer, MigrationProgress value)
    { if (observer != null) try { observer(value); } catch { } }
}

internal sealed class ProgressMeter(Action<MigrationProgress>? observer, string phase, string stage,
    long? total = null, string unit = "bytes")
{
    private long _last, _done;
    internal void Add(long count, long? inode = null, string? path = null,
        long? nodesDone = null, long? nodesTotal = null, bool force = false)
    {
        _done = count > long.MaxValue - _done ? long.MaxValue : _done + count;
        if (observer == null) return;
        long now = Stopwatch.GetTimestamp();
        if (!force && _last != 0 && Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds < 100) return;
        _last = now;
        MigrationTelemetry.Send(observer, new(phase, stage, _done, total, unit, inode, path, nodesDone, nodesTotal));
    }
}
