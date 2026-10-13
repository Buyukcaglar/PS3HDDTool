namespace PS3HddTool.Core.Migration;

internal static class TreeTransfer
{
    internal static Dictionary<long, long> Copy(UfsVolume source, UfsVolume destination,
        Action<string>? progress, CancellationToken token, Action<MigrationProgress>? telemetry = null)
    {
        var meter = new ProgressMeter(telemetry, "copy-tree", "Copy allocated filesystem tree",
            source.Nodes.Values.Sum(n => n.Sectors) * 512);
        long finished = 0;
        meter.Add(0, nodesDone: 0, nodesTotal: source.Nodes.Count, force: true);
        var mapping = new Dictionary<long, long> { [2] = destination.AllocateInode(true) };
        foreach (long ino in source.Nodes.Keys.Order())
        { token.ThrowIfCancellationRequested(); if (ino != 2) mapping.Add(ino, destination.AllocateInode()); }
        var dataBuffer = new TransferDataBuffer(destination, token);
        foreach (Node n in source.Nodes.Values.OrderBy(n => n.Number))
        {
            token.ThrowIfCancellationRequested(); progress?.Invoke($"Copy inode {n.Number}"); token.ThrowIfCancellationRequested();
            byte[] raw = (byte[])n.Raw.Clone(); long sectors = 0;
            var pointersCache = new TransferPointerCache(destination, token);
            using var sourceHash = new TransferSourceHash(n, token);
            Array.Clear(raw, 0x60, 16);
            if (!n.Inline && n.Type is 4 or 8 or 10)
            {
                Array.Clear(raw, 0x70, 120);
                // Read each indirect page once and amortize source I/O/crypto over
                // contiguous runs. Allocated tail padding is copied unchanged;
                // TransferSourceHash still hashes only the logical file bytes.
                using var blocks = source.NonzeroBlocks(n).GetEnumerator();
                bool more = blocks.MoveNext();
                while (more)
                {
                    token.ThrowIfCancellationRequested();
                    var first = blocks.Current;
                    int length = source.DataLength(n, first.Logical);
                    more = blocks.MoveNext();
                    while (more && length % source.Block == 0 && length <= 1024 * 1024 - source.Block &&
                        blocks.Current.Logical == first.Logical + length / source.Block &&
                        blocks.Current.Fragment == first.Fragment + length / source.Fragment)
                    {
                        length += source.DataLength(n, blocks.Current.Logical);
                        more = blocks.MoveNext();
                    }
                    byte[] batch = source.Read(first.Fragment, length);
                    for (int at = 0; at < batch.Length;)
                    {
                        token.ThrowIfCancellationRequested();
                        long logical = first.Logical + at / source.Block;
                        int count = source.DataLength(n, logical);
                        var bytes = batch.AsSpan(at, count);
                        sourceHash.Add(checked(logical * source.Block), bytes);
                        if (n.Type == 4)
                        {
                            byte[] directory = bytes.ToArray();
                            ImageMigrator.RewriteDirectory(directory, (int)Math.Min(count, n.Size - logical * source.Block), mapping);
                            directory.CopyTo(bytes);
                        }
                        long address = destination.Allocate(count / destination.Fragment);
                        dataBuffer.Write(address, bytes); sectors += count / 512;
                        long pointers = destination.PutPointer(raw, logical, address, pointersCache); sectors += pointers;
                        meter.Add(count + pointers * 512, n.Number, n.Path, finished, source.Nodes.Count);
                        at += count;
                    }
                }
            }
            sourceHash.Complete(); dataBuffer.Flush(); pointersCache.Flush();
            int ea = UfsVolume.I32(n.Raw, 0x5C);
            for (int i = 0; i * source.Block < ea; i++)
            {
                token.ThrowIfCancellationRequested();
                int length = ((Math.Min(source.Block, ea - i * source.Block) + source.Fragment - 1) / source.Fragment) * source.Fragment;
                byte[] bytes = source.Read(UfsVolume.I64(n.Raw, 0x60 + i * 8), length);
                long address = destination.Allocate(length / destination.Fragment); destination.Write(address, bytes);
                UfsVolume.P64(raw, 0x60 + i * 8, address); sectors += length / 512;
                meter.Add(length, n.Number, n.Path, finished, source.Nodes.Count);
            }
            UfsVolume.P64(raw, 0x18, sectors);
            RegularImage.Write(destination.Disk, destination.InodeOffset(mapping[n.Number]), raw);
            meter.Add(0, n.Number, n.Path, ++finished, source.Nodes.Count, force: finished == source.Nodes.Count);
        }
        return mapping;
    }
}
