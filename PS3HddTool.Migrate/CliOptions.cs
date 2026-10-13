using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PS3HddTool.Core.Migration;

namespace PS3HddTool.Migrate;

internal sealed class CliUsageException(string message) : Exception(message);

internal sealed class CliOptions
{
    internal const string Usage = """
    PS3HddTool.Migrate - offline same-console UFS migration
    Commands (legacy positional input is also accepted):
      direct-plan --request FILE       Read-only capacity/metadata-undo plan
      direct-run --request FILE        Copy, verify, commit; automatic rollback on failure
      direct-verify --journal DIR      Read-only completed-session verification
      direct-resume-verification --journal DIR  Restart paused destination checks and commit
      direct-status --journal DIR      Read journal snapshot only; opens no disk or key
      direct-recover --journal DIR     Restore unfinished session; needs destination + journal
      target-plan --request FILE       Read-only allocated target archive size/qualification
      target-preserve --request FILE   Archive target read-only, verify before publication
      target-verify --preservation DIR Verify archive without opening either endpoint
      target-export --preservation DIR --output FILE  Export verified sparse raw image
      prepare --request FILE          Build verified image package (full image space needed)
      verify --package DIR            Read-only package verification
      cancel --cancel-file FILE       Create a new cancellation marker

    Instead of --request, direct-plan/direct-run/prepare accept:
      --source PATH --destination PATH --source-sector N --destination-sector N
      --journal DIR (direct) | --output DIR (prepare)
      [--crypto plain|cbc192|cbc192-bswap16|xts128] [--key-file FILE] [--key-kind derived|eid-root]
      [--source-registry FILE] [--target-registry FILE] [--max-journal-bytes N]
      [--source-kind image|windows-device] [--destination-kind image|windows-device]
      [--source-serial SERIAL] [--destination-serial SERIAL]
      [--source-recovery none|detached-directory]  Explicit lossless recovery view
      [--target-preservation DIR] [--target-recovery none|detached-directory]
      [--source-hash-cache FILE --source-hash-cache-sha256 HEX]  Image source only
    Sectors are explicit decimal 512-byte sector offsets, not byte offsets.
    Default endpoints are regular images. Device endpoints require exact paths and serials.
    Prepare only supports images. A request file cannot be combined with inline fields.

    Reporting/control (all operation commands):
      --json                       NDJSON events on stdout; flush every line
      --event-log FILE              New NDJSON file, readable while running; never overwritten
      --heartbeat-seconds N         Default 5; range 0.1..60
      --progress-interval-ms N       Default 500; range 0..60000
      --cancel-file FILE            Poll for marker; keep outside inputs/journal/package
    Ctrl+C/marker rolls back copying; durable destination verification pauses without rollback.
    Resume restarts destination hashing using pinned source hashes. Recovery runs to completion
    and defers Ctrl+C; --cancel-file is forbidden for recovery/status.
    Exit: 0 success, 1 failure, 2 usage/request JSON, 4 recovery required/unknown,
          5 integrity failure, 6 reporting failure, 130 cancelled safely, 131 verification paused.
    See CLI.txt for event schema, phase-local totals and recovery instructions.
    --help | COMMAND --help | --version
    """;
    internal string Command = "";
    internal bool Help, Json;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private string? _positional;
    internal string? RequestPath => Get("request") ?? (Command is "prepare" or "direct-plan" or "direct-run" or "target-plan" or "target-preserve" ? _positional : null);
    internal double Heartbeat => Number("heartbeat-seconds", 5, .1, 60);
    internal int Interval => (int)Number("progress-interval-ms", 500, 0, 60000, integer: true);
    internal string? Get(string key) => _values.GetValueOrDefault(key);
    internal string Required(string key) => Get(key) ?? throw new CliUsageException("Missing --" + key + ".");
    internal string Input(string key) => Get(key) ?? _positional ?? throw new CliUsageException("Missing --" + key + ".");
    private double Number(string key, double fallback, double min, double max, bool integer = false)
    {
        if (Get(key) is not string text) return fallback;
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n) ||
            !double.IsFinite(n) || n < min || n > max || (integer && n != Math.Truncate(n)))
            throw new CliUsageException($"--{key} must be {(integer ? "an integer" : "a number")} from {min} to {max}.");
        return n;
    }
    private long Integer(string key, long? fallback = null)
    {
        string? value = Get(key);
        if (value == null && fallback != null) return fallback.Value;
        if (value == null || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
            throw new CliUsageException("--" + key + " requires a nonnegative decimal integer.");
        return n;
    }
    internal static CliOptions Parse(string[] args)
    {
        var o = new CliOptions { Json = args.Contains("--json") };
        if (args.Length == 0 || args.Contains("--help") || args[0] == "-h") { o.Help = true; return o; }
        o.Command = args[0] == "--version" ? "version" : args[0];
        string[] common = ["event-log", "heartbeat-seconds", "progress-interval-ms", "cancel-file"];
        string[] inline = ["source", "destination", "source-sector", "destination-sector", "crypto", "key-file", "key-kind", "source-registry", "target-registry",
            "source-kind", "destination-kind", "source-serial", "destination-serial", "source-recovery", "target-recovery", "target-preservation", "source-hash-cache", "source-hash-cache-sha256", "max-journal-bytes", "output", "journal"];
        if (o.Command is not ("direct-plan" or "direct-run" or "direct-verify" or "direct-resume-verification" or "direct-status" or "direct-recover" or "target-plan" or "target-preserve" or "target-verify" or "target-export" or "prepare" or "verify" or "cancel" or "version"))
            throw new CliUsageException("Unknown command. Use --help.");
        var allowed = new HashSet<string>(o.Command is "cancel" ? ["cancel-file"] : o.Command == "version" ? [] : common);
        if (o.Command is "prepare" or "direct-plan" or "direct-run" or "target-plan" or "target-preserve")
        {
            allowed.UnionWith(inline); allowed.Add("request");
            if (o.Command == "prepare") allowed.ExceptWith(["journal", "max-journal-bytes", "source-kind", "destination-kind", "source-serial", "destination-serial", "target-recovery", "target-preservation", "source-hash-cache", "source-hash-cache-sha256"]);
            else allowed.Remove("output");
        }
        else if (o.Command is "target-verify" or "target-export")
        {allowed.Add("preservation");if(o.Command=="target-export")allowed.Add("output");}
        else if (o.Command.StartsWith("direct-")) allowed.Add("journal");
        else if (o.Command == "verify") allowed.Add("package");
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--json") { if (o._values.ContainsKey("json")) throw new CliUsageException("Duplicate --json."); o._values.Add("json", "true"); continue; }
            if (!a.StartsWith("--"))
            {
                if (o._positional != null || o.Command is "cancel" or "version") throw new CliUsageException("Unexpected positional argument.");
                o._positional = a; continue;
            }
            string key = a[2..];
            if (!allowed.Contains(key)) throw new CliUsageException("Unknown/unsupported option " + a + ".");
            if (++i >= args.Length || args[i].StartsWith("--")) throw new CliUsageException("Missing value for " + a + ".");
            if (!o._values.TryAdd(key, args[i])) throw new CliUsageException("Duplicate " + a + ".");
        }
        if (o._positional != null && (o.Get("request") != null || o.Get("package") != null || o.Get("preservation")!=null || (o.Command is not ("direct-plan" or "direct-run" or "target-plan" or "target-preserve") && o.Get("journal") != null)))
            throw new CliUsageException("Specify the input once, either positional or named.");
        if (o.Command is "direct-recover" or "direct-status" && o.Get("cancel-file") != null)
            throw new CliUsageException("--cancel-file is not supported by recovery/status.");
        if (o.RequestPath != null && inline.Any(k => o.Get(k) != null)) throw new CliUsageException("Request file and inline request fields cannot be combined.");
        _ = o.Heartbeat; _ = o.Interval;
        return o;
    }
    internal DirectRequest DirectRequest()
    {
        if (RequestPath != null) return Read<DirectRequest>(RequestPath);
        string crypto = Get("crypto") ?? "plain";
        if (crypto is not ("plain" or "cbc192" or "cbc192-bswap16" or "xts128")) throw new CliUsageException("Invalid --crypto.");
        return new(new(Required("source"), Integer("source-sector"), crypto, Get("key-file"), Get("source-kind") ?? "image", Get("source-serial"),KeyKind()),
            new(Required("destination"), Integer("destination-sector"), crypto, Get("key-file"), Get("destination-kind") ?? "image", Get("destination-serial"),KeyKind()),
            Required("journal"), Get("source-registry"), Get("target-registry"), Integer("max-journal-bytes", 1073741824), RecoveryMode(),Get("target-preservation"),RecoveryMode("target-recovery"),Get("source-hash-cache"),Get("source-hash-cache-sha256"));
    }
    internal MigrationRequest ImageRequest()
    {
        if (RequestPath != null) return Read<MigrationRequest>(RequestPath);
        string crypto = Get("crypto") ?? "plain";
        if (crypto is not ("plain" or "cbc192" or "cbc192-bswap16" or "xts128")) throw new CliUsageException("Invalid --crypto.");
        return new(new(Required("source"), Integer("source-sector"), crypto, Get("key-file"),KeyKind()),
            new(Required("destination"), Integer("destination-sector"), crypto, Get("key-file"),KeyKind()),
            Required("output"), Get("source-registry"), Get("target-registry"), RecoveryMode());
    }
    private string? KeyKind()
    {
        string? mode=Get("key-kind");
        if(mode is not(null or "derived" or "eid-root"))throw new CliUsageException("Invalid --key-kind.");
        return mode;
    }
    private string RecoveryMode(string field="source-recovery")
    {
        string mode = Get(field) ?? "none";
        if (mode is not ("none" or "detached-directory")) throw new CliUsageException("Invalid --"+field+".");
        return mode;
    }
    private static T Read<T>(string path)
    {
        byte[] bytes = File.ReadAllBytes(RegularImage.CheckedPath(path));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        CheckObject(root);
        Endpoint("Source");
        Endpoint(typeof(T) == typeof(MigrationRequest) ? "DestinationTemplate" : "Destination");
        Text(root, typeof(T) == typeof(MigrationRequest) ? "OutputDirectory" : "JournalDirectory");
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(ImageMigrator.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new CliUsageException("Empty request.");

        void Endpoint(string name)
        {
            var endpoint = Field(root, name); CheckObject(endpoint); Text(endpoint, "Path");
            var sector = Field(endpoint, "PartitionSector");
            if (sector.ValueKind != JsonValueKind.Number || !sector.TryGetInt64(out long n) || n < 0)
                throw new CliUsageException(name + ".PartitionSector requires an explicit nonnegative integer.");
        }
        static JsonElement Field(JsonElement value, string name)
        {
            foreach (var p in value.EnumerateObject()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            throw new CliUsageException("Missing request field " + name + ".");
        }
        static void Text(JsonElement value, string name)
        {
            var field = Field(value, name);
            if (field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString())) throw new CliUsageException(name + " must be a nonempty string.");
        }
        static void CheckObject(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new CliUsageException("Request/endpoint must be a JSON object.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new CliUsageException("Duplicate request field " + p.Name + ".");
                if (p.Value.ValueKind == JsonValueKind.Object) CheckObject(p.Value);
            }
        }
    }
}
