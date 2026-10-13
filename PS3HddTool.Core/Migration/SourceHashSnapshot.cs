using System.Security.Cryptography;
using System.Text.Json;

namespace PS3HddTool.Core.Migration;

internal sealed record SourceHashNode(long Number, byte[] Raw, string Hash, List<Entry> Entries);
internal sealed record SourceHashSnapshot(int Version, string SourceDigest, List<SourceHashNode> Nodes)
{
    internal static string Save(UfsVolume source, string path)
    {
        var snapshot = new SourceHashSnapshot(1, DirectLayout.TreeDigest(source),
            source.Nodes.Values.Select(n => new SourceHashNode(n.Number, n.Raw, n.Hash, n.Entries)).ToList());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, ImageMigrator.Json);
        using (var file = new FileStream(RegularImage.CheckedPath(path, false), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        if (RegularImage.HashFile(path) != digest) throw new IOException("Source hash checkpoint readback failed.");
        return digest;
    }
    internal static void Apply(UfsVolume source, string path, string expectedSha256, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // One lease prevents replacement between checksum validation and parsing.
        using var file = new FileStream(RegularImage.CheckedPath(path),FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length > 1024L*1024*1024)throw new InvalidDataException("Source hash snapshot exceeds supported size.");
        if(Convert.ToHexString(SHA256.HashData(file)) != expectedSha256)throw new InvalidDataException("Source hash snapshot checksum mismatch.");
        file.Position=0;
        var saved=JsonSerializer.Deserialize<SourceHashSnapshot>(file,ImageMigrator.Json)??throw new InvalidDataException("Missing source hash snapshot.");
        if(saved.Version!=1||saved.Nodes.Count!=source.Nodes.Count||saved.Nodes.Select(n=>n.Number).Distinct().Count()!=source.Nodes.Count)
            throw new InvalidDataException("Source hash snapshot node count/version mismatch.");
        foreach(var n in saved.Nodes){
            token.ThrowIfCancellationRequested();
            if(!source.Nodes.TryGetValue(n.Number,out var current)||!current.Raw.AsSpan().SequenceEqual(n.Raw))throw new InvalidDataException("Source metadata differs from hash snapshot.");
            if(current.Type is 8 or 10){if(n.Hash.Length!=64||n.Hash.Any(c=>!char.IsAsciiHexDigit(c)||char.IsLower(c)))throw new InvalidDataException("Invalid saved content hash.");}
            else if(n.Hash!="")throw new InvalidDataException("Unexpected saved directory/special hash.");
            current.Hash=n.Hash;
        }
        if(DirectLayout.TreeDigest(source)!=saved.SourceDigest)throw new InvalidDataException("Source tree differs from hash snapshot.");
        // Source contents are checked against these hashes while they are copied.
        // This skips only the separate pre-hash pass, never content verification.
    }
}

internal sealed class TransferSourceHash : IDisposable
{
    readonly Node _node; readonly CancellationToken _token; readonly IncrementalHash? _hash;
    static readonly byte[] Zeros=new byte[1024*1024];
    long _position;
    internal TransferSourceHash(Node node,CancellationToken token){_node=node;_token=token;
        if(node.Hash.Length>0 && node.Type is 8 or 10)_hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if(_hash!=null&&node.Inline){_hash.AppendData(node.Raw.AsSpan(0x70,checked((int)node.Size)));_position=node.Size;}}
    void Hole(long until){if(until<_position)throw new InvalidDataException("Source blocks are not in logical order.");while(_position<until){_token.ThrowIfCancellationRequested();int size=(int)Math.Min(Zeros.Length,until-_position);_hash!.AppendData(Zeros.AsSpan(0,size));_position+=size;}}
    internal void Add(long offset,ReadOnlySpan<byte> data){if(_hash==null)return;Hole(offset);int size=checked((int)Math.Min(data.Length,_node.Size-offset));_hash.AppendData(data[..size]);_position=offset+size;}
    internal void Complete(){if(_hash==null)return;Hole(_node.Size);if(Convert.ToHexString(_hash.GetHashAndReset())!=_node.Hash)throw new InvalidDataException($"Source inode {_node.Number} changed since hashing.");}
    public void Dispose()=>_hash?.Dispose();
}
