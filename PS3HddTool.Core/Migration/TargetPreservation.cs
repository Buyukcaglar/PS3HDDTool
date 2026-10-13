using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

public sealed record TargetPreservationReport(string ManifestSha256, string ArchiveSha256,
    long ArchiveBytes, long DiskBytes, int Nodes, int OrphanFiles);
public sealed record TargetPreservationPlan(long ArchiveBytes, long DiskBytes, int Ranges,
    int Nodes, int OrphanFiles, bool UnallocatedDataIncluded = false);
internal sealed record TargetArchiveManifest(int Version, string Identity, long DiskBytes,
    long PartitionSector, string Crypto, string Recovery, int Nodes, int OrphanFiles,
    List<DiskRange> Ranges, long ArchiveBytes, string ArchiveSha256, string? RegistrySha256);
internal sealed record TargetArchiveEnvelope(TargetArchiveManifest Manifest, string Sha256);

// Independent raw-ciphertext backup of all live target bytes and interpretable
// filesystem metadata. No physical writer is present in this class. Free payload
// and uninitialized inode space are omitted; export produces a sparse raw image.
public static class TargetPreservation
{
    internal static void Inventory(UfsVolume volume, string? mode, CancellationToken token,
        Action<MigrationProgress>? telemetry)
    {
        MigrationSource.ValidateMode(mode);
        if (mode is null or "none") volume.Inventory(false, token, telemetry, "destination");
        else volume.TrackPreservedRecovery(volume.QualifyDetachedRecovery(token, telemetry));
    }
    internal static List<DiskRange> Ranges(UfsVolume volume)
    {
        var list = DirectLayout.DataRanges(volume);
        list.AddRange(DirectLayout.Outside(volume));
        list.Add(new(volume.Offset, checked((long)volume.Iblk*volume.Fragment)));
        int sblk=UfsVolume.I32(volume.Super,8);
        foreach(Group g in volume.Groups)
        {
            long at=checked(volume.Offset+(long)g.Number*volume.Fpg*volume.Fragment);
            list.Add(new(at+(long)sblk*volume.Fragment,(long)(volume.Iblk-sblk)*volume.Fragment));
            long initialized=UfsVolume.I32(g.Raw,0x78);
            if(initialized>0) list.Add(new(at+(long)volume.Iblk*volume.Fragment,(initialized*256+511)/512*512));
        }
        list.Add(new(volume.Offset+UfsVolume.I64(volume.Super,0x448)*volume.Fragment,UfsVolume.I32(volume.Super,0x9C)));
        return DirectLayout.Merge(list);
    }
    internal static TargetPreservationPlan MakePlan(UfsVolume volume)
    {
        var ranges=Ranges(volume);
        return new(DirectLayout.Chunks(ranges).Sum(r=>checked(r.Length+DirectJournal.RecordHeader)),
            volume.Disk.TotalSize,ranges.Count,volume.Nodes.Count,volume.PreservedOrphanFiles);
    }
    internal static string DirectoryPath(DirectRequest request)
    {
        if(request.TargetPreservation==null) throw new ArgumentException("TargetPreservation archive directory is required.");
        string path=RegularImage.CheckedPath(request.TargetPreservation,false);
        foreach(string other in new[]{request.JournalDirectory,request.Source.Path,request.Destination.Path,
            request.Source.KeyFile,request.Destination.KeyFile,request.SourceRegistry,request.TargetRegistry}.OfType<string>())
        {
            if(other.StartsWith(@"\\.\",StringComparison.Ordinal))continue;
            string full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(other));
            if(path.Equals(full,StringComparison.OrdinalIgnoreCase)||path.StartsWith(full+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
                full.StartsWith(path+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Target preservation directory overlaps an input or journal.");
        }
        return path;
    }
    public static TargetPreservationPlan Plan(DirectRequest request,CancellationToken token=default,Action<MigrationProgress>? telemetry=null)
    {
        token.ThrowIfCancellationRequested(); MigrationSource.ValidateMode(request.TargetRecovery);
        using var raw=new DirectDisk(request.Destination,false);
        using var disk=RegularImage.WrapCrypto(new BorrowedDisk(raw),request.Destination.Image);
        var volume=new UfsVolume(disk,request.Destination.PartitionSector);
        Inventory(volume,request.TargetRecovery,token,telemetry);
        ImageMigrator.ValidateUsers(volume,ReadRegistry(request));
        return MakePlan(volume);
    }
    public static TargetPreservationReport Create(DirectRequest request,CancellationToken token=default,Action<MigrationProgress>? telemetry=null)
    {
        using var raw=new DirectDisk(request.Destination,false);
        using var disk=RegularImage.WrapCrypto(new BorrowedDisk(raw),request.Destination.Image);
        return CreateCore(request,raw,disk,token,telemetry);
    }
    // Allows an independently identity-checked read-only transport to make a host
    // backup without changing Windows disk state or exporting a derived key.
    // Admission for migration STILL independently validates native handle identity,
    // qualified layout and every archived raw byte through DirectDisk's guards.
    public static TargetPreservationReport CreateReadOnly(DirectRequest request,IDiskSource readOnlyRaw,
        string checkedHandleIdentity,byte[]? derivedKey,CancellationToken token=default,Action<MigrationProgress>? telemetry=null)
    {
        if(readOnlyRaw.CanWrite||readOnlyRaw.SectorSize!=512||string.IsNullOrWhiteSpace(checkedHandleIdentity))
            throw new ArgumentException("Identity-checked read-only 512-byte transport required.");
        using var raw=new ReadOnlyIdentified(readOnlyRaw,checkedHandleIdentity);
        using IDiskSource disk=request.Destination.Crypto switch{
            "plain" when derivedKey==null=>new BorrowedDisk(raw),
            "cbc192" or "cbc192-bswap16" when derivedKey?.Length==24=>new DecryptedDiskSourceCbc(new BorrowedDisk(raw),derivedKey,request.Destination.Crypto=="cbc192-bswap16"),
            "xts128" when derivedKey?.Length==32=>new DecryptedDiskSource(new BorrowedDisk(raw),derivedKey[..16],derivedKey[16..],true),
            _=>throw new ArgumentException("Invalid in-memory derived key/encryption mode.")};
        return CreateCore(request,raw,disk,token,telemetry);
    }
    private sealed class ReadOnlyIdentified(IDiskSource raw,string identity):IIdentifiedDisk
    {
        public string Identity=>identity;public long TotalSize=>raw.TotalSize;public int SectorSize=>512;public long SectorCount=>raw.TotalSize/512;
        public bool CanWrite=>false;public string Description=>"Identity-checked read-only preservation transport";
        public byte[] ReadBytes(long offset,int count)=>raw.ReadBytes(offset,count);public byte[] ReadSectors(long sector,int count)=>raw.ReadSectors(sector,count);
        public void WriteBytes(long offset,byte[] data)=>throw new InvalidOperationException("Read-only archive transport");
        public void WriteBytes(long offset,ReadOnlySpan<byte> data)=>throw new InvalidOperationException("Read-only archive transport");
        public void WriteSectors(long sector,byte[] data)=>throw new InvalidOperationException("Read-only archive transport");
        public void Flush()=>throw new InvalidOperationException("No physical flush in read-only transport");public void Dispose(){}
    }
    private static TargetPreservationReport CreateCore(DirectRequest request,IIdentifiedDisk raw,IDiskSource disk,CancellationToken token,Action<MigrationProgress>? telemetry)
    {
        token.ThrowIfCancellationRequested();string path=DirectoryPath(request);
        if(Directory.Exists(path)||File.Exists(path))throw new IOException("Preservation output must not exist; incomplete archives are never reused.");
        var volume=new UfsVolume(disk,request.Destination.PartitionSector);
        Inventory(volume,request.TargetRecovery,token,telemetry);
        byte[]? registry=ReadRegistry(request);ImageMigrator.ValidateUsers(volume,registry);
        var plan=MakePlan(volume);var ranges=Ranges(volume);
        if(new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace<plan.ArchiveBytes+1024L*1024*1024)
            throw new IOException("Insufficient host space for preservation archive and 1GiB reserve.");
        Directory.CreateDirectory(path);
        string dataPath=Path.Combine(path,"preservation.bin");
        var meter=new ProgressMeter(telemetry,"target-preserve","Archive original target allocated bytes",plan.ArchiveBytes);
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using(var output=new FileStream(dataPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            foreach(var range in DirectLayout.Chunks(ranges))
            {
                token.ThrowIfCancellationRequested();byte[] data=raw.ReadBytes(range.Offset,checked((int)range.Length));
                byte[] header=Header(range,data);output.Write(header);output.Write(data);
                digest.AppendData(header);digest.AppendData(data);meter.Add(header.Length+data.Length);
            }
            output.Flush(true);
        }
        var manifest=new TargetArchiveManifest(1,raw.Identity,raw.TotalSize,request.Destination.PartitionSector,
            request.Destination.Crypto,request.TargetRecovery??"none",plan.Nodes,plan.OrphanFiles,ranges,
            plan.ArchiveBytes,Convert.ToHexString(digest.GetHashAndReset()),registry==null?null:Convert.ToHexString(SHA256.HashData(registry)));
        // Read back every chunk before publishing the manifest/completion marker.
        using(var file=new FileStream(dataPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            CheckPayload(file,manifest,raw,token,telemetry);
        string sha=ManifestHash(manifest);
        if(registry!=null)
            using(var file=new FileStream(Path.Combine(path,"target-xRegistry.sys"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {file.Write(registry);file.Flush(true);}
        using(var file=new FileStream(Path.Combine(path,"manifest.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {file.Write(JsonSerializer.SerializeToUtf8Bytes(new TargetArchiveEnvelope(manifest,sha),ImageMigrator.Json));file.Flush(true);}
        using var verified=Open(path);verified.ValidateLive(raw,volume,request.Destination.Crypto,request.TargetRecovery,token,telemetry);
        return verified.Report;
    }
    public static TargetPreservationReport Verify(string directory,CancellationToken token=default,Action<MigrationProgress>? telemetry=null)
    {using var archive=Open(directory);archive.Check(token,telemetry);return archive.Report;}
    public static TargetPreservationReport Export(string directory,string output,CancellationToken token=default,Action<MigrationProgress>? telemetry=null)
    {
        using var archive=Open(directory);string final=RegularImage.CheckedPath(output,false),partial=RegularImage.CheckedPath(output+".partial",false);
        if(File.Exists(final)||Directory.Exists(final))throw new IOException("Export output already exists.");
        using(var file=new FileStream(partial,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
        {
            if(OperatingSystem.IsWindows()&&!DeviceIoControl(file.SafeFileHandle,0x900C4,IntPtr.Zero,0,IntPtr.Zero,0,out _,IntPtr.Zero))throw new Win32Exception();
            file.SetLength(archive.Manifest.DiskBytes);
            archive.Check(token,telemetry,(offset,bytes)=>RandomAccess.Write(file.SafeFileHandle,bytes,offset));file.Flush(true);
            // Validate host export against the complete archive while it is locked.
            archive.Check(token,telemetry,(offset,bytes)=>{
                byte[] actual=new byte[bytes.Length];int at=0;
                while(at<actual.Length){int n=RandomAccess.Read(file.SafeFileHandle,actual.AsSpan(at),offset+at);if(n==0)throw new EndOfStreamException();at+=n;}
                if(!actual.AsSpan().SequenceEqual(bytes))throw new IOException("Preservation export readback mismatch.");
            });
        }
        File.Move(partial,final,false);return archive.Report;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle h,uint code,IntPtr input,uint inputSize,IntPtr output,uint outputSize,out uint returned,IntPtr overlapped);
    internal static HeldArchive Open(string path)=>new(path);
    private static byte[]? ReadRegistry(DirectRequest request)=>request.TargetRegistry==null?null:File.ReadAllBytes(RegularImage.CheckedPath(request.TargetRegistry));
    private static string ManifestHash(TargetArchiveManifest manifest)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest,ImageMigrator.Json)));
    private static byte[] Header(DiskRange range,byte[] data)
    {
        byte[] h=new byte[DirectJournal.RecordHeader];BinaryPrimitives.WriteInt64LittleEndian(h,range.Offset);BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8),data.Length);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData(h.AsSpan(0,12));hash.AppendData(data);hash.GetHashAndReset().CopyTo(h,12);return h;
    }
    private static void CheckPayload(FileStream file,TargetArchiveManifest manifest,IIdentifiedDisk? live,CancellationToken token,
        Action<MigrationProgress>? telemetry,Action<long,byte[]>? action=null)
    {
        file.Position=0;using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var meter=new ProgressMeter(telemetry,"target-preserve-verify",live==null?"Verify target archive":"Match target archive to live original",manifest.ArchiveBytes);
        foreach(var range in DirectLayout.Chunks(manifest.Ranges))
        {
            token.ThrowIfCancellationRequested();byte[] h=new byte[DirectJournal.RecordHeader],data=new byte[checked((int)range.Length)];
            file.ReadExactly(h);file.ReadExactly(data);
            if(!h.AsSpan().SequenceEqual(Header(range,data)))throw new InvalidDataException("Preservation chunk address/checksum mismatch.");
            digest.AppendData(h);digest.AppendData(data);
            if(live!=null&&!live.ReadBytes(range.Offset,data.Length).AsSpan().SequenceEqual(data))throw new InvalidDataException("Live destination differs from preservation archive.");
            action?.Invoke(range.Offset,data);meter.Add(h.Length+data.Length);
        }
        if(file.Position!=file.Length||Convert.ToHexString(digest.GetHashAndReset())!=manifest.ArchiveSha256)
            throw new InvalidDataException("Preservation archive length/checksum mismatch.");
    }
    internal sealed class HeldArchive:IDisposable
    {
        private readonly FileStream _manifest,_data;
        private readonly FileStream? _registry;
        internal TargetArchiveManifest Manifest {get;}
        internal TargetPreservationReport Report {get;}
        internal HeldArchive(string directory)
        {
            directory=RegularImage.CheckedPath(directory,false);
            _manifest=new(RegularImage.CheckedPath(Path.Combine(directory,"manifest.json")),FileMode.Open,FileAccess.Read,FileShare.Read);
            try{
                if(_manifest.Length>16*1024*1024)throw new InvalidDataException("Preservation manifest too large.");
                using var document=JsonDocument.Parse(_manifest);
                CheckJsonNames(document.RootElement);
                var envelope=document.RootElement.Deserialize<TargetArchiveEnvelope>(new JsonSerializerOptions(ImageMigrator.Json){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Missing preservation manifest.");
                Manifest=envelope.Manifest;
                if(envelope.Sha256!=ManifestHash(Manifest)||Manifest.Version!=1||Manifest.DiskBytes<=0||Manifest.DiskBytes>16L*1024*1024*1024*1024||
                    Manifest.DiskBytes%512!=0||Manifest.PartitionSector<0||Manifest.Nodes<1||Manifest.OrphanFiles<0||Manifest.Ranges.Count==0||
                    !DirectLayout.Merge(Manifest.Ranges).SequenceEqual(Manifest.Ranges)||Manifest.Ranges.Any(r=>r.Offset>Manifest.DiskBytes-r.Length)||
                    DirectLayout.Chunks(Manifest.Ranges).Sum(r=>checked(r.Length+DirectJournal.RecordHeader))!=Manifest.ArchiveBytes)
                    throw new InvalidDataException("Invalid preservation manifest/ranges.");
                MigrationSource.ValidateMode(Manifest.Recovery);
                if(Manifest.RegistrySha256!=null)
                {
                    _registry=new(RegularImage.CheckedPath(Path.Combine(directory,"target-xRegistry.sys")),FileMode.Open,FileAccess.Read,FileShare.Read);
                    if(_registry.Length!=262144)throw new InvalidDataException("Target registry bundle length mismatch.");
                    byte[] registry=new byte[262144];_registry.ReadExactly(registry);
                    if(Convert.ToHexString(SHA256.HashData(registry))!=Manifest.RegistrySha256)throw new InvalidDataException("Target registry bundle checksum mismatch.");
                    _=ConsoleState.ReadUsers(registry);
                }
                _data=new(RegularImage.CheckedPath(Path.Combine(directory,"preservation.bin")),FileMode.Open,FileAccess.Read,FileShare.Read);
                if(_data.Length!=Manifest.ArchiveBytes){_data.Dispose();throw new InvalidDataException("Truncated preservation archive.");}
                Report=new(envelope.Sha256,Manifest.ArchiveSha256,Manifest.ArchiveBytes,Manifest.DiskBytes,Manifest.Nodes,Manifest.OrphanFiles);
            }catch{_registry?.Dispose();_manifest.Dispose();throw;}
        }
        internal void Check(CancellationToken token,Action<MigrationProgress>? telemetry,Action<long,byte[]>? action=null)=>CheckPayload(_data,Manifest,null,token,telemetry,action);
        internal void ValidateLive(IIdentifiedDisk raw,UfsVolume volume,string crypto,string? recovery,CancellationToken token,Action<MigrationProgress>? telemetry)
        {
            if(Manifest.Identity!=raw.Identity||Manifest.DiskBytes!=raw.TotalSize||Manifest.PartitionSector!=volume.Offset/512||Manifest.Crypto!=crypto||
                Manifest.Recovery!=(recovery??"none")||Manifest.Nodes!=volume.Nodes.Count||Manifest.OrphanFiles!=volume.PreservedOrphanFiles||!Manifest.Ranges.SequenceEqual(Ranges(volume)))
                throw new InvalidDataException("Preservation archive identity/layout/recovery mismatch.");
            CheckPayload(_data,Manifest,raw,token,telemetry);
        }
        public void Dispose(){_data.Dispose();_registry?.Dispose();_manifest.Dispose();}
    }
    private static void CheckJsonNames(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in element.EnumerateObject())
            {if(!names.Add(property.Name))throw new InvalidDataException("Duplicate preservation manifest field.");CheckJsonNames(property.Value);}
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())CheckJsonNames(child);
    }
}
