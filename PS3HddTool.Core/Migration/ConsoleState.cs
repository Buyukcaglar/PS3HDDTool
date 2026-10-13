using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace PS3HddTool.Core.Migration;

public sealed record UserIdentity(string UserNumber, string AccountId);

public static class ConsoleState
{
    public static IReadOnlyList<UserIdentity> ReadUsers(byte[] registry)
    {
        if (registry.Length != 0x40000 || !registry.AsSpan(0, 4).SequenceEqual(new byte[] { 0xBC, 0xAD, 0xAD, 0xBC }))
            throw new InvalidDataException("Expected a complete 256 KiB xRegistry.sys snapshot.");
        string keys = Encoding.Latin1.GetString(registry, 0, 0x10000);
        var users = new SortedDictionary<string, UserIdentity>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(keys, @"/setting/user/(\d{8})/npaccount/accountid\x00"))
        {
            int reference = match.Index - 0x15;
            if (reference < 0 || reference > ushort.MaxValue) throw new InvalidDataException("Invalid registry reference.");
            var values = new List<string>();
            for (int v = 0x10002; v <= 0x1FFE7; v++)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(registry.AsSpan(v)) != reference ||
                    registry[v + 4] != 0 || registry[v + 5] != 0x11 || registry[v + 6] != 2) continue;
                byte[] value = registry.AsSpan(v + 7, 16).ToArray();
                if (value.All(b => b == 0)) values.Add(new string('0', 16));
                else
                {
                    string account = Encoding.ASCII.GetString(value);
                    if (!Regex.IsMatch(account, "\\A[0-9a-fA-F]{16}\\z"))
                        throw new InvalidDataException("Malformed registry account identity.");
                    values.Add(account.ToUpperInvariant());
                }
            }
            if (values.Count != 1 || !users.TryAdd(match.Groups[1].Value, new(match.Groups[1].Value, values[0])))
                throw new InvalidDataException("Missing/ambiguous registry user identity.");
        }
        return users.Values.ToArray();
    }
}
