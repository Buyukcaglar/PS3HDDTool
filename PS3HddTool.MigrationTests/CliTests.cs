using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PS3HddTool.Core.Migration;
using PS3HddTool.Migrate;

internal static class CliTests
{
    private sealed record Invocation(int Exit, List<JsonElement> Events, string Stderr)
    {
        internal JsonElement Last => Events[^1].GetProperty("data");
        internal void Check(int code, string terminal)
        {
            if (Exit != code || Events.Count == 0 || Events[^1].GetProperty("type").GetString() != terminal)
                throw new Exception($"CLI exit {Exit}, expected {code}: {Stderr} {string.Join('\n', Events)}");
            for (int i = 0; i < Events.Count; i++)
            {
                var e = Events[i];
                if (e.GetProperty("schemaVersion").GetInt32() != 1 || e.GetProperty("sequence").GetInt64() != i + 1 ||
                    !DateTimeOffset.TryParse(e.GetProperty("utc").GetString(), out _)) throw new Exception("Invalid event envelope/order.");
                if (i < Events.Count - 1 && e.GetProperty("type").GetString() is "result" or "error") throw new Exception("Events after terminal.");
            }
            if (Last.GetProperty("exitCode").GetInt32() != code) throw new Exception("Terminal exit code mismatch.");
        }
    }
    // Only generated request/image paths are ever passed to the child executable.
    private static Invocation Invoke(string dir, IEnumerable<string> args, Action<JsonElement>? onEvent = null)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = dir };
        start.ArgumentList.Add(typeof(CliOptions).Assembly.Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new Exception("CLI child did not start.");
        var events = new List<JsonElement>();
        Task errors = child.StandardError.ReadToEndAsync().ContinueWith(t => File.WriteAllText(Path.Combine(dir, "cli-last.stderr.txt"), t.Result));
        Task read = Task.Run(async () => {
            string? line;
            while ((line = await child.StandardOutput.ReadLineAsync()) != null)
            {
                var e = JsonDocument.Parse(line).RootElement.Clone(); events.Add(e); onEvent?.Invoke(e);
            }
        });
        try
        {
            if (!child.WaitForExit(60000)) throw new Exception("Synthetic CLI timed out.");
            Task.WaitAll(read, errors);
        }
        catch { if (!child.HasExited) child.Kill(); throw; } // Synthetic children only.
        string stderr = File.ReadAllText(Path.Combine(dir, "cli-last.stderr.txt"));
        File.WriteAllLines(Path.Combine(dir, "cli-last.ndjson"), events.Select(e => e.GetRawText()));
        return new(child.ExitCode, events, stderr);
    }
    internal static void Run(Action<string, Action<string>> test, Func<Fixture> profile,
        Func<string, Fixture, Fixture, string, byte[]?, byte[]?, MigrationRequest> setup, byte[] registry)
    {
        test("verification-resume-cli-end-to-end", dir => {
            var old = new Fixture(8); old.Add("old",payload:Fixture.Payload(4096,91)); old.Finish(true);
            var r = setup(dir,profile(),old,"cbc192-bswap16",registry,registry);
            var request = new DirectRequest(new(r.Source.Path,32,r.Source.Crypto,r.Source.KeyFile),
                new(r.DestinationTemplate.Path,32,r.DestinationTemplate.Crypto,r.DestinationTemplate.KeyFile),
                Path.Combine(dir,"journal"),r.SourceRegistry,r.TargetRegistry);
            string file=Path.Combine(dir,"request.json"),marker=Path.Combine(dir,"pause.flag");
            File.WriteAllBytes(file,JsonSerializer.SerializeToUtf8Bytes(request,ImageMigrator.Json));
            bool requested=false;
            var paused=Invoke(dir,["direct-run","--request",file,"--json","--cancel-file",marker,"--progress-interval-ms","0"],e=>{
                if(!requested&&e.TryGetProperty("data",out var data)&&data.TryGetProperty("progress",out var p)&&p.GetProperty("Phase").GetString()=="Verify direct destination"){
                    requested=true;File.WriteAllText(marker,"pause");
                }
            });
            paused.Check(131,"paused");
            if(!requested||paused.Last.GetProperty("journalPhase").GetString()!="verification-paused"||!paused.Last.GetProperty("verificationPaused").GetBoolean())
                throw new Exception("CLI did not report a paused verification checkpoint.");
            var resumed=Invoke(dir,["direct-resume-verification","--journal",request.JournalDirectory,"--json","--progress-interval-ms","0"]);
            resumed.Check(0,"result");
            if(resumed.Events.Any(e=>e.GetProperty("type").GetString()=="progress"&&e.GetProperty("data").GetProperty("progress").GetProperty("Phase").GetString()=="source-hash"))
                throw new Exception("CLI resume rehashed source payload.");
            if(DirectMigrator.Status(request.JournalDirectory).Phase!="complete")throw new Exception("CLI resume did not complete.");
        });
        test("cli-stress-controller-contracts", dir => {
            var cancel = new CliPopulatedStressTests.Session { Name = "cancel", Command = "cancel", Exit = 0, HasTerminal = true,
                Terminal = JsonSerializer.SerializeToElement(new { exitCode = 0, cancelFile = "generated", requested = true }) };
            cancel.Check(0);
            _ = JsonSerializer.Serialize(new[] { cancel, new CliPopulatedStressTests.Session { Name = "stopped", Command = "direct-run" } });
            string current = Path.Combine(dir, "current"), previous = Path.Combine(dir, "previous"); Directory.CreateDirectory(current); Directory.CreateDirectory(previous);
            string a = Path.Combine(current, "source.img"), b = Path.Combine(current, "target.img"), c = Path.Combine(previous, "source.img"), d = Path.Combine(previous, "target.img");
            foreach (string path in new[] { a, b, c, d }) { SparseFile.Create(path, 1_000_000_000_000); using var stream = File.OpenWrite(path); stream.Write(Fixture.Payload(512, 71)); }
            using (var meter = new CliPopulatedStressTests.HostMeter(current, a, b))
            {
                meter.Measure();
                long allocated = new[] { a, b, c, d }.Sum(SparseFile.Allocated);
                if (meter.PeakRun < allocated || meter.PeakRun > 1024 * 1024) throw new Exception("Host meter counted logical capacity or omitted a previous case.");
            }
            if (CliPopulatedStressTests.RawHash(new(a, 0), [new(0, 512)]) !=
                CliPopulatedStressTests.RawHash(new(a, 0, "cbc192", Path.Combine(dir, "unavailable-generated.key")), [new(0, 512)]))
                throw new Exception("Raw recovery checker depended on an unavailable key.");
            foreach (string path in new[] { a, b, c, d }) File.Delete(path);
        });
        Fixture Old(int groups = 8) { var f = new Fixture(groups); f.Add("retained-on-rollback", payload: Fixture.Payload(17003, 48)); f.Finish(true); return f; }
        DirectRequest Direct(MigrationRequest r) => new(new(r.Source.Path, 32, r.Source.Crypto, r.Source.KeyFile),
            new(r.DestinationTemplate.Path, 32, r.DestinationTemplate.Crypto, r.DestinationTemplate.KeyFile),
            Path.Combine(Path.GetDirectoryName(r.OutputDirectory)!, "journal"), r.SourceRegistry, r.TargetRegistry);
        string WriteRequest(string dir, object r) { string path = Path.Combine(dir, "request.json"); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(r, ImageMigrator.Json)); return path; }
        void OracleResult(Fixture fixture, DirectState state, bool rollback = false)
        {
            using var disk = RegularImage.Open(new(state.Request.Destination.Path, 32, state.Request.Destination.Crypto, state.Request.Destination.KeyFile));
            var manifest = new MigrationManifest(1, new(new(state.Request.Source.Path, 32), new(state.Request.Destination.Path, 32), ""), fixture.Items.Count,
                rollback ? fixture.Items.Select(n => new InodeMapping(n.Ino, n.Ino)).ToList() : state.Mapping, "", null, false);
            Oracle.Check(fixture, disk.ReadBytes(0, (int)disk.TotalSize), manifest);
        }
        string[] Inline(DirectRequest r) => ["--source", r.Source.Path, "--destination", r.Destination.Path, "--source-sector", "32", "--destination-sector", "32",
            "--journal", r.JournalDirectory, "--crypto", r.Source.Crypto, "--source-registry", r.SourceRegistry!, "--target-registry", r.TargetRegistry!];
        test("cli-source-hash-cache-reuse",dir=>{
            var f=profile();var r=Direct(setup(dir,f,Old(),"cbc192-bswap16",registry,registry));
            string cache=Path.Combine(dir,"hashes.json");
            using(var disk=RegularImage.Open(r.Source.Image)){var v=new UfsVolume(disk,32);v.Inventory(true);
                File.WriteAllBytes(cache,JsonSerializer.SerializeToUtf8Bytes(new SourceHashSnapshot(1,DirectLayout.TreeDigest(v),v.Nodes.Values.Select(n=>new SourceHashNode(n.Number,n.Raw,n.Hash,n.Entries)).ToList()),ImageMigrator.Json));}
            r=r with{SourceHashCache=cache,SourceHashCacheSha256=RegularImage.HashFile(cache)};
            string request=WriteRequest(dir,r);
            Invoke(dir,["direct-run","--request",request,"--json","--event-log",cache]).Check(2,"error");
            var plan=Invoke(dir,["direct-plan",..Inline(r),"--key-file",r.Source.KeyFile!,"--source-hash-cache",cache,"--source-hash-cache-sha256",r.SourceHashCacheSha256!,"--json"]);plan.Check(0,"result");
            var run=Invoke(dir,["direct-run","--request",request,"--json","--progress-interval-ms","0"]);run.Check(0,"result");
            if(run.Events.Any(e=>e.GetProperty("type").GetString()=="progress"&&e.GetProperty("data").GetProperty("progress").GetProperty("Phase").GetString()=="source-hash"))throw new Exception("CLI repeated the source pre-hash pass");
            OracleResult(f,DirectMigrator.Status(r.JournalDirectory));
        });
        foreach (string crypto in new[] { "plain", "cbc192", "cbc192-bswap16", "xts128" })
            test("cli-direct-full-session-" + crypto, dir => {
                var f = profile(); var r = Direct(setup(dir, f, Old(), crypto, registry, registry)); string request = WriteRequest(dir, r);
                string source = RegularImage.HashFile(r.Source.Path), target = RegularImage.HashFile(r.Destination.Path);
                var inline = Inline(r).ToList(); if (r.Source.KeyFile != null) inline.AddRange(["--key-file", r.Source.KeyFile]);
                var plan = Invoke(dir, ["direct-plan", ..inline, "--json"]); plan.Check(0, "result");
                if (Directory.Exists(r.JournalDirectory) || source != RegularImage.HashFile(r.Source.Path) || target != RegularImage.HashFile(r.Destination.Path)) throw new Exception("CLI plan wrote data.");
                string log = Path.Combine(dir, "copy.ndjson");
                var copy = Invoke(dir, ["direct-run", "--request", request, "--json", "--event-log", log, "--progress-interval-ms", "0", "--heartbeat-seconds", "0.1"]); copy.Check(0, "result");
                if (copy.Stderr.Length != 0 || File.ReadAllLines(log).Length != copy.Events.Count) throw new Exception("NDJSON pollution or event log mismatch.");
                var progress = copy.Events.Where(e => e.GetProperty("type").GetString() == "progress").Select(e => e.GetProperty("data").GetProperty("progress")).ToList();
                var copied = progress.Last(e => e.GetProperty("Phase").GetString() == "copy-tree");
                if (copied.GetProperty("Completed").GetInt64() != copied.GetProperty("Total").GetInt64() || copied.GetProperty("NodesCompleted").GetInt64() != f.Items.Count ||
                    !progress.Any(e => e.GetProperty("Phase").GetString() == "Direct commit durable")) throw new Exception("Copy/commit progress missing.");
                if (copy.Events[^1].GetProperty("data").GetProperty("result").GetProperty("ConsoleRuntimeValidated").GetBoolean()) throw new Exception("Runtime falsely claimed.");
                var state = DirectMigrator.Status(r.JournalDirectory); OracleResult(f, state);
                Invoke(dir, ["direct-verify", r.JournalDirectory, "--json"]).Check(0, "result");
                if (source != RegularImage.HashFile(r.Source.Path)) throw new Exception("Source changed.");
                // Status still works with both image files and generated key unavailable.
                File.Move(r.Source.Path, r.Source.Path + ".held"); File.Move(r.Destination.Path, r.Destination.Path + ".held");
                if (r.Source.KeyFile != null) File.Move(r.Source.KeyFile, r.Source.KeyFile + ".held");
                var status = Invoke(dir, ["direct-status", "--journal", r.JournalDirectory, "--json"]); status.Check(0, "result");
                if (status.Last.GetProperty("result").GetProperty("undoIntegrityValidated").GetBoolean() || status.Last.GetProperty("result").GetProperty("processLivenessKnown").GetBoolean()) throw new Exception("Status overstated evidence.");
                File.Move(r.Source.Path + ".held", r.Source.Path); File.Move(r.Destination.Path + ".held", r.Destination.Path);
                if (r.Source.KeyFile != null) File.Move(r.Source.KeyFile + ".held", r.Source.KeyFile);
                Invoke(dir, ["direct-recover", r.JournalDirectory, "--json"]).Check(1, "error");
            });
        test("cli-prepare-verify-legacy-and-inline", dir => {
            var f = profile(); var r = setup(dir, f, Old(), "plain", registry, registry); string file = WriteRequest(dir, r);
            Invoke(dir, ["prepare", file, "--json"]).Check(0, "result");
            Invoke(dir, ["verify", "--package", r.OutputDirectory, "--json"]).Check(0, "result");
            var inline = Direct(r); var args = Inline(inline).ToList(); int i = args.IndexOf("--journal"); args[i] = "--output"; args[i + 1] = r.OutputDirectory + "-inline";
            Invoke(dir, ["prepare", ..args, "--json"]).Check(0, "result");
        });
        test("cli-usage-and-input-isolation", dir => {
            var r = Direct(setup(dir, profile(), Old(), "plain", registry, registry)); string file = WriteRequest(dir, r);
            string before = RegularImage.HashFile(r.Destination.Path);
            foreach (string[] args in new string[][] {
                ["direct-run", "--request", file, "--surprise", "x", "--json"],
                ["direct-run", "--request", file, "--source-sector", "0", "--json"],
                ["direct-run", file, "--event-log", Path.Combine(r.JournalDirectory, "events"), "--json"],
                ["direct-run", file, "--cancel-file", r.Source.Path, "--json"],
                ["direct-run", file, "--heartbeat-seconds", "NaN", "--json"],
                ["direct-run", file, "--progress-interval-ms", "0.5", "--json"],
                ["direct-run", "--source", "x", "--destination", "y", "--source-sector", "-1", "--destination-sector", "32", "--journal", r.JournalDirectory, "--json"],
                ["direct-run", file, "--request", file, "--json"] }) Invoke(dir, args).Check(2, "error");
            string log = Path.Combine(dir, "existing.log"); File.WriteAllText(log, "keep me");
            Invoke(dir, ["direct-run", file, "--event-log", log, "--json"]).Check(1, "error");
            if (File.ReadAllText(log) != "keep me") throw new Exception("Log overwritten.");
            File.WriteAllText(file, "{bad-json"); Invoke(dir, ["direct-run", file, "--json"]).Check(2, "error");
            foreach (string malformed in new[] {
                JsonSerializer.Serialize(r, ImageMigrator.Json).Replace("\"PartitionSector\": 32,", "", StringComparison.Ordinal),
                JsonSerializer.Serialize(r, ImageMigrator.Json).Replace("\"MaxJournalBytes\"", "\"MaxJournalBytse\"", StringComparison.Ordinal),
                "{\"Source\":null}",
                "{\"Source\":{},\"source\":{}}" })
            { File.WriteAllText(file, malformed); Invoke(dir, ["direct-run", file, "--json"]).Check(2, "error"); }
            if (before != RegularImage.HashFile(r.Destination.Path) || Directory.Exists(r.JournalDirectory)) throw new Exception("Bad CLI request wrote destination/journal.");
        });
        test("cli-preexisting-cancel-marker", dir => {
            var r = Direct(setup(dir, profile(), Old(), "plain", registry, registry)); string file = WriteRequest(dir, r);
            string marker = Path.Combine(dir, "stop"); Invoke(dir, ["cancel", "--cancel-file", marker, "--json"]).Check(0, "result");
            string before = RegularImage.HashFile(r.Destination.Path);
            var cancelled = Invoke(dir, ["direct-run", file, "--cancel-file", marker, "--json"]); cancelled.Check(130, "error");
            if (before != RegularImage.HashFile(r.Destination.Path) || Directory.Exists(r.JournalDirectory)) throw new Exception("Cancelled session wrote anything.");
            Invoke(dir, ["cancel", "--cancel-file", marker, "--json"]).Check(1, "error");
        });
        test("cli-live-cancellation-and-rollback", dir => {
            var f = new Fixture(16); f.Add("game", 4); f.Add("game/payload", payload: Fixture.Payload(8 * 1024 * 1024, 903)); f.Finish();
            var old = Old(32); var r = Direct(setup(dir, f, old, "plain", null, null)); string file = WriteRequest(dir, r);
            string marker = Path.Combine(dir, "stop"); bool sent = false;
            var result = Invoke(dir, ["direct-run", file, "--json", "--cancel-file", marker, "--heartbeat-seconds", "0.1", "--progress-interval-ms", "0"], e => {
                if (!sent && e.GetProperty("type").GetString() == "progress" && e.GetProperty("data").GetProperty("progress").GetProperty("Phase").GetString() == "copy-tree")
                { sent = true; File.WriteAllText(marker, "cancel"); }
            });
            result.Check(130, "error");
            if (!sent || result.Last.GetProperty("journalPhase").GetString() != "rolled-back" || result.Last.GetProperty("recoveryRequired").GetBoolean()) throw new Exception("Cancellation rollback not confirmed.");
            if (!result.Events.Any(e => e.GetProperty("type").GetString() == "progress" && e.GetProperty("data").GetProperty("progress").GetProperty("Phase").GetString() == "rollback-metadata")) throw new Exception("Rollback progress missing.");
            OracleResult(old, DirectMigrator.Status(r.JournalDirectory), true);
        });
        test("cli-recovery-without-source-or-generated-key", dir => {
            var old = Old(); var r = Direct(setup(dir, profile(), old, "xts128", registry, registry));
            try { DirectMigrator.Run(r, s => { if (s == "Direct data copy durable") throw new SimulatedProcessLossException("synthetic"); }); }
            catch (SimulatedProcessLossException) { }
            File.Move(r.Source.Path, r.Source.Path + ".held"); File.Move(r.Source.KeyFile!, r.Source.KeyFile + ".held");
            Invoke(dir, ["direct-status", r.JournalDirectory, "--json"]).Check(0, "result");
            var recovered = Invoke(dir, ["direct-recover", "--journal", r.JournalDirectory, "--json"]); recovered.Check(0, "result");
            if (recovered.Last.GetProperty("result").GetProperty("Phase").GetString() != "rolled-back") throw new Exception("Recovery did not finish.");
            File.Move(r.Source.KeyFile + ".held", r.Source.KeyFile!); OracleResult(old, DirectMigrator.Status(r.JournalDirectory), true);
        });
        test("cli-corrupt-undo-recovery-required", dir => {
            var r = Direct(setup(dir, profile(), Old(), "plain", registry, registry));
            try { DirectMigrator.Run(r, s => { if (s == "Direct data copy durable") throw new SimulatedProcessLossException("synthetic"); }); } catch (SimulatedProcessLossException) { }
            string undo = Path.Combine(r.JournalDirectory, "undo.bin"); using (var f = File.OpenWrite(undo)) { f.Position = 100; f.WriteByte(255); }
            string before = RegularImage.HashFile(r.Destination.Path);
            var result = Invoke(dir, ["direct-recover", r.JournalDirectory, "--json"]); result.Check(4, "error");
            if (!result.Last.GetProperty("recoveryRequired").GetBoolean() || result.Last.GetProperty("error").GetProperty("type").GetString() != typeof(InvalidDataException).FullName || before != RegularImage.HashFile(r.Destination.Path)) throw new Exception("Recovery risk/error lost or destination changed.");
        });
        test("cli-nested-rollback-error-and-later-recovery", dir => {
            var old = Old(); var r = Direct(setup(dir, profile(), old, "plain", registry, registry)); string request = WriteRequest(dir, r);
            FileStream? blockStateReplacement = null;
            Invocation failed;
            try
            {
                failed = Invoke(dir, ["direct-run", request, "--json"], e => {
                    if (blockStateReplacement == null && e.GetProperty("type").GetString() == "progress" &&
                        e.GetProperty("data").GetProperty("progress").GetProperty("Phase").GetString() == "copy-tree")
                        blockStateReplacement = new FileStream(Path.Combine(r.JournalDirectory, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                });
            }
            finally { blockStateReplacement?.Dispose(); }
            failed.Check(4, "error");
            if (failed.Last.GetProperty("error").GetProperty("causes").GetArrayLength() != 2 || !failed.Last.GetProperty("recoveryRequired").GetBoolean())
                throw new Exception("Original failure/rollback failure were not both reported.");
            Invoke(dir, ["direct-recover", r.JournalDirectory, "--json"]).Check(0, "result");
            OracleResult(old, DirectMigrator.Status(r.JournalDirectory), true);
        });
        test("cli-package-integrity-error", dir => {
            var r = setup(dir, profile(), Old(), "plain", registry, registry); string request = WriteRequest(dir, r);
            Invoke(dir, ["prepare", request, "--json"]).Check(0, "result");
            using (var file = File.OpenWrite(Path.Combine(r.OutputDirectory, "disk.img"))) file.WriteByte(0);
            Invoke(dir, ["verify", r.OutputDirectory, "--json"]).Check(5, "error");
        });
        test("cli-legacy-human-output", dir => {
            var r = Direct(setup(dir, profile(), Old(), "plain", registry, registry)); string request = WriteRequest(dir, r);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(CliOptions).Assembly.Location); start.ArgumentList.Add("direct-plan"); start.ArgumentList.Add(request);
            using var p = Process.Start(start)!; var output = p.StandardOutput.ReadToEndAsync(); var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) { p.Kill(); throw new Exception("Synthetic human CLI timed out."); }
            Task.WaitAll(output, errors);
            if (p.ExitCode != 0 || JsonDocument.Parse(output.Result).RootElement.GetProperty("JournalBytes").GetInt64() <= 0 || string.IsNullOrEmpty(errors.Result))
                throw new Exception("Legacy stdout result/stderr progress changed.");
        });
        test("cli-heartbeat-protocol-and-reporting-failure", dir => {
            var options = CliOptions.Parse(["direct-run", "request.json", "--json", "--heartbeat-seconds", "0.1"]);
            var savedOut = Console.Out; var savedError = Console.Error;
            using var cancel = new CancellationTokenSource(); var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                using (var reporter = new EventReporter(options, Path.Combine(dir, "heartbeat.ndjson"), null, cancel, false))
                {
                    reporter.Start(); reporter.Progress(new("test", "Synthetic blocked stage", 512, 1024, "bytes"));
                    Thread.Sleep(250); reporter.Terminal("result", new { exitCode = 0 });
                }
                var events = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToList();
                new Invocation(0, events, "").Check(0, "result");
                if (!events.Any(e => e.GetProperty("type").GetString() == "heartbeat")) throw new Exception("No live heartbeat.");
                Console.SetOut(new BrokenWriter()); Console.SetError(new StringWriter());
                using var bad = new EventReporter(options, Path.Combine(dir, "failed-pipe.ndjson"), null, cancel, false);
                bad.Start(); bad.Progress(new("rollback-metadata", "Should remain observable in log")); bad.Terminal("error", new { exitCode = 6 });
                if (!cancel.IsCancellationRequested || bad.Failure == null) throw new Exception("Reporting failure did not cancel safely.");
                using var recoveryCancel = new CancellationTokenSource();
                Console.SetOut(new StringWriter());
                using var recovery = new EventReporter(options, Path.Combine(dir, "deferred-cancel.ndjson"), null, recoveryCancel, true);
                recovery.Start(); recovery.RequestCancel("synthetic-ctrl-c"); recovery.Terminal("result", new { exitCode = 0 });
                if (recoveryCancel.IsCancellationRequested) throw new Exception("Recovery was interrupted by Ctrl+C.");
            }
            finally { Console.SetOut(savedOut); Console.SetError(savedError); }
        });
        test("cli-observer-failure-cannot-abort-rollback", dir => {
            var old = Old(); var r = Direct(setup(dir, profile(), old, "plain", registry, registry)); int called = 0;
            try { DirectMigrator.Run(r, s => { if (s == "Direct data copy durable") throw new IOException("synthetic failure"); }, telemetry: p => { called++; throw new IOException("broken observer"); }); }
            catch (IOException) { }
            if (called == 0 || DirectMigrator.Status(r.JournalDirectory).Phase != "rolled-back") throw new Exception("Observer interfered with rollback.");
            OracleResult(old, DirectMigrator.Status(r.JournalDirectory), true);
        });
        test("cli-reporting-failure-during-copy-rolls-back", dir => {
            var old = Old(); var r = Direct(setup(dir, profile(), old, "plain", registry, registry));
            var options = CliOptions.Parse(["direct-run", "request.json", "--json"]);
            using var cancel = new CancellationTokenSource(); var savedOut = Console.Out; var savedError = Console.Error;
            try
            {
                Console.SetOut(new StringWriter()); Console.SetError(new StringWriter());
                using var reporter = new EventReporter(options, Path.Combine(dir, "failed-copy.ndjson"), null, cancel, false); reporter.Start();
                try { DirectMigrator.Run(r, token: cancel.Token, telemetry: p => {
                    if (p.Phase == "copy-tree") Console.SetOut(new BrokenWriter()); reporter.Progress(p);
                }); throw new Exception("Reporting failure did not interrupt copy."); }
                catch (OperationCanceledException) { }
                if (reporter.Failure == null || DirectMigrator.Status(r.JournalDirectory).Phase != "rolled-back") throw new Exception("Reporting failure prevented rollback.");
                reporter.Terminal("error", new { exitCode = 6 }); OracleResult(old, DirectMigrator.Status(r.JournalDirectory), true);
            }
            finally { Console.SetOut(savedOut); Console.SetError(savedError); }
        });
    }
    private sealed class BrokenWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("Synthetic broken output pipe.");
    }
}
