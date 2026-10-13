using System.Buffers.Binary;
using System.Numerics;
using PS3HddTool.Core.Migration;

// Independent streaming fixture author: full-capacity UFS maps, not a tiny FS
// padded to a huge apparent length. Uses no production UFS parser/allocator.
sealed class SparseFixture
{
    internal const long Offset = 1048576;
    internal const int Block = 16384, Frag = 4096, Fpb = 4, Fpg = 65536, Ipg = 64, Sblk = 20, Cblk = 24, Iblk = 28, Dblk = 32;
    internal readonly long DiskBytes, Fragments;
    internal readonly int Groups, SummaryBytes;
    internal readonly List<DiskRange> PayloadRanges = new();
    internal SparseFixture(long bytes)
    {
        DiskBytes = bytes; Fragments = (bytes - Offset - 4096) / Frag;
        Groups = checked((int)((Fragments + Fpg - 1) / Fpg)); SummaryBytes = (Groups * 16 + Frag - 1) / Frag * Frag;
        if (Fragments % Fpg <= Dblk + 128) throw new Exception("Fixture final CG is too small.");
    }
    internal void Create(DirectEndpoint endpoint, Fixture expected, bool compactZeros = false)
    {
        if (endpoint.Kind != "image" || expected.Block != Block || expected.Frag != Frag || expected.Ipg != Ipg)
            throw new Exception("Sparse fixture is regular-image-only with fixed test geometry.");
        SparseFile.Create(endpoint.Path, DiskBytes);
        using IIdentifiedDisk raw = compactZeros ? new SparseZeroDisk(endpoint, true) : new DirectDisk(endpoint, true);
        raw.WriteBytes(0, Enumerable.Repeat((byte)0xA5, (int)Offset).ToArray());
        raw.WriteBytes(DiskBytes - 4096, Enumerable.Repeat((byte)0x5A, 4096).ToArray());
        using var disk = RegularImage.WrapCrypto(new BorrowedDisk(raw), endpoint.Image);
        byte[] sb = (byte[])expected.Super.Clone();
        Fixture.W32(sb, 8, Sblk); Fixture.W32(sb, 12, Cblk); Fixture.W32(sb, 16, Iblk); Fixture.W32(sb, 20, Dblk);
        Fixture.W32(sb, 0x2C, Groups); Fixture.W32(sb, 0xBC, Fpg); Fixture.W32(sb, 0x9C, SummaryBytes);
        Fixture.W64(sb, 0x438, Fragments); Fixture.W64(sb, 0x448, Dblk); Fixture.W64(sb, 0x3E8, 65536);
        var owned = new HashSet<long>(); var pointers = new Dictionary<long, byte[]>();
        long next = (long)(Groups - 1) * Fpg + Dblk + 128;
        bool wrapped = false;
        long Allocate(int count)
        {
            if (next % Fpg < Dblk)
                next = next / Fpg * Fpg + Dblk + (next / Fpg == 0 ? SummaryBytes / Frag : 0);
            if (next % Fpb + count > Fpb || (count == Fpb && next % Fpb != 0)) next = (next + Fpb - 1) / Fpb * Fpb;
            if (next + count > Math.Min(Fragments, (next / Fpg + 1) * Fpg))
            {
                long cg = next / Fpg + 1;
                if (cg == Groups) { if (wrapped) throw new Exception("Fixture capacity exceeded."); cg = 0; wrapped = true; }
                if (wrapped && cg == Groups - 1) throw new Exception("Fixture capacity exceeded.");
                next = cg * Fpg + Dblk + (cg == 0 ? SummaryBytes / Frag : 0);
                next = (next + Fpb - 1) / Fpb * Fpb;
            }
            long at = next; next += count;
            for (int i = 0; i < count; i++) if (!owned.Add(at + i)) throw new Exception("Fixture duplicate allocation.");
            var range = new DiskRange(Offset + at * Frag, count * Frag);
            if (PayloadRanges.Count > 0 && PayloadRanges[^1].Offset + PayloadRanges[^1].Length == range.Offset)
                PayloadRanges[^1] = PayloadRanges[^1] with { Length = PayloadRanges[^1].Length + range.Length };
            else PayloadRanges.Add(range);
            return at;
        }
        long SetPointer(byte[] inode, long logical, long data)
        {
            if (logical < 12) { Fixture.W64(inode, 0x70 + (int)logical * 8, data); return 0; }
            int fan = Block / 8; long relative = logical - 12, capacity = fan, added = 0;
            for (int depth = 1; depth <= 3; depth++, capacity *= fan)
            {
                if (relative >= capacity) { relative -= capacity; continue; }
                int root = 0xC8 + depth * 8; long p = Fixture.R64(inode, root);
                long NewTable() { long n = Allocate(Fpb); pointers.Add(n, new byte[Block]); added++; return n; }
                if (p == 0) { p = NewTable(); Fixture.W64(inode, root, p); }
                for (int level = depth; level > 0; level--)
                {
                    long stride = 1; for (int i = 1; i < level; i++) stride *= fan;
                    int index = (int)(relative / stride) * 8; relative %= stride;
                    if (level == 1) Fixture.W64(pointers[p], index, data);
                    else { long child = Fixture.R64(pointers[p], index); if (child == 0) { child = NewTable(); Fixture.W64(pointers[p], index, child); } p = child; }
                }
                return added * Block / 512;
            }
            throw new Exception("Fixture indirect capacity.");
        }
        foreach (var n in expected.Items)
        {
            byte[] inode = (byte[])n.Raw.Clone(); Array.Clear(inode, 0x60, 16);
            if (!(n.Type == 10 && n.Data.Count == 0)) Array.Clear(inode, 0x70, 120);
            long sectors = 0;
            foreach (var (logical, payload) in n.Data)
            {
                int count = n.Size <= 12L * Block ? (int)Math.Min(Fpb, (n.Size - logical * Block + Frag - 1) / Frag) : Fpb;
                long at = Allocate(count); byte[] bytes = new byte[count * Frag]; payload.CopyTo(bytes, 0);
                disk.WriteBytes(Offset + at * Frag, bytes); sectors += count * Frag / 512 + SetPointer(inode, logical, at);
            }
            for (int i = 0; i * Block < n.Ea.Length; i++)
            {
                int length = Math.Min(Block, n.Ea.Length - i * Block), count = (length + Frag - 1) / Frag;
                long at = Allocate(count); byte[] bytes = new byte[count * Frag]; n.Ea.AsSpan(i * Block, length).CopyTo(bytes);
                disk.WriteBytes(Offset + at * Frag, bytes); Fixture.W64(inode, 0x60 + i * 8, at); sectors += count * Frag / 512;
            }
            Fixture.W64(inode, 0x18, sectors);
            RegularImage.Write(disk, Offset + (n.Ino / Ipg * Fpg + Iblk) * Frag + n.Ino % Ipg * 256, inode);
        }
        foreach (var (at, bytes) in pointers) disk.WriteBytes(Offset + at * Frag, bytes);
        var payloadGroups = owned.GroupBy(f => (int)(f / Fpg)).ToDictionary(g => g.Key, g => g.ToArray());
        var inodeGroups = expected.Items.GroupBy(n => (int)(n.Ino / Ipg)).ToDictionary(g => g.Key, g => g.ToArray());
        byte[] MakeGroup(int c)
        {
            int count = (int)Math.Min(Fpg, Fragments - (long)c * Fpg);
            byte[] g = new byte[Block]; int imap = 168, fmap = imap + Ipg / 8, so = fmap + Fpg / 8 - 4, co = so + 68;
            Fixture.W32(g, 4, 0x90255); Fixture.W32(g, 12, c); Fixture.W32(g, 0x14, count);
            Fixture.W32(g, 0x5C, imap); Fixture.W32(g, 0x60, fmap); Fixture.W32(g, 0x68, so); Fixture.W32(g, 0x6C, co);
            Fixture.W32(g, 0x70, count / Fpb); Fixture.W32(g, 0x74, Ipg); Fixture.W32(g, 0x78, inodeGroups.ContainsKey(c) ? Ipg : 0);
            g.AsSpan(fmap, (count + 7) / 8).Fill(0xFF);
            for (int f = count; f % 8 != 0; f++) Fixture.Set(g, fmap, f, false);
            for (int f = c == 0 ? 0 : Sblk; f < Dblk; f++) Fixture.Set(g, fmap, f, false);
            if (c == 0)
            {
                Fixture.Set(g, imap, 0, true); Fixture.Set(g, imap, 1, true);
                for (int f = Dblk; f < Dblk + SummaryBytes / Frag; f++) Fixture.Set(g, fmap, f, false);
            }
            if (payloadGroups.TryGetValue(c, out var allocations)) foreach (long f in allocations) Fixture.Set(g, fmap, (int)(f % Fpg), false);
            if (inodeGroups.TryGetValue(c, out var inodes)) foreach (var n in inodes) Fixture.Set(g, imap, (int)(n.Ino % Ipg), true);
            int[] sums = [inodes?.Count(n => n.Type == 4) ?? 0, 0, Ipg - (inodes?.Length ?? 0) - (c == 0 ? 2 : 0), 0];
            int run = 0;
            void EndRun() { if (run > 0) { int p = so + Math.Min(run, 16) * 4; Fixture.W32(g, p, Fixture.R32(g, p) + 1); run = 0; } }
            for (int f = 0; f < count; f += Fpb)
            {
                int mask = (g[fmap + f / 8] >> (f % 8)) & 15;
                if (mask == 15 && f + Fpb <= count) { sums[1]++; Fixture.Set(g, co, f / Fpb, true); run++; }
                else
                {
                    EndRun(); sums[3] += BitOperations.PopCount((uint)mask);
                    for (int j = 0; j < Fpb;)
                    {
                        if ((mask & (1 << j)) == 0) { j++; continue; }
                        int first = j; while (j < Fpb && (mask & (1 << j)) != 0) j++;
                        int p = 0x34 + (j - first) * 4; Fixture.W32(g, p, Fixture.R32(g, p) + 1);
                    }
                }
            }
            EndRun(); for (int i = 0; i < 4; i++) Fixture.W32(g, 0x18 + i * 4, sums[i]); return g;
        }
        int emptyGroup = Enumerable.Range(1, Groups - 2).First(c => !inodeGroups.ContainsKey(c) && !payloadGroups.ContainsKey(c));
        byte[] standard = MakeGroup(emptyGroup), summary = new byte[SummaryBytes]; long[] totals = new long[4];
        for (int c = 0; c < Groups; c++)
        {
            byte[] g = c == 0 || c == Groups - 1 || inodeGroups.ContainsKey(c) || payloadGroups.ContainsKey(c) ? MakeGroup(c) : (byte[])standard.Clone();
            Fixture.W32(g, 12, c); disk.WriteBytes(Offset + ((long)c * Fpg + Cblk) * Frag, g);
            for (int i = 0; i < 4; i++) { int n = Fixture.R32(g, 0x18 + i * 4); Fixture.W32(summary, c * 16 + i * 4, n); totals[i] += n; }
        }
        for (int i = 0; i < 4; i++) Fixture.W64(sb, 0x3F0 + i * 8, totals[i]);
        disk.WriteBytes(Offset + (long)Dblk * Frag, summary); disk.WriteBytes(Offset + 65536, sb);
        for (int c = 0; c < Groups; c++) disk.WriteBytes(Offset + ((long)c * Fpg + Sblk) * Frag, sb);
        RegularImage.Flush(disk);
    }
}
