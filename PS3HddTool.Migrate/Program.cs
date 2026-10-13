using System.Text.Json;
using PS3HddTool.Core.Migration;
using PS3HddTool.Migrate;

return MigrationCli.Run(args);

namespace PS3HddTool.Migrate
{
    internal static class MigrationCli
    {
        internal static int Run(string[] args)
        {
            CliOptions? options = null;
            EventReporter? reporter = null;
            string? journal = null;
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler? handler = null;
            try
            {
                options = CliOptions.Parse(args);
                if (options.Help) { Console.WriteLine(CliOptions.Usage); return 0; }
                if (options.Command == "version") { Console.WriteLine($"PS3HddTool.Migrate {typeof(MigrationCli).Assembly.GetName().Version} (CLI protocol 1)"); return 0; }
                if (options.Command == "cancel")
                {
                    string marker = RegularImage.CheckedPath(options.Required("cancel-file"), false);
                    using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    { file.Write(System.Text.Encoding.UTF8.GetBytes("cancel requested\n")); file.Flush(true); }
                    WriteStandalone(options.Json, "result", new { cancelFile = marker, requested = true, exitCode = 0 });
                    return 0;
                }
                DirectRequest? direct = null;
                MigrationRequest? image = null;
                string? package = null;
                string? preservationDirectory=null,exportPath=null;
                var protectedFiles = new List<string>();
                var protectedDirectories = new List<string>();
                if (options.Command is "direct-plan" or "direct-run")
                { direct = options.DirectRequest(); journal = direct.JournalDirectory; }
                else if(options.Command is "target-plan" or "target-preserve")direct=options.DirectRequest();
                else if(options.Command is "target-verify" or "target-export")
                {
                    preservationDirectory=RegularImage.CheckedPath(options.Input("preservation"),false);
                    protectedDirectories.Add(preservationDirectory);
                    if(options.Command=="target-export")
                    {exportPath=RegularImage.CheckedPath(options.Required("output"),false);protectedFiles.Add(exportPath);protectedFiles.Add(exportPath+".partial");}
                }
                else if (options.Command == "prepare") image = options.ImageRequest();
                else if (options.Command.StartsWith("direct-"))
                {
                    journal = RegularImage.CheckedPath(options.Input("journal"), false);
                    // Journal snapshot only: never open an endpoint/key here.
                    direct = DirectMigrator.Status(journal).Request;
                }
                else
                {
                    package = RegularImage.CheckedPath(options.Input("package"), false);
                    image = JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllBytes(RegularImage.CheckedPath(Path.Combine(package, "manifest.json"))), ImageMigrator.Json)?.Request
                        ?? throw new InvalidDataException("Missing manifest.");
                    protectedDirectories.Add(package);
                }
                if (direct != null)
                {
                    protectedFiles.AddRange(new[] { direct.Source.Path, direct.Destination.Path });
                    AddFile(direct.Source.KeyFile); AddFile(direct.Destination.KeyFile); AddFile(direct.SourceRegistry); AddFile(direct.TargetRegistry); AddFile(direct.SourceHashCache);
                    protectedDirectories.Add(direct.JournalDirectory);
                    if(direct.TargetPreservation!=null)protectedDirectories.Add(direct.TargetPreservation);
                }
                if (image != null)
                {
                    protectedFiles.AddRange(new[] { image.Source.Path, image.DestinationTemplate.Path });
                    AddFile(image.Source.KeyFile); AddFile(image.DestinationTemplate.KeyFile); AddFile(image.SourceRegistry); AddFile(image.TargetRegistry);
                    protectedDirectories.Add(image.OutputDirectory);
                }
                if (options.RequestPath != null) protectedFiles.Add(options.RequestPath);
                string? log = CheckAuxiliary(options.Get("event-log")), markerPath = CheckAuxiliary(options.Get("cancel-file"));
                if (log != null && log.Equals(markerPath, StringComparison.OrdinalIgnoreCase)) throw new CliUsageException("Event log and cancellation marker must differ.");
                bool recovering = options.Command == "direct-recover";
                reporter = new EventReporter(options, log, markerPath, cancellation, recovering);
                handler = (_, e) => { e.Cancel = true; reporter.RequestCancel("ctrl-c"); };
                Console.CancelKeyPress += handler;
                reporter.Start(); reporter.PollCancellation();
                if (!recovering) cancellation.Token.ThrowIfCancellationRequested();
                object result = options.Command switch
                {
                    "direct-plan" => DirectMigrator.Plan(direct!, cancellation.Token, reporter.Progress),
                    "direct-run" => Summary(DirectMigrator.Run(direct!, token: cancellation.Token, telemetry: reporter.Progress)),
                    "direct-verify" => Summary(DirectMigrator.Verify(journal!, cancellation.Token, reporter.Progress)),
                    "direct-resume-verification" => Summary(DirectMigrator.ResumeVerification(journal!, cancellation.Token, reporter.Progress)),
                    "direct-recover" => Summary(DirectMigrator.Recover(journal!, reporter.Progress)),
                    "direct-status" => Status(journal!),
                    "target-plan" => TargetPreservation.Plan(direct!,cancellation.Token,reporter.Progress),
                    "target-preserve" => TargetPreservation.Create(direct!,cancellation.Token,reporter.Progress),
                    "target-verify" => TargetPreservation.Verify(preservationDirectory!,cancellation.Token,reporter.Progress),
                    "target-export" => TargetPreservation.Export(preservationDirectory!,exportPath!,cancellation.Token,reporter.Progress),
                    "prepare" => Summary(ImageMigrator.Prepare(image!, token: cancellation.Token, telemetry: reporter.Progress)),
                    "verify" => Summary(ImageMigrator.Verify(package!, cancellation.Token, reporter.Progress)),
                    _ => throw new CliUsageException("Unknown command.")
                };
                return reporter.Result(result);

                void AddFile(string? path) { if (path != null) protectedFiles.Add(path); }
                string? CheckAuxiliary(string? path)
                {
                    if (path == null) return null;
                    string full = RegularImage.CheckedPath(path, false);
                    if (Directory.Exists(full)) throw new CliUsageException("Auxiliary paths must be regular files.");
                    foreach (string file in protectedFiles)
                    {
                        // Device names compared literally; no device attributes queried.
                        string candidate = file.StartsWith(@"\\.\", StringComparison.Ordinal) ? file : Path.GetFullPath(file);
                        if (full.Equals(candidate, StringComparison.OrdinalIgnoreCase)) throw new CliUsageException("Log/cancel path overlaps an input or disk endpoint.");
                    }
                    foreach (string dir in protectedDirectories)
                    {
                        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
                        if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new CliUsageException("Log/cancel file must be outside the journal/output/package directory.");
                    }
                    return full;
                }
            }
            catch (Exception error)
            {
                string? phase = null; bool? recoveryRequired = false;
                if (journal != null && Directory.Exists(journal))
                {
                    try { phase = DirectMigrator.Status(journal).Phase; recoveryRequired = phase is not ("complete" or "rolled-back"); }
                    catch { recoveryRequired = null; }
                }
                bool verificationPaused = error is VerificationPausedException && phase == "verification-paused";
                int exit = verificationPaused ? (reporter?.Failure == null ? 131 : 6) : recoveryRequired != false ? 4 : error is CliUsageException or JsonException ? 2 :
                    reporter?.Failure != null ? 6 : error is OperationCanceledException ? 130 : error is InvalidDataException ? 5 : 1;
                var data = new { exitCode = exit, error = Describe(error), journalDirectory = journal, journalPhase = phase,
                    recoveryRequired, verificationPaused, verificationResumeAvailable = phase is "verification-paused" or "verifying", reportingError = reporter?.Failure?.Message,
                    action = verificationPaused ? "Keep destination offline; retain journal. Use direct-resume-verification to restart checks, or direct-recover for rollback." :
                        recoveryRequired != false ? "Keep destination offline; retain journal. Inspect and use direct-recover before retrying." : null };
                string terminal = verificationPaused && reporter?.Failure == null ? "paused" : "error";
                if (reporter != null) reporter.Terminal(terminal, data);
                else WriteStandalone(options?.Json ?? args.Contains("--json"), terminal, data);
                return exit;
            }
            finally
            {
                if (handler != null) Console.CancelKeyPress -= handler;
                reporter?.Dispose();
            }
        }
        private static object Describe(Exception e) => new { type = e.GetType().FullName, message = e.Message, hresult = e.HResult,
            causes = (e is AggregateException a ? a.InnerExceptions.AsEnumerable() : e.InnerException == null ? [] : new[] { e.InnerException }).Select(Describe).ToArray() };
        private static object Summary(DirectState state) => new { state.Phase, state.Plan, state.DestinationDigest, state.ConsoleRuntimeValidated };
        private static object Summary(MigrationManifest m) => new { m.Nodes, m.ImageSha256, m.ConsoleStateRestoreRequired, m.ConsoleRuntimeValidated, m.SourceRecovery };
        private static object Status(string journal)
        {
            var state = DirectMigrator.Status(journal);
            string path = RegularImage.CheckedPath(Path.Combine(journal, "undo.bin"), false);
            long? length = File.Exists(path) ? new FileInfo(path).Length : null;
            return new { state.Phase, recoveryRequired = state.Phase is not ("complete" or "rolled-back"),
                verificationResumeAvailable = state.Phase is "verification-paused" or "verifying", state.Plan,
                expectedUndoLength = state.UndoLength, actualUndoLength = length, stateChecksumValidated = true,
                undoIntegrityValidated = false, processLivenessKnown = false, state.ConsoleRuntimeValidated };
        }
        private static void WriteStandalone(bool json, string type, object data)
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, type, sequence = 1, utc = DateTimeOffset.UtcNow, data }));
            else (type == "error" ? Console.Error : Console.Out).WriteLine(JsonSerializer.Serialize(data, ImageMigrator.Json));
        }
    }
}
