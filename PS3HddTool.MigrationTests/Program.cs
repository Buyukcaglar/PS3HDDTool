using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.FileSystem;
using PS3HddTool.Core.Migration;

// Only used by synthetic crash tests. Abrupt exit skips catch/finally and handle disposal.
if(args.Length==2 && args[0]=="--validate-source-cache")
{
    var r=JsonSerializer.Deserialize<DirectRequest>(File.ReadAllBytes(args[1]),ImageMigrator.Json)!;
    if(r.Source.Kind!="image"||r.SourceHashCache==null||r.SourceHashCacheSha256==null)throw new ArgumentException("Host image plus pinned source hash cache required");
    using var raw=new DirectDisk(r.Source,false);
    using var disk=RegularImage.WrapCrypto(new BorrowedDisk(raw),r.Source.Image);
    using var view=MigrationSource.Open(disk,r.Source.PartitionSector,r.SourceRecovery,default,null);
    view.Volume.Inventory(false);
    SourceHashSnapshot.Apply(view.Volume,r.SourceHashCache,r.SourceHashCacheSha256,default);
    Console.WriteLine(JsonSerializer.Serialize(new{validated=true,sourceIdentity=raw.Identity,nodes=view.Volume.Nodes.Count,hashedFiles=view.Volume.Nodes.Values.Count(n=>n.Hash.Length==64),sourceDigest=DirectLayout.TreeDigest(view.Volume),sourcePayloadRehashed=false,destinationOpened=false}));
    return 0;
}
if (args.Length is 3 or 4 && args[0] == "--synthetic-direct-exit")
{
    var request = JsonSerializer.Deserialize<DirectRequest>(File.ReadAllBytes(args[1]), ImageMigrator.Json)!;
    if (request.Source.Kind != "image" || request.Destination.Kind != "image")
        throw new InvalidOperationException("Synthetic child accepts regular images only.");
    void ExitAtStage(string stage) { if (stage == args[2]) Environment.Exit(73); }
    if (args.Length == 4)
    {
        if (args[3] != "--compact-generated-zero-blocks") throw new ArgumentException("Unknown synthetic child option.");
        DirectMigrator.RunCore(request, ExitAtStage, default, (endpoint, writable) => new SparseZeroDisk(endpoint, writable));
    }
    else DirectMigrator.Run(request, ExitAtStage);
    return 74;
}

string run = Path.GetFullPath(Path.Combine("artifacts", "migration-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]));
Directory.CreateDirectory(run); var results = new List<object>(); int failures = 0;
void Test(string name, Action<string> body)
{
    if (args.Length == 2 && args[0] == "--filter" && !name.Contains(args[1], StringComparison.Ordinal)) return;
    string dir = Path.Combine(run, name); Directory.CreateDirectory(dir);
    try { body(dir); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Expect<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
MigrationRequest Setup(string dir, Fixture source, Fixture template, string crypto = "plain", byte[]? registry = null, byte[]? target = null)
{
    string src = Path.Combine(dir, "source.img"), dst = Path.Combine(dir, "template.img"), keyPath = Path.Combine(dir, "generated.key");
    File.WriteAllBytes(src, source.Image); File.WriteAllBytes(dst, template.Image);
    if (crypto != "plain")
    {
        byte[] key = Fixture.Payload(crypto is "cbc192" or "cbc192-bswap16" ? 24 : 32, 99); File.WriteAllBytes(keyPath, key);
        foreach (string path in new[] { src, dst })
        {
            byte[] plaintext = File.ReadAllBytes(path);
            using var disk = RegularImage.Open(new(path, 32, crypto, keyPath), true);
            for (int p = 0; p < plaintext.Length; p += 65536) disk.WriteBytes(p, plaintext.AsSpan(p, Math.Min(65536, plaintext.Length - p)));
            RegularImage.Flush(disk);
        }
    }
    string? sourceRegistry = null, targetRegistry = null;
    if (registry != null) { sourceRegistry = Path.Combine(dir, "source-registry.bin"); File.WriteAllBytes(sourceRegistry, registry); }
    if (target != null) { targetRegistry = Path.Combine(dir, "target-registry.bin"); File.WriteAllBytes(targetRegistry, target); }
    return new(new(src, 32, crypto, crypto == "plain" ? null : keyPath),
        new(dst, 32, crypto, crypto == "plain" ? null : keyPath), Path.Combine(dir, "package"), sourceRegistry, targetRegistry);
}
MigrationManifest Success(Fixture source, MigrationRequest request)
{
    string beforeSource = RegularImage.HashFile(request.Source.Path), beforeTarget = RegularImage.HashFile(request.DestinationTemplate.Path);
    var manifest = ImageMigrator.Prepare(request);
    if (RegularImage.HashFile(request.Source.Path) != beforeSource || RegularImage.HashFile(request.DestinationTemplate.Path) != beforeTarget)
        throw new Exception("Input changed.");
    ImageMigrator.Verify(request.OutputDirectory);
    using var result = RegularImage.Open(request.DestinationTemplate with { Path = Path.Combine(request.OutputDirectory, "disk.img") });
    Oracle.Check(source, result.ReadBytes(0, (int)result.TotalSize), manifest);
    if (manifest.ConsoleRuntimeValidated) throw new Exception("Runtime acceptance falsely claimed.");
    if (Directory.GetDirectories(Path.GetDirectoryName(request.OutputDirectory)!, ".ps3-migration-*").Length != 0) throw new Exception("Staging leak.");
    return manifest;
}
void FailedUnchanged<T>(MigrationRequest request, Action<string>? fault = null, CancellationToken token = default) where T : Exception
{
    string a = RegularImage.HashFile(request.Source.Path), b = RegularImage.HashFile(request.DestinationTemplate.Path);
    Expect<T>(() => ImageMigrator.Prepare(request, fault, token));
    if (a != RegularImage.HashFile(request.Source.Path) || b != RegularImage.HashFile(request.DestinationTemplate.Path) || Directory.Exists(request.OutputDirectory))
        throw new Exception("Failure changed input or published output.");
    if (Directory.GetDirectories(Path.GetDirectoryName(request.OutputDirectory)!, ".ps3-migration-*").Length != 0) throw new Exception("Failure staging leak.");
}
Fixture Empty(int groups = 8, int block = 16384, int frag = 4096, int ipg = 64)
{ var f = new Fixture(groups, block, frag, ipg); f.Finish(true); return f; }
Fixture Profile()
{
    var f = new Fixture();
    f.Add("home", 4); f.Add("home/00000001", 4); f.Add("home/00000001/savedata", 4);
    f.Add("home/00000001/savedata/TEST00001", 4);
    f.Add("home/00000001/savedata/TEST00001/PARAM.SFO", payload: Fixture.Payload(1024, 1));
    f.Add("home/00000001/savedata/TEST00001/PARAM.PFD", payload: Fixture.Payload(4096, 2));
    f.Add("home/00000001/trophy", 4); f.Add("home/00000001/trophy/NPWR00001_00", 4);
    f.Add("home/00000001/trophy/NPWR00001_00/TROPUSR.DAT", payload: Fixture.Payload(8192, 3));
    f.Add("home/00000001/trophy/NPWR00001_00/TROPTRNS.DAT", payload: Fixture.Payload(900, 4));
    f.Add("home/00000001/exdata", 4); f.Add("home/00000001/exdata/act.dat", payload: Fixture.Payload(4152, 5));
    f.Add("home/00000001/exdata/TEST-LICENSE.rif", payload: Fixture.Payload(152, 6));
    f.Add("game", 4); f.Add("game/TEST00001", 4); f.Add("game/TEST00001/USRDIR", 4);
    var game = f.Add("game/TEST00001/USRDIR/EBOOT.BIN", payload: Fixture.Payload(16 * 16384 + 123, 7));
    game.Ea = Fixture.Payload(17000, 100); f.Link("", "hardlink", game);
    f.Add("vsh", 4); f.Add("vsh/task", 4); f.Add("vsh/task/database.bin", payload: Fixture.Payload(12000, 8));
    f.Add("Case", payload: new byte[] { 1 }); f.Add("case", payload: new byte[] { 2 });
    f.Add("CON", payload: new byte[] { 3 }); f.Add("a\\b", payload: new byte[] { 4 }); f.Add("日本語", payload: new byte[] { 5 });
    f.Add("short-link", 10, payload: Encoding.UTF8.GetBytes("home/00000001/savedata"), inline: true);
    f.Add("long-link", 10, payload: Encoding.UTF8.GetBytes(new string('x', 200)));
    f.Add("empty", payload: []);
    var sparse = f.Add("sparse", size: 14L * 16384 + 13); sparse.Data[0] = new byte[] { 7 }; sparse.Data[14] = new byte[] { 8 };
    for (int i = 0; i < 85; i++) f.Add("extra-" + i, payload: Fixture.Payload(i % 100 + 1, i));
    f.Finish(); return f;
}
byte[] reg = Fixture.Registry(("00000001", "0123456789ABCDEF"));
if(args.Length==2&&args[0]=="--legacy-verification-handoff")
{
    Test("legacy-verification-handoff",dir=>LegacyVerificationHandoffTests.Run(dir,args[1],Setup,reg));
    File.WriteAllBytes(Path.Combine(run,"results.json"),JsonSerializer.SerializeToUtf8Bytes(new{total=results.Count,failures,results},ImageMigrator.Json));
    Console.WriteLine($"{results.Count-failures}/{results.Count} passed. Results: {run}");return failures==0?0:1;
}
if (args.Length == 1 && args[0] == "--source-copy-performance")
{
    Test("source-copy-performance", SourceCopyPerformanceTests.Run);
    File.WriteAllBytes(Path.Combine(run,"results.json"),JsonSerializer.SerializeToUtf8Bytes(new{total=results.Count,failures,results},ImageMigrator.Json));
    Console.WriteLine($"{results.Count-failures}/{results.Count} passed. Results: {run}");return failures==0?0:1;
}
if (args.Length == 1 && args[0] == "--transfer-performance")
{
    Test("transfer-performance", dir => TransferPerformanceTests.Run(dir));
    File.WriteAllBytes(Path.Combine(run,"results.json"),JsonSerializer.SerializeToUtf8Bytes(new{total=results.Count,failures,results},ImageMigrator.Json));
    Console.WriteLine($"{results.Count-failures}/{results.Count} passed. Results: {run}");return failures==0?0:1;
}
if (args.Length == 1 && args[0] is "--recovery-large" or "--target-preservation-large")
{
    if(args[0]=="--recovery-large")Test("recovery-1TB-to-2TB-sparse", RecoveryTests.RunLarge);
    else Test("target-preserve-1TB-to-2TB-sparse",TargetPreservationTests.RunLarge);
    File.WriteAllBytes(Path.Combine(run, "results.json"), JsonSerializer.SerializeToUtf8Bytes(new { total=results.Count, failures, results }, ImageMigrator.Json));
    Console.WriteLine($"{results.Count-failures}/{results.Count} passed. Results: {run}");
    return failures == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--cli-populated-stress")
{
    foreach (string crypto in args[1] == "all" ? new[] { "plain", "cbc192", "cbc192-bswap16", "xts128" } : args[1] == "encrypted" ? new[] { "cbc192", "cbc192-bswap16", "xts128" } : new[] { args[1] })
    {
        Test("cli-populated-1TB-to-2TB-" + crypto, dir => CliPopulatedStressTests.Run(dir, crypto));
        if (failures != 0) break; // Retain a failed case without multiplying sparse fixtures.
    }
    File.WriteAllBytes(Path.Combine(run, "results.json"), JsonSerializer.SerializeToUtf8Bytes(new { total = results.Count, failures, results }, ImageMigrator.Json));
    Console.WriteLine($"{results.Count - failures}/{results.Count} passed. Results: {run}");
    return failures == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--populated-stress")
{
    foreach (string crypto in args[1] == "all" ? new[] { "plain", "cbc192", "cbc192-bswap16", "xts128" } : new[] { args[1] })
        Test("populated-stress-1TB-to-2TB-" + crypto, dir => PopulatedStressTests.Run(dir, crypto));
    File.WriteAllBytes(Path.Combine(run, "results.json"), JsonSerializer.SerializeToUtf8Bytes(new { total = results.Count, failures, results }, ImageMigrator.Json));
    Console.WriteLine($"{results.Count - failures}/{results.Count} passed. Results: {run}");
    return failures == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--large-sparse")
{
    Test("large-sparse-1TB-to-2TB-" + args[1], dir => LargeSparseTests.Run(dir, Profile(), reg, args[1]));
    File.WriteAllBytes(Path.Combine(run, "results.json"), JsonSerializer.SerializeToUtf8Bytes(new { total = results.Count, failures, results }, ImageMigrator.Json));
    Console.WriteLine($"{results.Count - failures}/{results.Count} passed. Results: {run}");
    return failures == 0 ? 0 : 1;
}
SourceHashCacheTests.Run(Test, Profile, Setup, reg);
VerificationResumeTests.Run(Test, Profile, Setup, reg);
Test("sparse-zero-backend-overlap-readback", dir => {
    string path = Path.Combine(dir, "generated.img"); SparseFile.Create(path, 16 * 1024 * 1024);
    var endpoint = new DirectEndpoint(path, 0); byte[] tag = Fixture.Payload(512, 912);
    using (var disk = new SparseZeroDisk(endpoint, true))
    {
        disk.WriteBytes(0, Fixture.Payload(131072, 913));
        disk.WriteBytes(0, new byte[65536]); disk.WriteBytes(65536, new byte[65536]);
        // A nonzero overwrite inside deferred zeros must survive drain/flush.
        disk.WriteBytes(512, tag); Oracle.Equal(tag, disk.ReadBytes(512, 512), "zero compactor overlapping nonzero write");
        if (disk.ReadBytes(1024, 130048).Any(b => b != 0)) throw new Exception("Deferred zeros were not visible on read.");
        disk.WriteBytes(0, new byte[131072]); disk.Flush();
        if (disk.ReadBytes(0, 131072).Any(b => b != 0)) throw new Exception("Compactor failed to clear preexisting bytes.");
        Expect<IOException>(() => disk.WriteBytes(1, tag));
        Expect<IOException>(() => disk.ReadBytes(16 * 1024 * 1024 - 1, 512));
    }
    if (SparseFile.Allocated(path) >= 131072) throw new Exception("Zero compactor did not release host allocation.");
    using var source = new SparseZeroDisk(endpoint, false); Expect<IOException>(() => source.WriteBytes(0, tag));
    if (source.ReadBytes(0, 131072).Any(b => b != 0)) throw new Exception("Zero bytes did not survive reopen.");
});
foreach (string crypto in new[] { "plain", "cbc192", "cbc192-bswap16", "xts128" })
    Test("profile-" + crypto, dir => {
        var f = Profile(); var template = new Fixture(8); template.Add("home", 4); template.Add("home/00000002", 4); template.Add("unrelated-old-file", payload: new byte[] { 99 }); template.Finish(true);
        var request = Setup(dir, f, template, crypto, reg, reg); var manifest = Success(f, request);
        if (manifest.ConsoleStateRestoreRequired) throw new Exception("Identical registry marked mismatched.");
        if (manifest.Mapping.All(p => p.Destination < 64)) throw new Exception("CG inode fallback not exercised.");
        Oracle.Equal(reg, File.ReadAllBytes(Path.Combine(request.OutputDirectory, "xRegistry.sys")), "registry byte preservation");
        Expect<IOException>(() => ImageMigrator.Prepare(request));
        File.WriteAllBytes(Path.Combine(dir, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request, ImageMigrator.Json));
    });
Test("registry-required", dir => { var request = Setup(dir, Profile(), Empty()); FailedUnchanged<InvalidDataException>(request); });
Test("registry-mismatch", dir => {
    var f = Profile(); var manifest = Success(f, Setup(dir, f, Empty(), registry: reg,
        target: Fixture.Registry(("00000001", "1111111111111111"))));
    if (!manifest.ConsoleStateRestoreRequired) throw new Exception("Mismatched console identities accepted as ready.");
});
Test("registry-missing-user", dir => { var request = Setup(dir, Profile(), Empty(), registry: Fixture.Registry(("00000002", "1111111111111111"))); FailedUnchanged<InvalidDataException>(request); });
Test("registry-malformed", dir => {
    byte[] malformed = (byte[])reg.Clone(); malformed[0x10009] = (byte)'Z';
    var request = Setup(dir, Profile(), Empty(), registry: malformed); FailedUnchanged<InvalidDataException>(request);
});
Test("directory-indirect", dir => {
    var f = new Fixture(16, 4096, 512, 1024); f.Add("large", 4);
    var target = f.Add("payload", payload: new byte[] { 4, 3, 2, 1 });
    for (int i = 0; i < 500; i++) f.Link("large", i.ToString("D5") + new string('a', 170), target);
    f.Finish(); Success(f, Setup(dir, f, Empty(20, 4096, 512, 1024)));
});
Test("double-triple-indirect-sparse", dir => {
    var f = new Fixture(6, 4096, 512, 16); long fan = 512;
    var n = f.Add("large-sparse", size: (12 + fan + fan * fan + 2) * 4096 + 13);
    n.Data[11] = new byte[] { 11 }; n.Data[12] = new byte[] { 12 }; n.Data[12 + fan] = new byte[] { 22 };
    n.Data[12 + fan + fan * fan] = new byte[] { 33 }; n.Data[12 + fan + fan * fan + 2] = new byte[] { 44 };
    f.Finish(); Success(f, Setup(dir, f, Empty(10, 4096, 512, 16)));
});
Test("injected-copy-failure", dir => {
    var request = Setup(dir, Profile(), Empty(), registry: reg); int count = 0;
    FailedUnchanged<IOException>(request, stage => { if (stage.StartsWith("Copy inode") && ++count == 10) throw new IOException("Synthetic write failure."); });
});
Test("failure-before-publication", dir => {
    var request = Setup(dir, Profile(), Empty(), registry: reg);
    FailedUnchanged<IOException>(request, stage => { if (stage == "Publish verified package") throw new IOException("Synthetic publication failure."); });
});
Test("cancellation-mid-copy", dir => {
    var request = Setup(dir, Profile(), Empty(), registry: reg); using var cancel = new CancellationTokenSource();
    FailedUnchanged<OperationCanceledException>(request, stage => { if (stage.StartsWith("Copy inode")) cancel.Cancel(); }, cancel.Token);
});
Test("out-of-inodes", dir => { var request = Setup(dir, Profile(), Empty(1), registry: reg); FailedUnchanged<IOException>(request); });
Test("out-of-data-space", dir => {
    var f = new Fixture(4); f.Add("big", payload: Fixture.Payload(2 * 1024 * 1024, 24)); f.Finish();
    FailedUnchanged<IOException>(Setup(dir, f, Empty(1)));
});
Test("dirty-filesystem", dir => {
    var f = Empty(4); f.Image[Fixture.Prefix + 65536 + 0xD1] = 0; FailedUnchanged<InvalidDataException>(Setup(dir, f, Empty()));
});
Test("inode-marked-free", dir => {
    var f = Profile(); int cg = Fixture.Prefix + f.Cblk * f.Frag, imap = Fixture.R32(f.Image, cg + 0x5C);
    Fixture.Set(f.Image, cg + imap, 2, false); FailedUnchanged<InvalidDataException>(Setup(dir, f, Empty(), registry: reg));
});
Test("summary-corruption", dir => {
    var f = Empty(4); Fixture.W64(f.Image, Fixture.Prefix + 65536 + 0x3F8, 999999);
    FailedUnchanged<InvalidDataException>(Setup(dir, f, Empty()));
});
Test("truncated-image", dir => {
    var request = Setup(dir, Empty(4), Empty()); using (var file = File.OpenWrite(request.Source.Path)) file.SetLength(Fixture.Prefix + 65536 + 8192 - 1);
    FailedUnchanged<ArgumentOutOfRangeException>(request);
});
Test("bad-directory-length", dir => {
    var f = Empty(4); int inode = Fixture.Prefix + f.Iblk * f.Frag + 2 * 256; long data = Fixture.R64(f.Image, inode + 0x70);
    f.Image[Fixture.Prefix + (int)data * f.Frag + 4] = 0; f.Image[Fixture.Prefix + (int)data * f.Frag + 5] = 0;
    FailedUnchanged<InvalidDataException>(Setup(dir, f, Empty()));
});
Test("overlapping-allocation", dir => {
    var f = new Fixture(); var a = f.Add("a", payload: new byte[] { 1 }); var b = f.Add("b", payload: new byte[] { 2 }); f.Finish();
    int at = Fixture.Prefix + f.Iblk * f.Frag + (int)b.Ino * 256; Fixture.W64(f.Image, at + 0x70, Fixture.R64(a.Raw, 0x70));
    FailedUnchanged<InvalidDataException>(Setup(dir, f, Empty()));
});
Test("output-corruption-detected", dir => {
    var f = Empty(4); var request = Setup(dir, f, Empty()); Success(f, request);
    string image = Path.Combine(request.OutputDirectory, "disk.img"); using (var stream = File.OpenWrite(image)) { stream.Position = 0; stream.WriteByte(0); }
    Expect<InvalidDataException>(() => ImageMigrator.Verify(request.OutputDirectory));
});
Test("corruption-with-updated-digest", dir => {
    var f = Profile(); var request = Setup(dir, f, Empty(), registry: reg); var manifest = Success(f, request);
    string image = Path.Combine(request.OutputDirectory, "disk.img"); byte[] bytes = File.ReadAllBytes(image);
    var entry = f.Items.Single(n => n.Path.EndsWith("PARAM.PFD")); long ino = manifest.Mapping.Single(p => p.Source == entry.Ino).Destination;
    int inode = Fixture.Prefix + (int)(ino / 64) * 256 * 4096 + 28 * 4096 + (int)(ino % 64) * 256;
    long address = Fixture.R64(bytes, inode + 0x70); bytes[Fixture.Prefix + (int)address * 4096] ^= 1; File.WriteAllBytes(image, bytes);
    manifest = manifest with { ImageSha256 = RegularImage.HashFile(image) };
    File.WriteAllBytes(Path.Combine(request.OutputDirectory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, ImageMigrator.Json));
    Expect<InvalidDataException>(() => ImageMigrator.Verify(request.OutputDirectory));
});
Test("orphan-inode", dir => {
    var f = Empty(4); int cg = Fixture.Prefix + f.Cblk * f.Frag, imap = Fixture.R32(f.Image, cg + 0x5C);
    Fixture.Set(f.Image, cg + imap, 3, true); FailedUnchanged<InvalidDataException>(Setup(dir, f, Empty()));
});
Test("free-fragment-reference", dir => {
    var f = Profile(); long address = Fixture.R64(f.Root.Raw, 0x70); int cg = Fixture.Prefix + (int)(address / f.Fpg * f.Fpg + f.Cblk) * f.Frag;
    int fmap = Fixture.R32(f.Image, cg + 0x60); Fixture.Set(f.Image, cg + fmap, (int)(address % f.Fpg), true);
    FailedUnchanged<InvalidDataException>(Setup(dir, f, Empty(), registry: reg));
});
Test("64bit-partition-offset", _ => {
    var f = new Fixture(); f.Add("payload", payload: Fixture.Payload(17001, 80)); f.Finish();
    long shift = (1L << 32) * 512;
    using var disk = new OffsetDisk(f.Image, shift);
    var volume = new UfsVolume(disk, (shift + Fixture.Prefix) / 512); volume.Inventory();
    if (volume.Offset != shift + Fixture.Prefix || volume.Nodes.Count != 2 || disk.Writes != 0) throw new Exception("64-bit offset/inventory mismatch.");
    byte[] inode = disk.ReadBytes(volume.InodeOffset(3), 256); inode[0x40] ^= 1;
    RegularImage.Write(disk, volume.InodeOffset(3), inode);
    Oracle.Equal(inode, disk.ReadBytes(volume.InodeOffset(3), 256), "write beyond 32-bit LBA");
});
Test("image-bounds-and-read-only", dir => {
    string image = Path.Combine(dir, "tiny.img"); File.WriteAllBytes(image, new byte[512]);
    using (var read = new ImageDiskSource(image)) { Expect<InvalidOperationException>(() => read.WriteBytes(0, new byte[1])); Expect<ArgumentOutOfRangeException>(() => read.ReadBytes(511, 2)); }
    using (var write = new ImageDiskSource(image, true)) Expect<ArgumentOutOfRangeException>(() => write.WriteBytes(511, new byte[2]));
    if (new FileInfo(image).Length != 512) throw new Exception("Image expanded.");
});
Test("device-path-policy", _ => {
    foreach (string path in new[] { @"\\.\PhysicalDrive0", @"\\?\GLOBALROOT\Device\Harddisk0\DR0", "/dev/sda", @"\\server\share\disk.img", @"C:\disk.img:secret" })
        Expect<ArgumentException>(() => RegularImage.CheckedPath(path));
    Expect<NotSupportedException>(() => MigrationPolicy.RejectPhysicalAccess());
    // No PhysicalDiskSource or DriveEnumerator invocation: no device gets probed, even for tests.
});
Test("legacy-writer-disabled", dir => {
    var f = Empty(4); string image = Path.Combine(dir, "test.img"); File.WriteAllBytes(image, f.Image);
    using var disk = new ImageDiskSource(image, true);
    Expect<NotSupportedException>(() => new Ufs2Writer(null!, disk, 32 * 512, false, _ => { }));
});
Test("different-console-key-rejected", dir => {
    var request = Setup(dir, Empty(4), Empty(), "cbc192"); string key = Path.Combine(dir, "different-generated.key");
    File.WriteAllBytes(key, Fixture.Payload(24, 199)); request = request with { DestinationTemplate = request.DestinationTemplate with { KeyFile = key } };
    FailedUnchanged<NotSupportedException>(request);
});
Test("different-geometry-rejected", dir => {
    FailedUnchanged<NotSupportedException>(Setup(dir, Empty(4), Empty(8, 4096, 512, 16)));
});
Test("registry-bundle-corruption", dir => {
    var f = Profile(); var request = Setup(dir, f, Empty(), registry: reg); Success(f, request);
    string registry = Path.Combine(request.OutputDirectory, "xRegistry.sys"); byte[] bytes = File.ReadAllBytes(registry); bytes[0x10009] ^= 1; File.WriteAllBytes(registry, bytes);
    Expect<InvalidDataException>(() => ImageMigrator.Verify(request.OutputDirectory));
});
Test("other-partition-corruption-with-updated-digest", dir => {
    var f = Empty(4); var request = Setup(dir, f, Empty()); var manifest = Success(f, request);
    string image = Path.Combine(request.OutputDirectory, "disk.img"); using (var stream = File.OpenWrite(image)) { stream.Position = 0; stream.WriteByte(0); }
    manifest = manifest with { ImageSha256 = RegularImage.HashFile(image) };
    File.WriteAllBytes(Path.Combine(request.OutputDirectory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, ImageMigrator.Json));
    Expect<InvalidDataException>(() => ImageMigrator.Verify(request.OutputDirectory));
});
Test("registry-identity-corruption-with-updated-digest", dir => {
    var f = Profile(); var request = Setup(dir, f, Empty(), registry: reg, target: reg); var manifest = Success(f, request);
    string registry = Path.Combine(request.OutputDirectory, "xRegistry.sys"); byte[] bytes = File.ReadAllBytes(registry); bytes[0x10009] = (byte)'A'; File.WriteAllBytes(registry, bytes);
    manifest = manifest with { RegistrySha256 = RegularImage.HashFile(registry) };
    File.WriteAllBytes(Path.Combine(request.OutputDirectory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, ImageMigrator.Json));
    Expect<InvalidDataException>(() => ImageMigrator.Verify(request.OutputDirectory));
});
Test("symlink-inline-boundary-and-attributes", dir => {
    var f = new Fixture(); var fast = f.Add("fast", 10, payload: Encoding.ASCII.GetBytes(new string('x', 119)), inline: true);
    fast.Ea = Fixture.Payload(900, 77);
    var slow = f.Add("slow", 10, payload: Encoding.ASCII.GetBytes(new string('y', 120))); slow.Ea = Fixture.Payload(600, 78);
    f.Finish(); Success(f, Setup(dir, f, Empty()));
});
NativeCbcTests.Run(Test);
foreach (string problem in new[] { "dirty", "unclean-flag", "wrong-mode", "mixed-modes" })
    Test("native-cbc-refuses-" + problem, dir => {
        var f = Profile();
        if (problem == "dirty") f.Image[Fixture.Prefix + 65536 + 0xD1] = 0;
        if (problem == "unclean-flag") Fixture.W32(f.Image, Fixture.Prefix + 65536 + 0x520, 3);
        var r = Setup(dir, f, Empty(), "cbc192-bswap16", reg, reg);
        if (problem == "wrong-mode") r = r with { Source = r.Source with { Crypto = "cbc192" }, DestinationTemplate = r.DestinationTemplate with { Crypto = "cbc192" } };
        if (problem == "mixed-modes") r = r with { DestinationTemplate = r.DestinationTemplate with { Crypto = "cbc192" } };
        if (problem == "mixed-modes") FailedUnchanged<NotSupportedException>(r);
        else FailedUnchanged<InvalidDataException>(r);
        string before = RegularImage.HashFile(r.DestinationTemplate.Path);
        var direct = new DirectRequest(new(r.Source.Path, 32, r.Source.Crypto, r.Source.KeyFile),
            new(r.DestinationTemplate.Path, 32, r.DestinationTemplate.Crypto, r.DestinationTemplate.KeyFile),
            Path.Combine(dir, "journal"), r.SourceRegistry, r.TargetRegistry);
        if (problem == "mixed-modes") Expect<NotSupportedException>(() => DirectMigrator.Run(direct));
        else Expect<InvalidDataException>(() => DirectMigrator.Run(direct));
        if (before != RegularImage.HashFile(r.DestinationTemplate.Path) || Directory.Exists(direct.JournalDirectory)) throw new Exception("Refused direct operation mutated target/journal.");
    });
foreach (bool target in new[] { false, true })
    Test("native-cbc-refuses-orphan-" + (target ? "target" : "source"), dir => {
        var bad = Empty(4); int cg = Fixture.Prefix + bad.Cblk * bad.Frag;
        Fixture.Set(bad.Image, cg + Fixture.R32(bad.Image, cg + 0x5C), 3, true);
        var r = Setup(dir, target ? Profile() : bad, target ? bad : Empty(), "cbc192-bswap16", reg, reg);
        string src = RegularImage.HashFile(r.Source.Path), dst = RegularImage.HashFile(r.DestinationTemplate.Path);
        var direct = new DirectRequest(new(r.Source.Path, 32, r.Source.Crypto, r.Source.KeyFile),
            new(r.DestinationTemplate.Path, 32, r.DestinationTemplate.Crypto, r.DestinationTemplate.KeyFile),
            Path.Combine(dir, "journal"), r.SourceRegistry, r.TargetRegistry);
        Expect<InvalidDataException>(() => DirectMigrator.Run(direct));
        if (src != RegularImage.HashFile(r.Source.Path) || dst != RegularImage.HashFile(r.DestinationTemplate.Path) || Directory.Exists(direct.JournalDirectory)) throw new Exception("Orphan rejection mutated input/journal.");
    });
DirectTests.Run(Test, Profile, (dir, src, dst, crypto, r, t) => Setup(dir, src, dst, crypto, r, t), reg);
RecoveryTests.Run(Test, (dir, src, dst, crypto, r, t) => Setup(dir, src, dst, crypto, r, t), reg);
TargetPreservationTests.Run(Test,Profile,(dir,src,dst,crypto,r,t)=>Setup(dir,src,dst,crypto,r,t),reg);
RootKeyTests.Run(Test,Profile,(dir,src,dst,crypto,r,t)=>Setup(dir,src,dst,crypto,r,t),reg);
CliTests.Run(Test, Profile, (dir, src, dst, crypto, r, t) => Setup(dir, src, dst, crypto, r, t), reg);
File.WriteAllBytes(Path.Combine(run, "results.json"), JsonSerializer.SerializeToUtf8Bytes(new { total = results.Count, failures, results }, ImageMigrator.Json));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed. Results: {run}");
return failures == 0 ? 0 : 1;
