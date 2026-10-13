using System.Diagnostics;
using System.Text.Json;
using PS3HddTool.Core.Migration;

static class LargeSparseTests
{
    internal static void Run(string dir, Fixture profile, byte[] registry, string crypto, bool compactZeros = false, bool populated = false)
    {
        if (crypto is not ("plain" or "cbc192" or "cbc192-bswap16" or "xts128")) throw new ArgumentException("Unknown generated-key mode.");
        const long sourceBytes = 1_000_000_000_000, destinationBytes = 2_000_000_000_000;
        var watch = Stopwatch.StartNew();
        using var heartbeat = new Timer(_ => Console.WriteLine($"Large synthetic test active: {watch.Elapsed.TotalSeconds:F0}s"), null, 30000, 30000);
        string key = Path.Combine(dir, "generated.key"), sourcePath = Path.Combine(dir, "source-1TB.img"), targetPath = Path.Combine(dir, "target-2TB.img");
        if (crypto != "plain") File.WriteAllBytes(key, Fixture.Payload(crypto is "cbc192" or "cbc192-bswap16" ? 24 : 32, 99));
        DirectEndpoint Endpoint(string path) => new(path, SparseFixture.Offset / 512, crypto, crypto == "plain" ? null : key);
        var sourceFixture = new SparseFixture(sourceBytes); var targetFixture = new SparseFixture(destinationBytes);
        Console.WriteLine("Create sparse 1,000,000,000,000-byte source with far-end allocated payloads");
        sourceFixture.Create(Endpoint(sourcePath), profile, compactZeros);
        var old = new Fixture(); old.Add("old-directory", 4); old.Add("old-directory/old-payload", payload: Fixture.Payload(17003, 711)); old.Finish();
        Console.WriteLine("Create sparse 2,000,000,000,000-byte destination with complete allocation maps");
        targetFixture.Create(Endpoint(targetPath), old);
        string registryPath = Path.Combine(dir, "generated-registry.bin"); File.WriteAllBytes(registryPath, registry);
        var request = new DirectRequest(Endpoint(sourcePath), Endpoint(targetPath), Path.Combine(dir, "journal"), registryPath, registryPath);
        File.WriteAllBytes(Path.Combine(dir, "direct-request.json"), JsonSerializer.SerializeToUtf8Bytes(request, ImageMigrator.Json));
        long sourceAllocated = SparseFile.Allocated(sourcePath), targetAllocatedBefore = SparseFile.Allocated(targetPath);
        if (sourceAllocated > 2L * 1024 * 1024 * 1024 || targetAllocatedBefore > 2L * 1024 * 1024 * 1024)
            throw new Exception("Sparse file unexpectedly allocated gigabytes of host space.");
        if (new FileInfo(sourcePath).Length != sourceBytes || new FileInfo(targetPath).Length != destinationBytes ||
            sourceFixture.PayloadRanges.Max(r => r.Offset) < 990_000_000_000)
            throw new Exception("Full-capacity/far-end fixture boundary not exercised.");
        string sourceDigest = SparseFile.AllocatedDigest(sourcePath), targetBeforePlan = SparseFile.AllocatedDigest(targetPath);
        Console.WriteLine("Plan full-capacity filesystems (read-only)"); var plan = DirectMigrator.Plan(request);
        if (Directory.Exists(request.JournalDirectory) || SparseFile.AllocatedDigest(targetPath) != targetBeforePlan)
            throw new Exception("Large planning changed the destination.");
        int snapshots = 0;
        long peakHost = 0;
        void MeasureHost()
        {
            long images = SparseFile.Allocated(sourcePath) + SparseFile.Allocated(targetPath);
            long other = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".img", StringComparison.Ordinal)).Sum(p => new FileInfo(p).Length);
            peakHost = Math.Max(peakHost, images + other);
            if (images + other > 6L * 1024 * 1024 * 1024) throw new Exception("Stress fixture exceeded 6 GiB host storage budget.");
        }
        void Progress(string stage)
        {
            if (stage == "Journal snapshot chunk") { if (++snapshots % 2000 == 0) Console.WriteLine($"Metadata snapshots: {snapshots}"); }
            else if (stage.StartsWith("Copy inode"))
            { if (populated && long.Parse(stage.AsSpan(11)) % 2000 == 0) { Console.WriteLine(stage); MeasureHost(); } }
            else { Console.WriteLine(stage); MeasureHost(); }
        }
        var opened = new List<SparseZeroDisk>();
        IIdentifiedDisk Open(DirectEndpoint endpoint, bool writable)
        { var disk = new SparseZeroDisk(endpoint, writable); opened.Add(disk); return disk; }
        Console.WriteLine("Migrate full-capacity sparse files");
        var complete = compactZeros ? DirectMigrator.RunCore(request, Progress, default, Open) : DirectMigrator.Run(request, Progress);
        Console.WriteLine("Reopen and verify completed migration"); DirectMigrator.Verify(request.JournalDirectory);
        var payload = new List<DiskRange>();
        MigrationManifest Manifest(List<InodeMapping> mapping) => new(1,
            new(request.Source.Image, request.Destination.Image, request.JournalDirectory), profile.Items.Count, mapping, "", null, false);
        Console.WriteLine("Independent raw inode/directory/payload/map/summary audit over 2TB filesystem");
        using (var disk = RegularImage.Open(request.Destination.Image))
        {
            byte[] sb = disk.ReadBytes(SparseFixture.Offset + 65536, 8192);
            if (Fixture.R64(sb, 0x438) != targetFixture.Fragments || Fixture.R32(sb, 0x2C) != targetFixture.Groups)
                throw new Exception("Destination does not retain its full 2TB filesystem geometry.");
            Oracle.Check(profile, disk.ReadBytes, SparseFixture.Offset, Manifest(complete.Mapping),
                (at, count) => payload.Add(new(SparseFixture.Offset + at * SparseFixture.Frag, count * SparseFixture.Frag)));
        }
        var protectedRanges = DirectLayout.Merge(complete.Metadata.Concat(payload).Concat(complete.Outside));
        string protectedDigest;
        using (var disk = new DirectDisk(request.Destination, false)) protectedDigest = DirectLayout.HashRanges(disk, protectedRanges);
        if (sourceDigest != SparseFile.AllocatedDigest(sourcePath)) throw new Exception("Large source file changed.");
        var interrupted = request with { JournalDirectory = Path.Combine(dir, "interrupted-journal") };
        string childRequest = Path.Combine(dir, "interrupted-request.json");
        File.WriteAllBytes(childRequest, JsonSerializer.SerializeToUtf8Bytes(interrupted, ImageMigrator.Json));
        Console.WriteLine("Abrupt child exit during second direct copy into same 2TB destination");
        var launch = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        launch.ArgumentList.Add(typeof(LargeSparseTests).Assembly.Location); launch.ArgumentList.Add("--synthetic-direct-exit");
        launch.ArgumentList.Add(childRequest); launch.ArgumentList.Add("Direct data copy durable");
        if (compactZeros) launch.ArgumentList.Add("--compact-generated-zero-blocks");
        using (var child = Process.Start(launch)!)
        {
            if (!child.WaitForExit(600000)) { child.Kill(); throw new Exception("Large synthetic child timed out."); }
            if (child.ExitCode != 73) throw new Exception("Large child missed abrupt-exit boundary: " + child.ExitCode);
        }
        using (var disk = RegularImage.Open(request.Destination.Image))
            if (disk.ReadBytes(SparseFixture.Offset + 65536, 512)[0xD1] != 0) throw new Exception("Interrupted target is clean.");
        // Renames are exact generated files within this test directory, never huge-file copies.
        File.Move(sourcePath, sourcePath + ".held"); if (crypto != "plain") File.Move(key, key + ".held");
        try { Console.WriteLine("Recover 2TB destination with source/key unavailable"); DirectMigrator.Recover(interrupted.JournalDirectory); }
        finally { File.Move(sourcePath + ".held", sourcePath); if (crypto != "plain") File.Move(key + ".held", key); }
        using (var disk = new DirectDisk(request.Destination, false))
            if (DirectLayout.HashRanges(disk, protectedRanges) != protectedDigest) throw new Exception("Large rollback did not restore metadata and old allocated payloads.");
        using (var disk = RegularImage.Open(request.Destination.Image)) Oracle.Check(profile, disk.ReadBytes, SparseFixture.Offset, Manifest(complete.Mapping));
        if (sourceDigest != SparseFile.AllocatedDigest(sourcePath)) throw new Exception("Large recovery changed source.");
        MeasureHost();
        long journalBytes = new FileInfo(Path.Combine(request.JournalDirectory, "undo.bin")).Length;
        if (journalBytes != plan.JournalBytes || Directory.GetFiles(request.JournalDirectory, "*.img", SearchOption.AllDirectories).Length != 0)
            throw new Exception("Large undo budget/full-image boundary.");
        var report = new {
            SourceLogicalBytes = sourceBytes, DestinationLogicalBytes = destinationBytes, Crypto = crypto,
            SourceFilesystemFragments = sourceFixture.Fragments, DestinationFilesystemFragments = targetFixture.Fragments,
            FragmentBytes = SparseFixture.Frag, SourceCylinderGroups = sourceFixture.Groups, DestinationCylinderGroups = targetFixture.Groups,
            Nodes = profile.Items.Count, AllocatedTreeFragments = plan.RequiredFragments,
            PopulatedWorkload = populated, Accounts = populated ? PopulatedStressTests.Accounts : 1,
            SaveSets = populated ? PopulatedStressTests.Accounts * PopulatedStressTests.SavesPerAccount : 1,
            TrophySets = populated ? PopulatedStressTests.Accounts * PopulatedStressTests.TrophiesPerAccount : 1,
            GameTrees = populated ? PopulatedStressTests.Games : 1,
            RegularFiles = profile.Items.Count(n => n.Type == 8), Directories = profile.Items.Count(n => n.Type == 4),
            LogicalRegularFileBytes = profile.Items.Where(n => n.Type == 8).Sum(n => n.Size),
            AllocatedFilePayloadBytes = profile.Items.Where(n => n.Type == 8).Sum(n => n.Data.Values.Sum(b => (long)b.Length)),
            PatternedZeroPayloadBytes = profile.Items.Where(n => n.Type == 8).Sum(n => n.Data.Values.Where(b => b.AsSpan().IndexOfAnyExcept((byte)0) < 0).Sum(b => (long)b.Length)),
            TestOnlyZeroCompactingBackend = compactZeros,
            MigrationBackendReadBytes = compactZeros ? opened.Sum(d => d.ReadByteCount) : (long?)null,
            MigrationBackendWriteBytes = compactZeros ? opened.Sum(d => d.WriteByteCount) : (long?)null,
            MigrationBackendZeroWriteBytes = compactZeros ? opened.Sum(d => d.ZeroWriteByteCount) : (long?)null,
            MeasuredPeakHostBytes = peakHost, HostBudgetBytes = 6L * 1024 * 1024 * 1024,
            SourceHighestAllocatedPayloadByte = sourceFixture.PayloadRanges.Max(r => r.Offset + r.Length - 1),
            SourceHostAllocatedBytes = sourceAllocated, DestinationHostAllocatedBytesBefore = targetAllocatedBefore,
            DestinationHostAllocatedBytesAfter = SparseFile.Allocated(targetPath), UndoBytesPerSession = journalBytes,
            SourceAllocatedRangesSha256 = sourceDigest, RestoredMetadataAndPayloadSha256 = protectedDigest,
            DestinationTreeSha256 = complete.DestinationDigest, Migration = complete.Phase,
            CrashRecovery = DirectJournal.Load(interrupted.JournalDirectory).Phase, IndependentOracle = "pass",
            ElapsedSeconds = watch.Elapsed.TotalSeconds, PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            PhysicalDriveAccess = false, ConsoleRuntimeValidated = false, FullyPopulatedTransfer = false
        };
        File.WriteAllBytes(Path.Combine(dir, "large-report.json"), JsonSerializer.SerializeToUtf8Bytes(report, ImageMigrator.Json));
        Console.WriteLine(JsonSerializer.Serialize(report, ImageMigrator.Json));
    }
}
