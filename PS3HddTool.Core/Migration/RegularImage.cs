using System.Security.Cryptography;
using System.Text.Json.Serialization;
using PS3HddTool.Core.Crypto;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

public sealed record ImageSpec(string Path, long PartitionSector, string Crypto = "plain", string? KeyFile = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? KeyKind = null);

public static class RegularImage
{
    // No devices, UNC paths, links, alternate streams, or reparse-point ancestors.
    public static string CheckedPath(string path, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\") || path.StartsWith("//") ||
            path.StartsWith("/dev/", StringComparison.Ordinal) || path.Contains('\0'))
            throw new ArgumentException("Only local regular files/directories are allowed.");
        string full = System.IO.Path.GetFullPath(path);
        if (full.AsSpan(System.IO.Path.GetPathRoot(full)!.Length).Contains(':'))
            throw new ArgumentException("Alternate streams are forbidden.");
        for (string? p = full; p != null; p = System.IO.Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) &&
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Links/reparse points are forbidden.");
        if (mustExist && (!File.Exists(full) || Directory.Exists(full)))
            throw new FileNotFoundException("A regular image or snapshot file is required.", full);
        return full;
    }

    public static IDiskSource Open(ImageSpec spec, bool writable = false)
    {
        string path = CheckedPath(spec.Path);
        if (spec.PartitionSector < 0) throw new ArgumentOutOfRangeException(nameof(spec));
        var raw = new ImageDiskSource(path, writable);
        try { return WrapCrypto(raw, spec); }
        catch { raw.Dispose(); throw; }
    }

    internal static IDiskSource WrapCrypto(IDiskSource raw, ImageSpec spec)
    {
        ValidateKeyKind(spec);
        if (spec.Crypto == "plain") return raw;
        byte[] key = ReadKey(spec);
        try
        {
                return spec.Crypto switch
                {
                    "cbc192" when key.Length == 24 => new DecryptedDiskSourceCbc(raw, key, false),
                    "cbc192-bswap16" when key.Length == 24 => new DecryptedDiskSourceCbc(raw, key, true),
                    "xts128" when key.Length == 32 => new DecryptedDiskSource(raw, key[..16], key[16..], true),
                    _ => throw new ArgumentException("Use plain, cbc192 or cbc192-bswap16 (24-byte key), or xts128 (32-byte key).")
                };
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static void ValidateKeyKind(ImageSpec spec)
    {
        if(spec.KeyKind is not (null or "derived" or "eid-root")) throw new ArgumentException("KeyKind must be derived or eid-root.");
        if(spec.KeyKind=="eid-root" && spec.Crypto is not ("cbc192" or "cbc192-bswap16" or "xts128"))
            throw new ArgumentException("EID root keys require an explicit supported encrypted mode.");
    }
    // No key autodetection, export, fingerprint or key bytes in reports/journals.
    internal static byte[] ReadKey(ImageSpec spec)
    {
        ValidateKeyKind(spec);
        if(spec.KeyFile==null) throw new ArgumentException("Encrypted images require a key file.");
        int expected=spec.KeyKind=="eid-root"?48:spec.Crypto switch{
            "cbc192" or "cbc192-bswap16"=>24,"xts128"=>32,_=>throw new ArgumentException("Invalid encrypted mode.")};
        byte[] input=new byte[expected];
        Ps3DerivedKeys? keys=null;
        try
        {
            using(var file=new FileStream(CheckedPath(spec.KeyFile),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(file.Length!=expected)throw new ArgumentException($"Selected key kind/mode requires exactly {expected} bytes.");
                file.ReadExactly(input);
            }
            if(spec.KeyKind!="eid-root")return input.ToArray();
            keys=spec.Crypto=="xts128"?Ps3KeyDerivation.DeriveKeys(input):Ps3KeyDerivation.DeriveKeysFatNand(input);
            if(spec.Crypto!="xts128")return keys.AtaDataKey.ToArray();
            byte[] result=new byte[32];keys.AtaDataKey.CopyTo(result,0);keys.AtaTweakKey.CopyTo(result,16);return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            if(keys!=null)foreach(byte[] key in new[]{keys.AtaDataKey,keys.AtaTweakKey,keys.EncdecDataKey,keys.EncdecTweakKey})CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void Flush(IDiskSource disk)
    {
        if (disk is ImageDiskSource image) image.FlushToDisk();
        else if (disk is DecryptedDiskSourceCbc cbc) Flush(cbc.RawSource);
        else if (disk is DecryptedDiskSource xts) Flush(xts.RawSource);
        else if (disk is IDurableDisk durable) durable.Flush();
        else throw new NotSupportedException("Migration supports regular image files only.");
    }

    internal static void Write(IDiskSource disk, long offset, byte[] data)
    {
        if (!disk.CanWrite) throw new InvalidOperationException("Image opened read-only.");
        if (offset < 0 || offset > disk.TotalSize - data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset % 512 == 0 && data.Length % 512 == 0) { disk.WriteBytes(offset, data); return; }
        long first = offset / 512 * 512;
        int length = checked((int)((offset - first + data.Length + 511) / 512 * 512));
        byte[] sector = disk.ReadBytes(first, length);
        data.CopyTo(sector, checked((int)(offset - first))); disk.WriteBytes(first, sector);
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(CheckedPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
