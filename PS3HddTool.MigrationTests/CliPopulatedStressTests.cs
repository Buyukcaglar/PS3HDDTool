using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PS3HddTool.Core.Migration;
using PS3HddTool.Migrate;

// Opt-in end-to-end controller. Children run the unmodified production CLI and
// image transport, never an injected backend, physical endpoint or test-only CLI.
static class CliPopulatedStressTests
{
    internal static void Run(string dir, string crypto)
    {
        if (crypto is not ("plain" or "cbc192" or "cbc192-bswap16" or "xts128")) throw new ArgumentException("Unknown synthetic crypto mode.");
        const long sourceBytes = 1_000_000_000_000, targetBytes = 2_000_000_000_000;
        var clock = Stopwatch.StartNew();
        var profile = PopulatedStressTests.Population(bulk: false);
        var sourceFixture = new SparseFixture(sourceBytes); var targetFixture = new SparseFixture(targetBytes);
        string source = Path.Combine(dir, "source-1TB.img"), target = Path.Combine(dir, "target-2TB.img"), key = Path.Combine(dir, "generated.key");
        if (crypto != "plain") File.WriteAllBytes(key, Fixture.Payload(crypto is "cbc192" or "cbc192-bswap16" ? 24 : 32, 991));
        DirectEndpoint Endpoint(string path) => new(path, SparseFixture.Offset / 512, crypto, crypto == "plain" ? null : key, "image");
        var request = new DirectRequest(Endpoint(source), Endpoint(target), Path.Combine(dir, "success-journal"),
            Path.Combine(dir, "generated-registry.bin"), Path.Combine(dir, "generated-registry.bin"));
        var registry = Fixture.Registry(Enumerable.Range(1, PopulatedStressTests.Accounts).Select(u => ($"{u:D8}", $"{u:X16}")).ToArray());
        File.WriteAllBytes(request.SourceRegistry!, registry);
        var sessions = new List<Session>();
        using var host = new HostMeter(dir, source, target);
        Console.WriteLine($"CLI populated stress {crypto}: create 1TB/2TB regular sparse images, {profile.Items.Count} inodes");
        sourceFixture.Create(request.Source, profile);
        var old = new Fixture(); old.Add("old-directory", 4); old.Add("old-directory/old-payload", payload: Fixture.Payload(17003, 711)); old.Finish();
        targetFixture.Create(request.Destination, old);
        host.Measure();
        if (new FileInfo(source).Length != sourceBytes || new FileInfo(target).Length != targetBytes ||
            sourceFixture.PayloadRanges.Max(r => r.Offset + r.Length) < 990_000_000_000)
            throw new Exception("Full-capacity/far-end geometry not exercised.");
        if (SparseFile.Allocated(source) > 2L << 30 || SparseFile.Allocated(target) > 2L << 30) throw new Exception("Fixture allocation budget exceeded.");
        string sourceHash = SparseFile.AllocatedDigest(source), originalTargetHash = SparseFile.AllocatedDigest(target);
        string WriteRequest(DirectRequest r, string name)
        {
            if (r.Source.Kind != "image" || r.Destination.Kind != "image" || r.Source.Path != source || r.Destination.Path != target)
                throw new Exception("Stress accepts only its exact generated images.");
            string path = Path.Combine(dir, name + "-request.json");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(r, ImageMigrator.Json)); return path;
        }
        Session Execute(string name, string[] arguments, Action<JsonElement, Process>? trigger = null, bool killed = false)
        {
            var session = Invoke(dir, name, arguments, host, trigger, killed); sessions.Add(session); return session;
        }
        string successRequest = WriteRequest(request, "success");
        var planSession = Execute("plan", ["direct-plan", "--request", successRequest]); planSession.Check(0);
        var plan = planSession.Result.Deserialize<DirectPlan>(ImageMigrator.Json)!;
        if (plan.Nodes != profile.Items.Count || Directory.Exists(request.JournalDirectory) || SparseFile.AllocatedDigest(target) != originalTargetHash ||
            SparseFile.AllocatedDigest(source) != sourceHash) throw new Exception("Read-only CLI plan changed inputs or inventory.");
        bool liveStatus = false;
        var copy = Execute("success", ["direct-run", "--request", successRequest], (e, _) => {
            if (!liveStatus && Phase(e) == "copy-tree")
            {
                liveStatus = true; var status = Execute("live-status", ["direct-status", "--journal", request.JournalDirectory]); status.Check(0);
                if (status.Result.GetProperty("Phase").GetString() != "copying" || !status.Result.GetProperty("recoveryRequired").GetBoolean() ||
                    status.Result.GetProperty("processLivenessKnown").GetBoolean()) throw new Exception("Live journal snapshot misleading.");
            }
        });
        copy.Check(0);
        if (!liveStatus || copy.CopyNodes != profile.Items.Count || copy.CopyBytes != plan.RequiredFragments * SparseFixture.Frag ||
            !copy.Phases.Contains("Direct commit durable") || copy.Heartbeats == 0 || !copy.CopyCompletedBeforeCommit)
            throw new Exception("CLI copy counters, heartbeat or commit ordering missing.");
        Execute("verify", ["direct-verify", "--journal", request.JournalDirectory]).Check(0);
        var complete = DirectMigrator.Status(request.JournalDirectory);
        if (complete.Phase != "complete" || complete.ConsoleRuntimeValidated) throw new Exception("CLI completion boundary.");
        var payload = new List<DiskRange>();
        void Audit(bool collect)
        {
            Console.WriteLine("Independent full-geometry raw inode/directory/payload/map audit");
            using var disk = RegularImage.Open(new(target, SparseFixture.Offset / 512, crypto, crypto == "plain" ? null : key));
            byte[] sb = disk.ReadBytes(SparseFixture.Offset + 65536, 8192);
            if (Fixture.R64(sb, 0x438) != targetFixture.Fragments || Fixture.R32(sb, 0x2C) != targetFixture.Groups) throw new Exception("2TB geometry lost.");
            var manifest = new MigrationManifest(1, new(request.Source.Image, request.Destination.Image, dir), profile.Items.Count,
                complete.Mapping, "", null, false);
            Oracle.Check(profile, disk.ReadBytes, SparseFixture.Offset, manifest,
                collect ? (at, count) => payload.Add(new(SparseFixture.Offset + at * SparseFixture.Frag, count * SparseFixture.Frag)) : null);
        }
        Audit(true);
        var protectedRanges = DirectLayout.Merge(complete.Metadata.Concat(payload).Concat(complete.Outside));
        string ProtectedHash()
            => RawHash(request.Destination, protectedRanges);
        string protectedHash = ProtectedHash();
        if (SparseFile.AllocatedDigest(source) != sourceHash) throw new Exception("CLI copy changed source.");

        var cancelRequest = request with { JournalDirectory = Path.Combine(dir, "cancel-journal") };
        string cancelJson = WriteRequest(cancelRequest, "cancel"), marker = Path.Combine(dir, "cancel.flag");
        bool cancellationSent = false; long cancelledAtNodes = 0, cancelledAtBytes = 0;
        var cancelled = Execute("cancel-copy", ["direct-run", "--request", cancelJson, "--cancel-file", marker], (e, _) => {
            if (!cancellationSent && Phase(e) == "copy-tree")
            {
                var p = e.GetProperty("data").GetProperty("progress");
                long nodes = p.GetProperty("NodesCompleted").GetInt64(), bytes = p.GetProperty("Completed").GetInt64();
                if (nodes >= profile.Items.Count / 2 && bytes > 0)
                {
                    cancellationSent = true; cancelledAtNodes = nodes; cancelledAtBytes = bytes;
                    Execute("cancel-command", ["cancel", "--cancel-file", marker]).Check(0);
                }
            }
        });
        cancelled.Check(130);
        if (!cancellationSent || cancelled.Terminal.GetProperty("journalPhase").GetString() != "rolled-back" ||
            cancelled.Terminal.GetProperty("recoveryRequired").GetBoolean() || !cancelled.Phases.Contains("rollback-metadata") ||
            !cancelled.Phases.Contains("rollback-readback") || ProtectedHash() != protectedHash)
            throw new Exception("CLI cancellation did not restore the complete previous tree.");
        Audit(false);
        if (SparseFile.AllocatedDigest(source) != sourceHash) throw new Exception("Cancellation changed source.");

        var crashRequest = request with { JournalDirectory = Path.Combine(dir, "crash-journal") };
        string crashJson = WriteRequest(crashRequest, "crash");
        bool stopped = false;
        var crashed = Execute("crash-copy", ["direct-run", "--request", crashJson], (e, child) => {
            if (!stopped && Phase(e) == "Direct data copy durable")
            {
                stopped = true; Console.WriteLine("Controller stopping synthetic CLI at durable copy event"); child.Kill();
            }
        }, killed: true);
        if (!stopped || crashed.CopyNodes != profile.Items.Count || crashed.HasTerminal || crashed.Exit == 0) throw new Exception("CLI crash checkpoint missed.");
        var crashState = DirectMigrator.Status(crashRequest.JournalDirectory);
        if (crashState.Phase is "complete" or "rolled-back") throw new Exception("Crash did not leave an unfinished transaction.");
        using (var disk = RegularImage.Open(request.Destination.Image))
            if (disk.ReadBytes(SparseFixture.Offset + 65536, 512)[0xD1] != 0) throw new Exception("Stopped target incorrectly clean.");
        string crashedHash = ProtectedHash();
        File.Move(source, source + ".held"); if (crypto != "plain") File.Move(key, key + ".held");
        try
        {
            var status = Execute("crash-status", ["direct-status", "--journal", crashRequest.JournalDirectory]); status.Check(0);
            if (!status.Result.GetProperty("recoveryRequired").GetBoolean() || status.Result.GetProperty("undoIntegrityValidated").GetBoolean()) throw new Exception("Crash status overstated integrity.");
            // Damage only this test's undo file, assert refusal before writes, then
            // restore the exact byte and independently validate its whole checksum.
            string undo = Path.Combine(crashRequest.JournalDirectory, "undo.bin"); byte saved;
            using (var file = new FileStream(undo, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { file.Position = 100; saved = checked((byte)file.ReadByte()); file.Position = 100; file.WriteByte((byte)(saved ^ 255)); file.Flush(true); }
            var refused = Execute("corrupt-undo-refused", ["direct-recover", "--journal", crashRequest.JournalDirectory]); refused.Check(4);
            if (!refused.Terminal.GetProperty("recoveryRequired").GetBoolean() ||
                refused.Terminal.GetProperty("error").GetProperty("type").GetString() != typeof(InvalidDataException).FullName || ProtectedHash() != crashedHash)
                throw new Exception("Corrupt journal recovery changed destination or lost structured error.");
            using (var file = new FileStream(undo, FileMode.Open, FileAccess.Write, FileShare.None)) { file.Position = 100; file.WriteByte(saved); file.Flush(true); }
            if (RegularImage.HashFile(undo) != crashState.UndoSha256) throw new Exception("Undo byte repair mismatch.");
            Execute("recover", ["direct-recover", "--journal", crashRequest.JournalDirectory]).Check(0);
            if (DirectMigrator.Status(crashRequest.JournalDirectory).Phase != "rolled-back" || ProtectedHash() != protectedHash) throw new Exception("CLI crash recovery did not restore protected bytes.");
            Execute("recover-again", ["direct-recover", "--journal", crashRequest.JournalDirectory]).Check(0);
        }
        finally { File.Move(source + ".held", source); if (crypto != "plain") File.Move(key + ".held", key); }
        Audit(false);
        Execute("verify-restored", ["direct-verify", "--journal", request.JournalDirectory]).Check(0);
        if (SparseFile.AllocatedDigest(source) != sourceHash) throw new Exception("Crash recovery changed source.");
        var journals = new[] { request, cancelRequest, crashRequest }.Select(r => {
            var state = DirectMigrator.Status(r.JournalDirectory); string undo = Path.Combine(r.JournalDirectory, "undo.bin");
            long length = new FileInfo(undo).Length;
            if (length != state.Plan.JournalBytes || length != state.UndoLength || RegularImage.HashFile(undo) != state.UndoSha256 ||
                Directory.GetFiles(r.JournalDirectory, "*.img", SearchOption.AllDirectories).Length != 0) throw new Exception("Undo integrity/space budget mismatch.");
            return new { Directory = r.JournalDirectory, state.Phase, UndoBytes = length, state.UndoSha256 };
        }).ToArray();
        host.Measure();
        var report = new {
            Crypto = crypto, SourceLogicalBytes = sourceBytes, DestinationLogicalBytes = targetBytes,
            SourceCylinderGroups = sourceFixture.Groups, DestinationCylinderGroups = targetFixture.Groups, FragmentBytes = SparseFixture.Frag,
            Nodes = profile.Items.Count, Accounts = PopulatedStressTests.Accounts,
            SaveSets = PopulatedStressTests.Accounts * PopulatedStressTests.SavesPerAccount,
            TrophySets = PopulatedStressTests.Accounts * PopulatedStressTests.TrophiesPerAccount, GameTrees = PopulatedStressTests.Games,
            RegularFiles = profile.Items.Count(n => n.Type == 8), Directories = profile.Items.Count(n => n.Type == 4), Symlinks = profile.Items.Count(n => n.Type == 10),
            LogicalRegularFileBytes = profile.Items.Where(n => n.Type == 8).Sum(n => n.Size),
            AllocatedFilePayloadBytes = profile.Items.Where(n => n.Type == 8).Sum(n => n.Data.Values.Sum(b => (long)b.Length)),
            AllocatedTreeBytes = plan.RequiredFragments * SparseFixture.Frag,
            SourceHighestAllocatedPayloadByte = sourceFixture.PayloadRanges.Max(r => r.Offset + r.Length - 1),
            SourceAllocatedRangesSha256 = sourceHash, RestoredProtectedRangesSha256 = protectedHash, complete.DestinationDigest,
            CancellationTriggeredAtNodes = cancelledAtNodes, CancellationTriggeredAtBytes = cancelledAtBytes,
            CrashJournalPhaseBeforeRecovery = crashState.Phase, CompleteCopy = "pass", LiveMonitoring = "pass", GracefulCancellation = "pass",
            CorruptUndoRefusalWithoutWrites = "pass", DestinationOnlyCrashRecovery = "pass", IndependentOracle = "pass",
            PeakCaseHostBytes = host.PeakCase, PeakRunHostBytes = host.PeakRun, HostBudgetBytes = HostMeter.Budget,
            StorageSamples = host.Samples, Journals = journals, Sessions = sessions,
            ElapsedSeconds = clock.Elapsed.TotalSeconds, PeakControllerWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            ProductionCliAndImageBackend = true, TestOnlyZeroBackend = false, FullyOccupiedTerabyteTransfer = false,
            PhysicalDriveAccess = false, ConsoleRuntimeValidated = false
        };
        File.WriteAllBytes(Path.Combine(dir, "cli-populated-report.json"), JsonSerializer.SerializeToUtf8Bytes(report, ImageMigrator.Json));
        // Only our two exact generated files, after every assertion passes. Keep
        // undo/state/logs and sparse fingerprints; never touch another run's images.
        foreach (string path in new[] { source, target }) { RegularImage.CheckedPath(path); File.Delete(path); }
        File.WriteAllText(Path.Combine(dir, "images-released.txt"), "Exact generated 1TB/2TB sparse images released after all CLI/oracle checks passed.\n");
        Console.WriteLine($"CLI populated {crypto} PASS in {clock.Elapsed.TotalSeconds:F1}s; sampled case peak {host.PeakCase / (double)(1L << 30):F2} GiB");
        GC.Collect(); GC.WaitForPendingFinalizers();
    }

    private static string? Phase(JsonElement e) => e.GetProperty("type").GetString() == "progress" ? e.GetProperty("data").GetProperty("progress").GetProperty("Phase").GetString() : null;

    internal static string RawHash(DirectEndpoint endpoint, IEnumerable<DiskRange> ranges)
    {
        if (endpoint.Kind != "image") throw new ArgumentException("Synthetic raw checker accepts image files only.");
        // Raw ciphertext checks, like actual recovery, need no encryption key.
        using var disk = new DirectDisk(endpoint with { KeyFile = null }, false);
        return DirectLayout.HashRanges(disk, ranges);
    }

    internal sealed class Session
    {
        public required string Name { get; init; }
        public required string Command { get; init; }
        public int ProcessId { get; set; }
        public int Exit { get; set; }
        public long Events { get; set; }
        public int Heartbeats { get; set; }
        public int ProgressEvents { get; set; }
        public double ElapsedSeconds { get; set; }
        public double MaxObservedEventGapSeconds { get; set; }
        public long CopyBytes { get; set; }
        public long CopyNodes { get; set; }
        public bool CopyCompletedBeforeCommit { get; set; }
        public bool HasTerminal { get; set; }
        public List<string> Phases { get; } = new();
        public JsonElement Terminal { get; set; } = JsonSerializer.SerializeToElement<object?>(null);
        [System.Text.Json.Serialization.JsonIgnore]
        public JsonElement Result => Terminal.GetProperty("result");
        internal void Check(int exit)
        {
            if (Exit != exit || !HasTerminal || Terminal.GetProperty("exitCode").GetInt32() != exit) throw new Exception($"CLI {Name} exit/terminal mismatch: {Exit}, expected {exit}.");
            if (exit == 0 && Terminal.TryGetProperty("result", out var result) && result.TryGetProperty("ConsoleRuntimeValidated", out var runtime) && runtime.GetBoolean()) throw new Exception("Runtime acceptance falsely claimed.");
        }
    }

    private static Session Invoke(string dir, string name, string[] arguments, HostMeter host, Action<JsonElement, Process>? trigger, bool killed)
    {
        string stdout = Path.Combine(dir, name + ".stdout.ndjson"), stderr = Path.Combine(dir, name + ".stderr.txt"), log = Path.Combine(dir, name + ".events.ndjson");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(typeof(CliOptions).Assembly.Location);
        foreach (string a in arguments) start.ArgumentList.Add(a);
        start.ArgumentList.Add("--json");
        if (arguments[0] != "cancel") foreach (string a in new[] { "--event-log", log, "--heartbeat-seconds", "0.5", "--progress-interval-ms", "100" }) start.ArgumentList.Add(a);
        using var child = Process.Start(start) ?? throw new Exception("CLI child failed to start."); child.StandardInput.Close();
        var session = new Session { Name = name, Command = arguments[0], ProcessId = child.Id };
        var watch = Stopwatch.StartNew(); long received = Stopwatch.GetTimestamp();
        var errors = child.StandardError.ReadToEndAsync();
        Task read = Task.Run(async () => {
            using var capture = new StreamWriter(new FileStream(stdout, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            string? line, runId = null, phase = null; long? completed = null; double elapsed = 0;
            while ((line = await child.StandardOutput.ReadLineAsync()) != null)
            {
                capture.WriteLine(line);
                var e = JsonDocument.Parse(line).RootElement;
                double gap = Stopwatch.GetElapsedTime(Interlocked.Exchange(ref received, Stopwatch.GetTimestamp())).TotalSeconds;
                session.MaxObservedEventGapSeconds = Math.Max(session.MaxObservedEventGapSeconds, gap);
                if (session.HasTerminal || e.GetProperty("schemaVersion").GetInt32() != 1 || e.GetProperty("sequence").GetInt64() != ++session.Events ||
                    !DateTimeOffset.TryParse(e.GetProperty("utc").GetString(), out _)) throw new Exception("Malformed/interleaved/trailing CLI event.");
                if (e.TryGetProperty("runId", out var id))
                { runId ??= id.GetString(); if (id.GetString() != runId || e.GetProperty("processId").GetInt32() != child.Id) throw new Exception("CLI session identity changed."); }
                if (e.TryGetProperty("elapsedSeconds", out var time))
                { double t = time.GetDouble(); if (t < elapsed) throw new Exception("Elapsed time regressed."); elapsed = t; }
                string type = e.GetProperty("type").GetString()!;
                if (type == "heartbeat") session.Heartbeats++;
                else if (type == "progress")
                {
                    session.ProgressEvents++; var p = e.GetProperty("data").GetProperty("progress"); string next = p.GetProperty("Phase").GetString()!;
                    if (next != phase) { phase = next; completed = null; session.Phases.Add(next); Console.WriteLine($"{name}: {next}"); }
                    if (p.GetProperty("Completed").ValueKind == JsonValueKind.Number)
                    { long n = p.GetProperty("Completed").GetInt64(); if (n < 0 || completed != null && n < completed) throw new Exception("Phase counter regressed."); completed = n; }
                    if (next == "copy-tree")
                    {
                        session.CopyBytes = p.GetProperty("Completed").GetInt64(); session.CopyNodes = p.GetProperty("NodesCompleted").GetInt64();
                        if (session.CopyNodes == p.GetProperty("NodesTotal").GetInt64()) session.CopyCompletedBeforeCommit = true;
                    }
                    if (next == "Direct commit durable" && !session.CopyCompletedBeforeCommit) throw new Exception("Commit reported before copy completion.");
                }
                else if (type is "result" or "error")
                {
                    if (type != (e.GetProperty("data").GetProperty("exitCode").GetInt32() == 0 ? "result" : "error")) throw new Exception("Terminal event type/exit mismatch.");
                    session.Terminal = e.GetProperty("data").Clone(); session.HasTerminal = true;
                }
                else if (type is not ("started" or "cancel-requested")) throw new Exception("Unknown CLI event type.");
                trigger?.Invoke(e, child);
            }
        });
        Console.WriteLine($"CLI child {name}: PID {child.Id}, {arguments[0]}");
        try
        {
            double sampled = -10;
            while (!child.WaitForExit(250))
            {
                if (read.IsFaulted) { child.Kill(); read.GetAwaiter().GetResult(); }
                if (watch.Elapsed.TotalSeconds - sampled >= 10) { host.Measure(); sampled = watch.Elapsed.TotalSeconds; }
                if (watch.Elapsed.TotalMinutes > 30 || Stopwatch.GetElapsedTime(Interlocked.Read(ref received)).TotalSeconds > 90)
                { child.Kill(); throw new Exception("Synthetic CLI watchdog: timeout or no event for 90s; images retained."); }
            }
            read.GetAwaiter().GetResult(); File.WriteAllText(stderr, errors.GetAwaiter().GetResult());
        }
        catch { if (!child.HasExited) child.Kill(); throw; }
        session.Exit = child.ExitCode; session.ElapsedSeconds = watch.Elapsed.TotalSeconds; host.Measure();
        if (new FileInfo(stderr).Length != 0) throw new Exception("Unexpected CLI stderr: " + File.ReadAllText(stderr));
        if (arguments[0] != "cancel" && RegularImage.HashFile(stdout) != RegularImage.HashFile(log)) throw new Exception("Live stdout and persistent event log differ.");
        if (!killed && !session.HasTerminal) throw new Exception("CLI exited without terminal event.");
        if (killed && session.HasTerminal) throw new Exception("Stopped CLI unexpectedly committed or handled cancellation.");
        return session;
    }

    internal sealed class HostMeter : IDisposable
    {
        internal const long Budget = 6L << 30;
        private readonly string _dir, _root, _source, _target;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly StreamWriter _samples;
        private readonly object _gate = new();
        internal long PeakCase, PeakRun, Samples;
        internal HostMeter(string dir, string source, string target)
        {
            _dir = dir; _root = Path.GetDirectoryName(dir)!; _source = source; _target = target;
            _samples = new StreamWriter(Path.Combine(dir, "host-storage.ndjson"), false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        internal void Measure()
        {
            lock (_gate)
            {
                string? src = File.Exists(_source) ? _source : File.Exists(_source + ".held") ? _source + ".held" : null;
                long images = (src == null ? 0 : SparseFile.Allocated(src)) + (File.Exists(_target) ? SparseFile.Allocated(_target) : 0);
                var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
                long other = files.Where(p => p != src && p != _target).Sum(Length);
                long ownOther = files.Where(p => p.StartsWith(_dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && p != src && p != _target).Sum(Length);
                long own = images + ownOther, total = images + other;
                PeakCase = Math.Max(PeakCase, own); PeakRun = Math.Max(PeakRun, total); Samples++;
                _samples.WriteLine(JsonSerializer.Serialize(new { elapsedSeconds = _clock.Elapsed.TotalSeconds, caseBytes = own, runBytes = total, allocatedImageBytes = images }));
                if (total > Budget) throw new Exception("CLI stress exceeded 6GiB allocated-images + host-artifacts run budget; images retained.");
                static long Length(string path)
                {
                    try { return path.EndsWith(".img", StringComparison.Ordinal) || path.EndsWith(".img.held", StringComparison.Ordinal) ? SparseFile.Allocated(path) : new FileInfo(path).Length; }
                    catch (FileNotFoundException) { return 0; }
                }
            }
        }
        public void Dispose() => _samples.Dispose();
    }
}
