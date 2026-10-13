using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.Migration;

// Generated regular files only. The latency model exposes I/O-call overhead;
// these measurements do not predict sustained throughput of a physical SSD.
static class SourceCopyPerformanceTests
{
    internal static void Run(string directory)
    {
        var source = new Fixture(20, ipg: 16384);
        source.Add("home", 4); source.Add("home/00000001", 4);
        source.Add("home/00000001/savedata", 4);
        source.Add("home/00000001/savedata/PARAM.PFD", payload: Fixture.Payload(4097, 44));
        source.Add("home/00000001/trophy", 4);
        source.Add("home/00000001/trophy/TROPUSR.DAT", payload: Fixture.Payload(16385, 45));
        source.Add("game", 4);
        var large = source.Add("game/payload.bin", payload: Fixture.Payload(64 * 1024 * 1024 + 731, 46));
        large.Ea = Fixture.Payload(17001, 47); source.Link("game", "hardlink", large);
        var sparse = source.Add("sparse", size: 20L * source.Block + 99);
        sparse.Data[0] = [1]; sparse.Data[20] = [2];
        source.Add("inline", 10, payload: "game/payload.bin"u8.ToArray(), inline: true);
        source.Finish();
        var template = new Fixture(24, ipg: 16384); template.Finish(true);
        string src = Path.Combine(directory, "generated-source.img"), dst = Path.Combine(directory, "generated-target.img");
        string key = Path.Combine(directory, "generated.key"); File.WriteAllBytes(key, Fixture.Payload(24, 48));
        var reports = new List<object>();
        foreach (string crypto in new[] { "plain", "cbc192-bswap16" })
        {
            var sourceSpec = new ImageSpec(src, 32, crypto, crypto == "plain" ? null : key);
            var targetSpec = new ImageSpec(dst, 32, crypto, crypto == "plain" ? null : key);
            Initialize(src, source.Image, sourceSpec);
            using var sourceRaw = new Observed(new DirectDisk(new(src, 32), false));
            using var sourceDisk = RegularImage.WrapCrypto(new BorrowedDisk(sourceRaw), sourceSpec);
            var s = new UfsVolume(sourceDisk, 32); s.Inventory(true);
            var n = s.Nodes[large.Ino];
            var hashCases = new List<object>();
            long previousHashReads = 0;
            foreach (bool optimized in new[] { false, true })
            {
                sourceRaw.Reset(); sourceRaw.ModelLatency = true;
                var watch = Stopwatch.StartNew(); string digest;
                if (optimized) digest = s.Hash(n);
                else
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    for (long logical = 0; logical * s.Block < n.Size; logical++) hash.AppendData(s.LogicalBytes(n, logical));
                    digest = Convert.ToHexString(hash.GetHashAndReset());
                }
                watch.Stop(); sourceRaw.ModelLatency = false;
                if (digest != n.Hash) throw new Exception("Source hash changed.");
                if (optimized && sourceRaw.ReadCalls * 4 >= previousHashReads) throw new Exception("Source hash batching did not reduce reads.");
                if (!optimized) previousHashReads = sourceRaw.ReadCalls;
                hashCases.Add(new { optimized, seconds = watch.Elapsed.TotalSeconds, sourceRaw.ReadCalls,
                    sourceRaw.ReadBytesTotal, sourceRaw.MaxRead, digest });
            }
            var copyCases = new List<object>();
            long previousReads = 0, previousWrites = 0;
            foreach (bool optimized in new[] { false, true, false, true })
            {
                Initialize(dst, template.Image, targetSpec);
                using var targetRaw = new Observed(new DirectDisk(new(dst, 32), true));
                using var targetDisk = RegularImage.WrapCrypto(new BorrowedDisk(targetRaw), targetSpec);
                var baseline = new UfsVolume(targetDisk, 32); baseline.Inventory(false);
                var layout = DirectLayout.Plan(s, baseline, default, null);
                using var guarded = RegularImage.WrapCrypto(new JournaledTarget(targetRaw, baseline, layout.Metadata), targetSpec);
                var d = new UfsVolume(guarded, 32); d.Inventory(false); d.ReleaseOldTree(false);
                sourceRaw.Reset(); targetRaw.Reset(); sourceRaw.ModelLatency = targetRaw.ModelLatency = true;
                long allocatedBefore = GC.GetTotalAllocatedBytes(true);
                var watch = Stopwatch.StartNew();
                var mapping = optimized ? TreeTransfer.Copy(s, d, null, default) : PreviousBatchedTreeTransfer.Copy(s, d, null, default);
                RegularImage.Flush(guarded); watch.Stop();
                long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
                sourceRaw.ModelLatency = targetRaw.ModelLatency = false;
                if (optimized && (sourceRaw.ReadCalls * 4 >= previousReads || targetRaw.WriteCalls * 3 >= previousWrites * 2))
                    throw new Exception("Copy read/write batching did not reduce calls.");
                if (!optimized) { previousReads = sourceRaw.ReadCalls; previousWrites = targetRaw.WriteCalls; }
                copyCases.Add(new { optimized, seconds = watch.Elapsed.TotalSeconds, allocatedManagedBytes = allocated,
                    sourceReadCalls = sourceRaw.ReadCalls, sourceReadBytes = sourceRaw.ReadBytesTotal,
                    destinationReadCalls = targetRaw.ReadCalls, destinationWriteCalls = targetRaw.WriteCalls,
                    destinationWriteBytes = targetRaw.WriteBytesTotal, targetRaw.MaxWrite });
                d.ReleaseRetiredData(); d.RecalculateAndFlush(); RegularImage.Flush(guarded);
                var check = new UfsVolume(targetDisk, 32); check.Inventory(true); ImageMigrator.Compare(s, check, mapping, default);
                Oracle.Check(source, targetDisk.ReadBytes(0, template.Image.Length),
                    new MigrationManifest(1, new(sourceSpec, targetSpec, directory), source.Items.Count,
                        mapping.Select(p => new InodeMapping(p.Key, p.Value)).ToList(), "", null, false));
            }
            reports.Add(new { crypto, generatedImagesOnly = true, modeledDelayMillisecondsPerReadOrWrite = 1,
                payloadBytes = large.Size, hashCases, copyCases, independentOraclePassed = true });
        }
        File.WriteAllBytes(Path.Combine(directory, "source-copy-performance.json"), JsonSerializer.SerializeToUtf8Bytes(reports, ImageMigrator.Json));
        File.Delete(src); File.Delete(dst); // Retain evidence, release generated image allocation.
    }
    static void Initialize(string path, byte[] plaintext, ImageSpec spec)
    {
        File.WriteAllBytes(path, plaintext);
        if (spec.Crypto == "plain") return;
        using var disk = RegularImage.Open(spec, true);
        for (int at = 0; at < plaintext.Length; at += 1024 * 1024)
            disk.WriteBytes(at, plaintext.AsSpan(at, Math.Min(1024 * 1024, plaintext.Length - at)));
        RegularImage.Flush(disk);
    }
    sealed class Observed(IIdentifiedDisk raw) : IIdentifiedDisk
    {
        public bool ModelLatency;
        public long ReadCalls, ReadBytesTotal, WriteCalls, WriteBytesTotal;
        public int MaxRead, MaxWrite;
        public void Reset() { ReadCalls = ReadBytesTotal = WriteCalls = WriteBytesTotal = 0; MaxRead = MaxWrite = 0; }
        public string Identity => raw.Identity;
        public long TotalSize => raw.TotalSize;
        public int SectorSize => 512;
        public long SectorCount => raw.SectorCount;
        public string Description => raw.Description;
        public bool CanWrite => raw.CanWrite;
        public byte[] ReadBytes(long at, int count)
        {
            if (ModelLatency) Thread.Sleep(1);
            ReadCalls++; ReadBytesTotal += count; MaxRead = Math.Max(MaxRead, count);
            return raw.ReadBytes(at, count);
        }
        public byte[] ReadSectors(long at, int count) => ReadBytes(checked(at * 512), checked(count * 512));
        public void WriteBytes(long at, byte[] bytes) => WriteBytes(at, bytes.AsSpan());
        public void WriteBytes(long at, ReadOnlySpan<byte> bytes)
        {
            if (ModelLatency) Thread.Sleep(1);
            WriteCalls++; WriteBytesTotal += bytes.Length; MaxWrite = Math.Max(MaxWrite, bytes.Length);
            raw.WriteBytes(at, bytes);
        }
        public void WriteSectors(long at, byte[] bytes) => WriteBytes(checked(at * 512), bytes);
        public void Flush() => raw.Flush();
        public void Dispose() => raw.Dispose();
    }
}
