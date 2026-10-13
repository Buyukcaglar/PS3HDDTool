using System.Security.Cryptography;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.Migration;

static class NativeCbcTests
{
    internal static void Run(Action<string, Action<string>> test)
    {
        test("native-cbc-independent-ciphertext", dir => {
            byte[] key = Fixture.Payload(24, 745), plain = Fixture.Payload(512 * 300, 746);
            byte[] expected = new byte[plain.Length];
            // Independent .NET AES oracle: reset IV each sector, then swap ciphertext pairs.
            using var aes = Aes.Create(); aes.Key = key;
            for (int p = 0; p < plain.Length; p += 512)
                aes.EncryptCbc(plain.AsSpan(p, 512), new byte[16], PaddingMode.None).CopyTo(expected, p);
            for (int i = 0; i < expected.Length; i += 2) (expected[i], expected[i + 1]) = (expected[i + 1], expected[i]);
            string image = Path.Combine(dir, "native.img"), keyFile = Path.Combine(dir, "generated.key");
            File.WriteAllBytes(image, new byte[plain.Length + 512]); File.WriteAllBytes(keyFile, key);
            var spec = new ImageSpec(image, 0, "cbc192-bswap16", keyFile);
            using (var disk = RegularImage.Open(spec, true))
            {
                disk.WriteBytes(512, plain); RegularImage.Flush(disk);
                Oracle.Equal(plain, disk.ReadSectors(1, 300), "native multi-chunk read");
                Oracle.Equal(plain.AsSpan(311, 845).ToArray(), disk.ReadBytes(823, 845), "native unaligned read");
                var pipeline = (IPipelinedDiskSource)disk; byte[] encoded = new byte[plain.Length + 512];
                Array.Fill(encoded, (byte)0xA5);
                pipeline.EncryptForWrite(plain, plain.Length - 512, 512, encoded);
                Oracle.Equal(expected.AsSpan(0, plain.Length - 512).ToArray(), encoded.AsSpan(0, plain.Length - 512).ToArray(), "native partial pipeline");
                if (encoded.AsSpan(plain.Length - 512).ContainsAnyExcept((byte)0xA5)) throw new Exception("Pipeline overwrote output suffix.");
                pipeline.EncryptForWrite(plain, plain.Length, 512, encoded);
                Oracle.Equal(expected, encoded.AsSpan(0, plain.Length).ToArray(), "native full pipeline");
            }
            Oracle.Equal(expected, File.ReadAllBytes(image).AsSpan(512).ToArray(), "native raw ciphertext");
            using var wrong = RegularImage.Open(spec with { Crypto = "cbc192" });
            if (wrong.ReadBytes(512, 512).AsSpan().SequenceEqual(plain.AsSpan(0, 512))) throw new Exception("Modes unexpectedly equivalent.");
        });
    }
}
