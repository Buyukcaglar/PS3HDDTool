using System.Text.Json;
using System.Diagnostics;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.Migration;

static class DirectTests
{
    internal static void Run(Action<string, Action<string>> test, Func<Fixture> profile,
        Func<string, Fixture, Fixture, string, byte[]?, byte[]?, MigrationRequest> setup, byte[] registry)
    {
        Fixture Old()
        {
            var f = new Fixture(8); f.Add("old-directory", 4); f.Add("old-directory/keep-on-rollback", payload: Fixture.Payload(17003, 711));
            f.Finish(true); return f;
        }
        DirectRequest Request(MigrationRequest r) => new(new(r.Source.Path, r.Source.PartitionSector, r.Source.Crypto, r.Source.KeyFile),
            new(r.DestinationTemplate.Path, r.DestinationTemplate.PartitionSector, r.DestinationTemplate.Crypto, r.DestinationTemplate.KeyFile),
            Path.Combine(Path.GetDirectoryName(r.OutputDirectory)!, "journal"), r.SourceRegistry, r.TargetRegistry);
        void Expect<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        MigrationManifest OracleManifest(DirectState state, Fixture fixture, bool old = false) => new(1,
            new(state.Request.Source.Image, state.Request.Destination.Image, state.Request.JournalDirectory), fixture.Items.Count,
            old ? fixture.Items.Select(n => new InodeMapping(n.Ino, n.Ino)).ToList() : state.Mapping, "", null, false);
        void CheckRollback(Fixture fixture, DirectRequest request, string sourceHash, byte[] originalRaw)
        {
            var state = DirectJournal.Load(request.JournalDirectory);
            if (state.Phase != "rolled-back") throw new Exception("Rollback did not finish.");
            if (RegularImage.HashFile(request.Source.Path) != sourceHash) throw new Exception("Source changed.");
            byte[] raw = File.ReadAllBytes(request.Destination.Path);
            Oracle.Equal(originalRaw.AsSpan(0, Fixture.Prefix).ToArray(), raw.AsSpan(0, Fixture.Prefix).ToArray(), "rollback prefix");
            Oracle.Equal(originalRaw.AsSpan(originalRaw.Length - 4096).ToArray(), raw.AsSpan(raw.Length - 4096).ToArray(), "rollback other partition");
            using var disk = RegularImage.Open(request.Destination.Image);
            Oracle.Check(fixture, disk.ReadBytes(0, (int)disk.TotalSize), OracleManifest(state, fixture, true));
            // Independent oracle audits ORIGINAL allocated payloads, metadata and maps.
            // Unallocated bytes may retain partial migration payload; no full-disk undo is claimed.
        }
        foreach (string crypto in new[] { "plain", "cbc192", "cbc192-bswap16", "xts128" })
            test("direct-profile-" + crypto, dir => {
                var f = profile(); var old = Old(); var r = Request(setup(dir, f, old, crypto, registry, registry));
                byte[] original = File.ReadAllBytes(r.Destination.Path); string sourceHash = RegularImage.HashFile(r.Source.Path);
                var plan = DirectMigrator.Plan(r);
                if (Directory.Exists(r.JournalDirectory) || sourceHash != RegularImage.HashFile(r.Source.Path) ||
                    !original.AsSpan().SequenceEqual(File.ReadAllBytes(r.Destination.Path))) throw new Exception("Plan caused writes.");
                var result = DirectMigrator.Run(r); DirectMigrator.Verify(r.JournalDirectory);
                if (result.Phase != "complete" || result.ConsoleRuntimeValidated || sourceHash != RegularImage.HashFile(r.Source.Path)) throw new Exception("Completion/source boundary.");
                if (new FileInfo(Path.Combine(r.JournalDirectory, "undo.bin")).Length != plan.JournalBytes || plan.JournalBytes >= original.Length / 4)
                    throw new Exception("Journal unexpectedly large/full-image sized.");
                if (Directory.GetFiles(r.JournalDirectory, "*.img", SearchOption.AllDirectories).Length != 0) throw new Exception("Extra disk image created.");
                using var disk = RegularImage.Open(r.Destination.Image);
                Oracle.Check(f, disk.ReadBytes(0, (int)disk.TotalSize), OracleManifest(result, f));
                Expect<IOException>(() => DirectMigrator.Run(r));
                Expect<InvalidOperationException>(() => DirectMigrator.Recover(r.JournalDirectory));
                File.WriteAllBytes(Path.Combine(dir, "direct-request.json"), JsonSerializer.SerializeToUtf8Bytes(r, ImageMigrator.Json));
            });
        foreach (string stage in new[] { "Destination dirty marker durable", "Copy inode 10", "Direct data copy durable", "Verify direct destination", "Direct commit durable" })
            test("direct-rollback-" + stage.Replace(' ', '-'), dir => {
                var old = Old(); var r = Request(setup(dir, profile(), old, "plain", registry, registry));
                string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
                Expect<IOException>(() => DirectMigrator.Run(r, s => { if (s == stage) throw new IOException("Synthetic failure at " + stage); }));
                CheckRollback(old, r, before, original);
            });
        test("direct-cancellation-rollback", dir => {
            var old = Old(); var r = Request(setup(dir, profile(), old, "xts128", registry, registry));
            string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
            using var cancel = new CancellationTokenSource();
            Expect<OperationCanceledException>(() => DirectMigrator.Run(r, s => { if (s == "Copy inode 10") cancel.Cancel(); }, cancel.Token));
            CheckRollback(old, r, before, original);
        });
        foreach (bool duringFlush in new[] { false, true })
        foreach (string location in new[] { "primary", "backup" })
            test("direct-silent-clean-sector-corruption-" + location + (duringFlush ? "-flush" : ""), dir => {
                var old = Old(); var r = Request(setup(dir, profile(), old, "plain", registry, registry));
                string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
                long fault = Fixture.Prefix + (location == "primary" ? 65536 : old.Sblk * old.Frag);
                Expect<IOException>(() => DirectMigrator.RunCore(r, null, default, (e, w) => {
                    var raw = new DirectDisk(e, w); return w ? new CleanSectorFaultDisk(raw, fault, duringFlush) : raw;
                }));
                CheckRollback(old, r, before, original);
            });
        test("direct-undo-lease-blocks-writer-and-delete", dir => {
            var r = Request(setup(dir, profile(), Old(), "plain", registry, registry)); bool checkedLease = false;
            DirectMigrator.Run(r, s => {
                if (s != "Direct data copy durable") return;
                string undo = Path.Combine(r.JournalDirectory, "undo.bin");
                Expect<IOException>(() => { using var writer = File.OpenWrite(undo); });
                Expect<IOException>(() => File.Delete(undo)); checkedLease = true;
            });
            if (!checkedLease) throw new Exception("Journal lease not tested."); DirectMigrator.Verify(r.JournalDirectory);
        });
        test("direct-complete-state-save-failure-rolls-back", dir => {
            var old = Old(); var r = Request(setup(dir, profile(), old, "plain", registry, registry));
            string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
            Expect<IOException>(() => DirectMigrator.RunCore(r, null, default, (e, w) => new DirectDisk(e, w),
                (path, state) => { if (state.Phase == "complete") throw new IOException("Synthetic final state publication failure."); DirectJournal.Save(path, state); }));
            CheckRollback(old, r, before, original);
        });
        test("direct-reversed-size-refused-before-write", dir => {
            var emptyLarge = new Fixture(8); emptyLarge.Finish();
            var r = Request(setup(dir, emptyLarge, profile(), "plain", null, null));
            string before = RegularImage.HashFile(r.Destination.Path);
            Expect<IOException>(() => DirectMigrator.Run(r));
            if (Directory.Exists(r.JournalDirectory) || before != RegularImage.HashFile(r.Destination.Path)) throw new Exception("Reversed roles changed target.");
        });
        test("direct-physical-alignment-parser-no-open", _ => {
            byte[] Descriptor(int logical, int physical, int offset) {
                byte[] b = new byte[28]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 28);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), logical);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), physical);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), offset); return b;
            }
            if (DirectDisk.ParseAlignment(Descriptor(512, 4096, 0)) != 4096 || DirectDisk.ParseAlignment(Descriptor(512, 512, 0)) != 512)
                throw new Exception("Alignment parser accepted geometry incorrectly.");
            foreach (var b in new[] { Descriptor(4096,4096,0), Descriptor(512,0,0), Descriptor(512,768,0), Descriptor(512,8192,0), Descriptor(512,4096,512) })
                Expect<NotSupportedException>(() => DirectDisk.ParseAlignment(b));
            Expect<InvalidDataException>(() => DirectDisk.ParseAlignment(new byte[12]));
        });
        test("direct-write-unit-isolation-before-write", dir => {
            var source = new Fixture(4, 4096, 512, 16); source.Finish();
            var target = new Fixture(8, 4096, 512, 16); target.Finish();
            var r = Request(setup(dir, source, target, "plain", null, null));
            string before = RegularImage.HashFile(r.Destination.Path);
            Expect<NotSupportedException>(() => DirectMigrator.RunCore(r, null, default,
                (e, w) => new WriteUnitDisk(new DirectDisk(e, w), 4096)));
            if (Directory.Exists(r.JournalDirectory) || before != RegularImage.HashFile(r.Destination.Path)) throw new Exception("Unsafe fragment alignment wrote target.");
        });
        test("direct-double-triple-sparse-and-symlink-attributes", dir => {
            var f = new Fixture(6, 4096, 512, 16); long fan = 512;
            var n = f.Add("large-sparse", size: (12 + fan + fan * fan + 2) * 4096 + 13);
            n.Data[11] = [11]; n.Data[12] = [12]; n.Data[12 + fan] = [22];
            n.Data[12 + fan + fan * fan] = [33]; n.Data[12 + fan + fan * fan + 2] = [44];
            f.Add("link119", 10, payload: Fixture.Payload(119, 7), inline: true).Ea = Fixture.Payload(5000, 8);
            f.Add("link120", 10, payload: Fixture.Payload(120, 9)); f.Finish();
            var old = new Fixture(10, 4096, 512, 16); old.Add("occupied", payload: Fixture.Payload(12000, 3)); old.Finish(true);
            var r = Request(setup(dir, f, old, "cbc192", null, null));
            var result = DirectMigrator.Run(r); DirectMigrator.Verify(r.JournalDirectory);
            using var disk = RegularImage.Open(r.Destination.Image);
            Oracle.Check(f, disk.ReadBytes(0, (int)disk.TotalSize), OracleManifest(result, f));
        });
        test("direct-exclusive-leases-and-source-readonly", dir => {
            var r = Request(setup(dir, profile(), Old(), "plain", registry, registry)); bool checkedSource = false;
            DirectMigrator.RunCore(r, null, default, (e, writable) => {
                var raw = new DirectDisk(e, writable);
                try {
                    Expect<IOException>(() => { using var conflicting = new FileStream(e.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); });
                    if (!writable) { checkedSource = true; Expect<InvalidOperationException>(() => raw.WriteBytes(0, new byte[512])); }
                    return raw;
                } catch { raw.Dispose(); throw; }
            });
            if (!checkedSource) throw new Exception("Source lease was not checked.");
            DirectMigrator.Verify(r.JournalDirectory);
        });
        foreach (string crypto in new[] { "plain", "cbc192", "cbc192-bswap16", "xts128" })
            test("direct-process-loss-recovery-" + crypto, dir => {
                var old = Old(); var r = Request(setup(dir, profile(), old, crypto, registry, registry));
                string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
                Expect<SimulatedProcessLossException>(() => DirectMigrator.Run(r, s => { if (s == "Direct data copy durable") throw new SimulatedProcessLossException("Synthetic process exit."); }));
                using (var disk = RegularImage.Open(r.Destination.Image))
                    if (disk.ReadBytes(Fixture.Prefix + 65536, 512)[0xD1] != 0) throw new Exception("Interrupted target is marked clean.");
                // Recovery opens only the destination. Remove source and key/snapshot files temporarily.
                string held = r.Source.Path + ".held"; File.Move(r.Source.Path, held);
                string? heldKey = null; if (r.Source.KeyFile != null) { heldKey = r.Source.KeyFile + ".held"; File.Move(r.Source.KeyFile, heldKey); }
                try { DirectMigrator.Recover(r.JournalDirectory); DirectMigrator.Recover(r.JournalDirectory); }
                finally { File.Move(held, r.Source.Path); if (heldKey != null) File.Move(heldKey, r.Source.KeyFile!); }
                CheckRollback(old, r, before, original);
                var restart = r with { JournalDirectory = Path.Combine(dir, "restart-journal") };
                DirectMigrator.Run(restart); DirectMigrator.Verify(restart.JournalDirectory);
            });
        test("direct-ambiguous-commit-recovery", dir => {
            var old = Old(); var r = Request(setup(dir, profile(), old, "cbc192", registry, registry));
            string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
            Expect<SimulatedProcessLossException>(() => DirectMigrator.Run(r, s => { if (s == "Direct commit durable") throw new SimulatedProcessLossException("After clean write, before complete state."); }));
            DirectMigrator.Recover(r.JournalDirectory); CheckRollback(old, r, before, original);
        });
        test("direct-interrupted-rollback-retry", dir => {
            var old = Old(); var r = Request(setup(dir, profile(), old, "xts128", registry, registry));
            string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
            Expect<SimulatedProcessLossException>(() => DirectMigrator.Run(r, s => { if (s == "Direct data copy durable") throw new SimulatedProcessLossException("Synthetic exit."); }));
            using (var disk = new DirectDisk(r.Destination, true))
                Expect<IOException>(() => DirectJournal.Restore(r.JournalDirectory, DirectJournal.Load(r.JournalDirectory), disk, _ => throw new IOException("Interrupted rollback.")));
            if (DirectJournal.Load(r.JournalDirectory).Phase != "rolling-back") throw new Exception("Recovery phase missing.");
            DirectMigrator.Recover(r.JournalDirectory); CheckRollback(old, r, before, original);
        });
        test("direct-rollback-readback-keeps-primary-dirty", dir => {
            var old = Old(); var r = Request(setup(dir, profile(), old, "plain", registry, registry));
            string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
            Expect<SimulatedProcessLossException>(() => DirectMigrator.Run(r, s => { if (s == "Direct data copy durable") throw new SimulatedProcessLossException("Synthetic exit."); }));
            using (var disk = new RestoreFaultDisk(new DirectDisk(r.Destination, true), Fixture.Prefix + old.Cblk * old.Frag))
                Expect<IOException>(() => DirectJournal.Restore(r.JournalDirectory, DirectJournal.Load(r.JournalDirectory), disk));
            using (var disk = RegularImage.Open(r.Destination.Image))
                if (disk.ReadBytes(Fixture.Prefix + 65536, 512)[0xD1] != 0) throw new Exception("Failed rollback marked corrupt filesystem clean.");
            DirectMigrator.Recover(r.JournalDirectory); CheckRollback(old, r, before, original);
        });
        foreach (string stage in new[] { "Destination dirty marker durable", "Direct data copy durable", "Direct commit durable" })
            test("direct-abrupt-process-exit-" + stage.Replace(' ', '-'), dir => {
                var old = Old(); var r = Request(setup(dir, profile(), old, "xts128", registry, registry));
                string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
                string request = Path.Combine(dir, "child-request.json");
                File.WriteAllBytes(request, JsonSerializer.SerializeToUtf8Bytes(r, ImageMigrator.Json));
                var launch = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                launch.ArgumentList.Add(typeof(DirectTests).Assembly.Location);
                launch.ArgumentList.Add("--synthetic-direct-exit"); launch.ArgumentList.Add(request); launch.ArgumentList.Add(stage);
                using var child = Process.Start(launch)!;
                if (!child.WaitForExit(60000)) { child.Kill(); throw new Exception("Synthetic child timed out."); }
                if (child.ExitCode != 73) throw new Exception("Child missed abrupt exit boundary: " + child.ExitCode);
                DirectMigrator.Recover(r.JournalDirectory); CheckRollback(old, r, before, original);
            });
        foreach (string at in new[] { "primary", "cg" })
            test("direct-partial-write-" + at, dir => {
                var old = Old(); var r = Request(setup(dir, profile(), old, "xts128", registry, registry));
                string before = RegularImage.HashFile(r.Source.Path); byte[] original = File.ReadAllBytes(r.Destination.Path);
                long fault = Fixture.Prefix + (at == "primary" ? 65536 : old.Cblk * old.Frag);
                Expect<IOException>(() => DirectMigrator.RunCore(r, null, default, (e, w) => {
                    var raw = new DirectDisk(e, w); return w ? new OneShotFaultDisk(raw, fault) : raw;
                }));
                CheckRollback(old, r, before, original);
            });
        test("direct-journal-limit-before-write", dir => {
            var r = Request(setup(dir, profile(), Old(), "plain", registry, registry)) with { MaxJournalBytes = 512 };
            byte[] original = File.ReadAllBytes(r.Destination.Path);
            Expect<IOException>(() => DirectMigrator.Run(r));
            Oracle.Equal(original, File.ReadAllBytes(r.Destination.Path), "preflight journal limit no write");
            if (Directory.Exists(r.JournalDirectory)) throw new Exception("Journal limit created session.");
        });
        test("direct-originally-free-space-required", dir => {
            var f = new Fixture(4); f.Add("large", payload: Fixture.Payload(900000, 7)); f.Finish();
            var small = new Fixture(1); small.Add("occupied", payload: Fixture.Payload(500000, 8)); small.Finish();
            var r = Request(setup(dir, f, small, "plain", null, null)); byte[] original = File.ReadAllBytes(r.Destination.Path);
            Expect<IOException>(() => DirectMigrator.Run(r)); Oracle.Equal(original, File.ReadAllBytes(r.Destination.Path), "occupied target preserved");
        });
        foreach (string corruption in new[] { "undo", "state", "identity", "protected" })
            test("direct-corrupt-" + corruption + "-recovery-refused", dir => {
                var old = Old(); var r = Request(setup(dir, profile(), old, "plain", registry, registry));
                Expect<SimulatedProcessLossException>(() => DirectMigrator.Run(r, s => { if (s == "Direct data copy durable") throw new SimulatedProcessLossException("Synthetic process loss."); }));
                if (corruption == "undo" || corruption == "state")
                {
                    string path = Path.Combine(r.JournalDirectory, corruption == "undo" ? "undo.bin" : "state.json");
                    byte[] bytes = File.ReadAllBytes(path); bytes[20] ^= 1; File.WriteAllBytes(path, bytes);
                }
                else if (corruption == "identity")
                {
                    File.Move(r.Destination.Path, r.Destination.Path + ".original");
                    File.Copy(r.Destination.Path + ".original", r.Destination.Path);
                }
                else
                {
                    var state = DirectJournal.Load(r.JournalDirectory); using var file = File.OpenWrite(r.Destination.Path);
                    file.Position = state.ProtectedData[0].Offset; file.WriteByte(0xDE);
                }
                string before = RegularImage.HashFile(r.Destination.Path);
                try { DirectMigrator.Recover(r.JournalDirectory); throw new Exception("Corruption recovery unexpectedly accepted."); }
                catch (Exception e) when (e is InvalidDataException or JsonException) { }
                if (before != RegularImage.HashFile(r.Destination.Path)) throw new Exception("Refused recovery wrote to target.");
            });
        foreach (string corruption in new[] { "missing-primary", "payload-range", "budget", "outside" })
            test("direct-invalid-recovery-plan-" + corruption, dir => {
                var r = Request(setup(dir, profile(), Old(), "plain", registry, registry));
                Expect<SimulatedProcessLossException>(() => DirectMigrator.Run(r, s => { if (s == "Direct data copy durable") throw new SimulatedProcessLossException("Synthetic exit."); }));
                var state = DirectJournal.Load(r.JournalDirectory);
                state = corruption switch {
                    "missing-primary" => state with { Metadata = state.Metadata.Where(a => !(a.Offset <= Fixture.Prefix + 65536 && a.Offset + a.Length > Fixture.Prefix + 65536)).ToList() },
                    "payload-range" => state with { Metadata = DirectLayout.Merge(state.Metadata.Concat(state.ProtectedData.Take(1))) },
                    "budget" => state with { Request = state.Request with { MaxJournalBytes = 512 } },
                    _ => state with { Outside = [] }
                };
                DirectJournal.Save(r.JournalDirectory, state); // Recompute outer checksum; validate structure independently.
                string before = RegularImage.HashFile(r.Destination.Path);
                Expect<InvalidDataException>(() => DirectMigrator.Recover(r.JournalDirectory));
                if (before != RegularImage.HashFile(r.Destination.Path)) throw new Exception("Malformed recovery plan caused writes.");
            });
        test("direct-missing-registry-no-write", dir => {
            var r = Request(setup(dir, profile(), Old(), "plain", null, null)); byte[] original = File.ReadAllBytes(r.Destination.Path);
            Expect<InvalidDataException>(() => DirectMigrator.Run(r)); Oracle.Equal(original, File.ReadAllBytes(r.Destination.Path), "missing account snapshot");
        });
        test("direct-device-policy-no-open", _ => {
            foreach (var e in new[] { new DirectEndpoint(@"\\.\PhysicalDrive0", 0),
                new DirectEndpoint(@"\\?\GLOBALROOT\Device\Harddisk0\DR0", 0, Kind: "windows-device", ExpectedSerial: "mock"),
                new DirectEndpoint(@"\\.\PhysicalDrive0", 0, Kind: "windows-device") })
                Expect<ArgumentException>(() => e.Normalize());
            var valid = new DirectEndpoint(@"\\.\PhysicalDrive99", 0, Kind: "windows-device", ExpectedSerial: "synthetic-not-opened").Normalize();
            if (valid.Kind != "windows-device") throw new Exception("Explicit device policy rejected.");
            // Only lexical validation. No physical handle, IOCTL, size probe or enumeration.
        });
    }
}

sealed class CleanSectorFaultDisk(IIdentifiedDisk raw, long faultOffset, bool duringFlush = false) : IIdentifiedDisk
{
    private bool _fired, _armed;
    public long TotalSize => raw.TotalSize; public int SectorSize => 512; public long SectorCount => raw.SectorCount;
    public string Identity => raw.Identity; public string Description => raw.Description; public bool CanWrite => raw.CanWrite;
    public byte[] ReadBytes(long offset, int count) => raw.ReadBytes(offset, count);
    public byte[] ReadSectors(long sector, int count) => raw.ReadSectors(sector, count);
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        if (!_fired && offset == faultOffset && data.Length >= 512 && data[0xD1] == 1)
        {
            if (duringFlush) _armed = true;
            else { _fired = true; byte[] corrupt = data.ToArray(); corrupt[0x30] ^= 1; raw.WriteBytes(offset, corrupt); return; }
        }
        raw.WriteBytes(offset, data);
    }
    public void WriteSectors(long sector, byte[] data) => WriteBytes(checked(sector * 512), data);
    public void Flush()
    {
        raw.Flush();
        if (_armed && !_fired) { _fired = true; byte[] corrupt = raw.ReadBytes(faultOffset, 512); corrupt[0x30] ^= 1; raw.WriteBytes(faultOffset, corrupt); raw.Flush(); }
    }
    public void Dispose() => raw.Dispose();
}

sealed class RestoreFaultDisk(IIdentifiedDisk raw, long faultOffset) : IIdentifiedDisk
{
    private bool _fired;
    public long TotalSize => raw.TotalSize; public int SectorSize => 512; public long SectorCount => raw.SectorCount;
    public string Identity => raw.Identity; public string Description => raw.Description; public bool CanWrite => raw.CanWrite;
    public byte[] ReadBytes(long offset, int count) => raw.ReadBytes(offset, count);
    public byte[] ReadSectors(long sector, int count) => raw.ReadSectors(sector, count);
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        if (!_fired && offset == faultOffset && data.Length > 512)
        { _fired = true; byte[] corrupt = data.ToArray(); corrupt[0x1C] ^= 1; raw.WriteBytes(offset, corrupt); return; }
        raw.WriteBytes(offset, data);
    }
    public void WriteSectors(long sector, byte[] data) => WriteBytes(checked(sector * 512), data);
    public void Flush() => raw.Flush(); public void Dispose() => raw.Dispose();
}

sealed class WriteUnitDisk(IIdentifiedDisk raw, int unit) : IIdentifiedDisk
{
    public int PhysicalWriteUnit => unit;
    public long TotalSize => raw.TotalSize; public int SectorSize => 512; public long SectorCount => raw.SectorCount;
    public string Identity => raw.Identity; public string Description => raw.Description; public bool CanWrite => raw.CanWrite;
    public byte[] ReadBytes(long offset, int count) => raw.ReadBytes(offset, count);
    public byte[] ReadSectors(long sector, int count) => raw.ReadSectors(sector, count);
    public void WriteBytes(long offset, byte[] data) => raw.WriteBytes(offset, data);
    public void WriteBytes(long offset, ReadOnlySpan<byte> data) => raw.WriteBytes(offset, data);
    public void WriteSectors(long sector, byte[] data) => raw.WriteSectors(sector, data);
    public void Flush() => raw.Flush(); public void Dispose() => raw.Dispose();
}

sealed class OneShotFaultDisk(IIdentifiedDisk raw, long faultOffset) : IIdentifiedDisk
{
    private bool _fired;
    public long TotalSize => raw.TotalSize; public int SectorSize => 512; public long SectorCount => raw.SectorCount;
    public string Identity => raw.Identity; public string Description => raw.Description; public bool CanWrite => raw.CanWrite;
    public byte[] ReadBytes(long offset, int count) => raw.ReadBytes(offset, count);
    public byte[] ReadSectors(long sector, int count) => raw.ReadSectors(sector, count);
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        if (!_fired && offset == faultOffset && data.Length >= 1024)
        { _fired = true; raw.WriteBytes(offset, data[..512]); raw.Flush(); throw new IOException("Synthetic partial multi-sector write."); }
        raw.WriteBytes(offset, data);
    }
    public void WriteSectors(long sector, byte[] data) => WriteBytes(checked(sector * 512), data);
    public void Flush() => raw.Flush(); public void Dispose() => raw.Dispose();
}
