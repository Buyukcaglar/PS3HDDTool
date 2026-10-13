using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PS3HddTool.Core.Migration;

static class PopulatedStressTests
{
    internal const int Accounts = 16, SavesPerAccount = 128, TrophiesPerAccount = 96, Games = 96, AssetsPerGame = 256;
    internal static Fixture Population(bool bulk)
    {
        var f = new Fixture(3726, metadataOnly: true);
        int seed = 17000;
        Fixture.Item File(string path, int size)
        {
            var n = f.Add(path, payload: Fixture.Payload(size, ++seed));
            Fixture.W32(n.Raw, 4, 1000 + seed % Accounts); Fixture.W32(n.Raw, 8, 1000 + seed % 7);
            Fixture.W64(n.Raw, 0x20, 1600000000L + seed); return n;
        }
        f.Add("home", 4);
        for (int u = 1; u <= Accounts; u++)
        {
            string home = $"home/{u:D8}"; f.Add(home, 4); f.Add(home + "/savedata", 4);
            for (int s = 0; s < SavesPerAccount; s++)
            {
                string save = $"{home}/savedata/TEST{s:D5}-SLOT{s % 8:D2}"; f.Add(save, 4);
                File(save + "/PARAM.SFO", 1024); File(save + "/PARAM.PFD", 4096);
                File(save + "/ICON0.PNG", 2048 + s % 1024); File(save + "/SAVE.DAT", 8192 + s * 257 % 65536);
            }
            f.Add(home + "/trophy", 4);
            for (int t = 0; t < TrophiesPerAccount; t++)
            {
                string trophy = $"{home}/trophy/NPWR{t:D5}_00"; f.Add(trophy, 4);
                File(trophy + "/TROPUSR.DAT", 8192 + t * 113); File(trophy + "/TROPTRNS.DAT", 4096 + t * 31);
                File(trophy + "/TROPCONF.SFM", 1024 + t);
            }
            f.Add(home + "/exdata", 4); File(home + "/exdata/act.dat", 4152);
            for (int g = 0; g < Games; g++) File($"{home}/exdata/TEST{g:D5}.rif", 152);
        }
        byte[] zero = new byte[SparseFixture.Block];
        f.Add("game", 4);
        for (int g = 0; g < Games; g++)
        {
            string game = $"game/TEST{g:D5}"; f.Add(game, 4); f.Add(game + "/USRDIR", 4);
            File(game + "/PARAM.SFO", 1024); File(game + "/ICON0.PNG", 4096);
            var executable = File(game + "/USRDIR/EBOOT.BIN", 196608 + g * 13);
            executable.Ea = Fixture.Payload(g % 2 == 0 ? 17000 : 317, ++seed);
            for (int folder = 0; folder < 8; folder++)
            {
                string folderPath = $"{game}/USRDIR/assets-{folder}"; f.Add(folderPath, 4);
                for (int a = 0; a < AssetsPerGame / 8; a++)
                    File($"{folderPath}/asset-{a:D4}.bin", 1 + (g * 991 + folder * 83 + a * 137) % 8192);
            }
            int blocks = (bulk ? 80 * 1024 * 1024 : 1024 * 1024) / SparseFixture.Block;
            var archive = f.Add(game + "/USRDIR/data.psarc", size: (long)blocks * SparseFixture.Block + 123);
            for (int b = 0; b <= blocks; b++)
            {
                // Every UFS block allocated, including zero blocks. Tags throughout
                // each archive detect shifted/wrong-game/omitted data and bad tails.
                if (!bulk || b % 256 == 0 || b == blocks)
                {
                    byte[] tag = new byte[b == blocks ? 123 : SparseFixture.Block];
                    Encoding.ASCII.GetBytes($"GENERATED-GAME-{g:D5}-BLOCK-{b:D8}").CopyTo(tag, 0);
                    BinaryPrimitives.WriteInt64BigEndian(tag.AsSpan(tag.Length - 8), ((long)g << 32) | (uint)b);
                    archive.Data[b] = tag;
                }
                else archive.Data[b] = zero;
            }
            f.Link(game, "executable-hardlink", executable);
            f.Add(game + "/current-assets", 10, payload: Encoding.UTF8.GetBytes("USRDIR/assets-0"), inline: true);
            File(game + "/USRDIR/empty.bin", 0);
        }
        f.Add("vsh", 4); f.Add("vsh/task", 4); File("vsh/task/database.bin", 8 * 1024 * 1024);
        f.Add("特殊文字", 4); File("特殊文字/Case", 17); File("特殊文字/case", 31); File("特殊文字/CON", 23);
        // Cross the 32-bit size boundary without storing 4 GiB of payload. Hashes
        // still process its complete logical length, independently of used blocks.
        var sparse = f.Add("vsh/task/sparse-over-4GiB.bin", size: (1L << 32) + 123);
        sparse.Data[0] = Fixture.Payload(SparseFixture.Block, ++seed);
        sparse.Data[(sparse.Size - 1) / SparseFixture.Block] = Fixture.Payload(123, ++seed);
        f.FinishMetadata(); return f;
    }
    internal static void Run(string dir, string crypto)
    {
        bool bulk = crypto == "plain";
        var population = Population(bulk);
        byte[] registry = Fixture.Registry(Enumerable.Range(1, Accounts).Select(u => ($"{u:D8}", $"{u:X16}")).ToArray());
        Console.WriteLine($"Populated stress: {population.Items.Count} inodes; {Accounts} accounts; {Accounts * SavesPerAccount} saves; {Accounts * TrophiesPerAccount} trophy sets; {Games} games");
        LargeSparseTests.Run(dir, population, registry, crypto, compactZeros: bulk, populated: true);
        // Retain reports and undo evidence, release only exact generated image
        // files after every audit and recovery assertion has passed.
        foreach (string name in new[] { "source-1TB.img", "target-2TB.img" }) File.Delete(Path.Combine(dir, name));
        File.WriteAllText(Path.Combine(dir, "images-released.txt"), "Both generated images deleted after successful verification; journals/reports retained.\n");
        GC.Collect(); GC.WaitForPendingFinalizers();
    }
}
