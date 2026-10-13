using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PS3HddTool.Core.Migration;

namespace PS3HddTool.Migrate;

// Output failures request cancellation at safe checkpoints. They never throw
// through a migration observer, especially during rollback or commit.
internal sealed class EventReporter : IDisposable
{
    private readonly object _gate = new();
    private readonly CliOptions _options;
    private readonly CancellationTokenSource _cancel;
    private readonly bool _recovery;
    private readonly string? _marker;
    private readonly StreamWriter? _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private Timer? _timer;
    private MigrationProgress? _last;
    private double _lastProgress, _lastEmit, _phaseStart;
    private long _sequence;
    private bool _terminal, _cancelReported, _stdoutFailed, _stderrFailed, _logFailed;
    internal Exception? Failure { get; private set; }
    internal EventReporter(CliOptions options, string? log, string? marker, CancellationTokenSource cancel, bool recovery)
    {
        _options = options; _cancel = cancel; _recovery = recovery; _marker = marker;
        if (log != null) _log = new StreamWriter(new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }
    internal void Start()
    {
        lock (_gate) Emit("started", new { command = _options.Command, cancellation = _recovery ? "deferred-until-recovery-finishes" : "graceful-rollback" });
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(_options.Heartbeat), TimeSpan.FromSeconds(_options.Heartbeat));
    }
    internal void PollCancellation()
    {
        lock (_gate)
        {
            if (_terminal || _marker == null || _cancelReported) return;
            try { if (File.Exists(RegularImage.CheckedPath(_marker, false))) RequestCancel("cancel-file"); }
            catch (Exception e) { Failed(e); }
        }
    }
    internal void RequestCancel(string source)
    {
        lock (_gate)
        {
            if (_terminal || _cancelReported) return;
            _cancelReported = true; Emit("cancel-requested", new { source, deferred = _recovery });
            if (!_recovery) _cancel.Cancel();
        }
    }
    internal void Progress(MigrationProgress value)
    {
        lock (_gate)
        {
            if (_terminal) return;
            PollCancellation();
            bool changed = _last?.Phase != value.Phase;
            if (changed) _phaseStart = _clock.Elapsed.TotalSeconds;
            _last = value; _lastProgress = _clock.Elapsed.TotalSeconds;
            if (changed || value.Completed != null && value.Completed == value.Total || (_clock.Elapsed.TotalSeconds - _lastEmit) * 1000 >= _options.Interval)
            { _lastEmit = _clock.Elapsed.TotalSeconds; Emit("progress", Snapshot()); }
        }
    }
    private object Snapshot()
    {
        double seconds = _clock.Elapsed.TotalSeconds - _phaseStart;
        double? rate = _last?.Completed > 0 && seconds > 0 ? _last.Completed / seconds : null;
        double? percent = _last?.Total > 0 && _last.Completed != null ? Math.Min(100, (double)_last.Completed.Value / _last.Total.Value * 100) : null;
        return new { progress = _last, phasePercent = percent, unitsPerSecond = rate,
            phaseEtaSeconds = rate > 0 && _last?.Total != null ? Math.Max(0, (_last.Total.Value - (_last.Completed ?? 0)) / rate.Value) : (double?)null,
            secondsSinceProgress = _clock.Elapsed.TotalSeconds - _lastProgress, cancellationRequested = _cancel.IsCancellationRequested };
    }
    private void Tick()
    {
        lock (_gate)
        {
            if (_terminal) return;
            try { PollCancellation(); Emit("heartbeat", Snapshot()); } catch (Exception e) { Failed(e); }
        }
    }
    internal void Terminal(string type, object data)
    {
        lock (_gate)
        {
            if (_terminal) return;
            _terminal = true; Emit(type, data);
        }
    }
    internal int Result(object result)
    {
        lock (_gate)
        {
            // Stop heartbeats atomically with deciding the terminal outcome.
            if (_terminal) throw new InvalidOperationException("Session already finished.");
            _terminal = true;
            int exit = Failure == null ? 0 : 6;
            Emit("result", new { exitCode = exit, result, reportingError = Failure?.Message });
            return Failure == null ? exit : 6;
        }
    }
    private void Emit(string type, object data)
    {
        string line = JsonSerializer.Serialize(new { schemaVersion = 1, type, runId = _runId, sequence = ++_sequence,
            utc = DateTimeOffset.UtcNow, processId = Environment.ProcessId, command = _options.Command, elapsedSeconds = _clock.Elapsed.TotalSeconds, data });
        if (_log != null && !_logFailed) try { _log.WriteLine(line); } catch (Exception e) { _logFailed = true; Failed(e); }
        if (_options.Json && !_stdoutFailed) try { Console.Out.WriteLine(line); Console.Out.Flush(); } catch (Exception e) { _stdoutFailed = true; Failed(e); }
        if (!_options.Json)
        {
            if (type == "result" && !_stdoutFailed) try {
                var payload = JsonSerializer.SerializeToElement(data);
                Console.Out.WriteLine(JsonSerializer.Serialize(payload.TryGetProperty("result", out var result) ? result : payload, ImageMigrator.Json));
                Console.Out.Flush();
            } catch (Exception e) { _stdoutFailed = true; Failed(e); }
            else if (!_stderrFailed) try { Console.Error.WriteLine(type is "progress" or "heartbeat" ? $"{type}: {_last?.Stage} {_last?.Completed}/{_last?.Total} {_last?.Unit}" : type + ": " + JsonSerializer.Serialize(data)); } catch (Exception e) { _stderrFailed = true; Failed(e); }
        }
    }
    private void Failed(Exception error)
    {
        Failure ??= error;
        if (!_recovery) _cancel.Cancel();
        if (!_stderrFailed) try { Console.Error.WriteLine("Reporting failure; graceful cancellation requested (recovery continues): " + error.Message); } catch { _stderrFailed = true; }
    }
    public void Dispose()
    {
        _timer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { _log?.Dispose(); } catch { /* Every line has already been flushed. */ }
    }
}
