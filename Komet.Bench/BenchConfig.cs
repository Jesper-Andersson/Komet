using System.Globalization;
using System.Text.Json;
using Komet.Core;

namespace Komet.Bench;

// Key: a knob of Features' table or another mod's (modid:name), resolved by BenchDriver once the world is up
internal readonly record struct BenchSetting(string Key, int Value);

// Engine: knobs start at Knobs.Snapshot(engine: true), not the player's; Settings apply on top either way
internal readonly record struct BenchArm(string Name, bool Engine, BenchSetting[] Settings);

// bench.json as bench.sh flattens it; caps, ranges and unknown keys fail at load ("_" keys are comments)
internal sealed class BenchConfig
{
    public const int MaxBytes = 1 << 20, MaxDepth = 16, MaxArms = 8, MaxLaps = 64, MaxKeys = 64, MaxText = 512;

    // One frame of BenchDriver.MaxStep stays under the 128 blocks EntityPos.SetFromPacket accepts
    public const double MaxSpeed = 30;

    private static readonly string[] TopKeys =
    [
        "name", "output", "modDir", "revision", "env", "world", "settle", "route", "lap", "laps", "warmupLaps", "arms",
        "sandbox", "shots", "hudWindow", "gui", "joinSeconds", "captureSeconds", "landSeconds", "spawning"
    ];

    public JsonElement Raw { get; private init; }
    public string Name { get; private set; } = "bench";
    public string Output { get; private set; } = "";
    public string ModDir { get; private set; } = "";
    public string SavegameId { get; private set; } = "";

    // After the climb; the queue drains in about 54-75 s at view distance 1536
    public double Settle { get; private set; } = 150;

    public double LapSettle { get; private set; } = 10;
    public double Speed { get; private set; } = 11;
    public double Still { get; private set; } = 10;
    public double Rotate { get; private set; } = 20;
    public double Leg { get; private set; } = 40;
    public double Turn { get; private set; } = 2;
    public int Laps { get; private set; } = 8;
    public int WarmupLaps { get; private set; } = -1; // one per arm unless set
    public IReadOnlyList<BenchArm> Arms { get; private set; } = [];

    // A screenshot at the start of every measured lap: the arms side by side, same place, same hour
    public bool Shots { get; private set; }

    // Seconds in the world before the setup's commands (the hour, the weather), a screenshot at their end: the world as a
    // player joins it
    public double JoinSeconds { get; private set; }

    // A debug capture (Logs/komet-debug) of this many seconds, started as the world is up
    public double CaptureSeconds { get; private set; }

    // After the laps: down to the ground where the world was joined and this long standing there (0: no landing)
    public double LandSeconds { get; private set; }

    // route.back false: each lap flies out only, so a landing (LandSeconds) comes down where the terrain just arrived
    public bool Back { get; private set; } = true;

    // Entity spawning left on (the setup switches it off otherwise): creatures appear as they would for a player
    public bool Spawning { get; private set; }

    // "cycle": each lap shows the next HUD window tab, the last the debug window
    public string HudWindow { get; private set; } = "";
    public string Gui { get; private set; } = ""; // BenchGui.Creative: the creative inventory scrolled while standing still
    public string Frames => Path.Join(Path.GetDirectoryName(Output) ?? "", "frames.csv");
    public string Status => Output + ".status";

    public static BenchConfig Parse(string json)
    {
        if (!NotNull(json) || json.Length > MaxBytes)
            throw new InvalidDataException($"bench.json: missing or over {MaxBytes} bytes");
        var options = new JsonDocumentOptions
        { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var document = Document(json, options);
        var root = document.RootElement;
        Keys(root, "", TopKeys);
        var config = new BenchConfig { Raw = root.Clone() };
        config.Read(root);
        config.Arms = ReadArms(root);
        var arms = config.Arms.Count;
        if (config.WarmupLaps < 0) config.WarmupLaps = arms;
        if (!Assert(arms is > 0 and <= MaxArms) || config.Laps % arms != 0)
            throw Bad("laps",
                string.Create(CultureInfo.InvariantCulture, $"a multiple of the {arms} arms: whole mirrored blocks"));
        return config;
    }

    private static JsonDocument Document(string json, JsonDocumentOptions options)
    {
        if (!Assert(options.MaxDepth > 0) || !NotNull(json)) throw new InvalidDataException("bench.json: no document");
        try
        {
            return JsonDocument.Parse(json, options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("bench.json: " + e.Message, e);
        }
    }

    private void Read(JsonElement root)
    {
        if (!Assert(root.ValueKind == JsonValueKind.Object) || !Assert(Raw.ValueKind == JsonValueKind.Object)) return;
        Name = Text(root, "name", "", false) is { Length: > 0 } name ? name : Name;
        Output = FullPath(root, "output", true);
        ModDir = FullPath(root, "modDir", true);
        _ = Text(root, "revision", "", false);
        Env(root);
        SavegameId = Text(Section(root, "world", ["save", "savegameId"]), "savegameId", "world.", true);
        var settle = Section(root, "settle", ["seconds", "lapSeconds"]);
        Settle = Number(settle, "seconds", "settle.", Settle, 0, 3600);
        LapSettle = Number(settle, "lapSeconds", "settle.", LapSettle, 0, 600);
        var route = Section(root, "route", ["speed", "back"]);
        Speed = Number(route, "speed", "route.", Speed, 0.5, MaxSpeed);
        if (Get(route, "back", out var back))
            Back = back.ValueKind is JsonValueKind.True or JsonValueKind.False ? back.GetBoolean() : throw Bad("route.back", "true or false");
        if (Get(root, "spawning", out var spawning))
            Spawning = spawning.ValueKind is JsonValueKind.True or JsonValueKind.False ? spawning.GetBoolean() : throw Bad("spawning", "true or false");
        var lap = Section(root, "lap", ["still", "rotate", "leg", "turn"]);
        Still = Number(lap, "still", "lap.", Still, 0, 600);
        Rotate = Number(lap, "rotate", "lap.", Rotate, 0, 600);
        Leg = Number(lap, "leg", "lap.", Leg, 0, 600);
        Turn = Number(lap, "turn", "lap.", Turn, 0.1, 60);
        Laps = Integer(root, "laps", Laps, 1, MaxLaps);
        WarmupLaps = Integer(root, "warmupLaps", WarmupLaps, 0, MaxArms);
        HudWindow = Choice(root, "hudWindow", ["cycle"]);
        Gui = Choice(root, "gui", [BenchGui.Creative, BenchGui.Debug, BenchGui.Options, BenchGui.Help]);
        JoinSeconds = Number(root, "joinSeconds", "", JoinSeconds, 0, 300);
        CaptureSeconds = Number(root, "captureSeconds", "", CaptureSeconds, 0, 300);
        LandSeconds = Number(root, "landSeconds", "", LandSeconds, 0, 300);
        if (Get(root, "shots", out var shots))
            Shots = shots.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? shots.GetBoolean()
                : throw Bad("shots", "true or false");
    }

    private static void Env(JsonElement root)
    {
        if (!Get(root, "env", out var env)) return;
        if (env.ValueKind != JsonValueKind.Object) throw Bad("env", "an object of NAME: \"value\" strings");
        foreach (var entry in env.EnumerateObject().Bounded(MaxKeys))
            if (entry.Value.ValueKind != JsonValueKind.String) throw Bad("env." + entry.Name, "a string");
    }

    private static List<BenchArm> ReadArms(JsonElement root)
    {
        if (!Assert(root.ValueKind == JsonValueKind.Object)) return [];
        if (!Get(root, "arms", out var arms)) return [new BenchArm("A", false, [])];
        if (arms.ValueKind != JsonValueKind.Array || arms.GetArrayLength() is < 1 or > MaxArms)
            throw Bad("arms", $"a list of 1 to {MaxArms} arms");
        List<BenchArm> list = [];
        foreach (var arm in arms.EnumerateArray().Bounded(MaxArms))
        {
            var parsed = ReadArm(arm, list.Count);
            if (list.Exists(other => other.Name == parsed.Name))
                throw Bad("arms", $"unique names, {parsed.Name} twice");
            list.Add(parsed);
        }

        return list;
    }

    private static BenchArm ReadArm(JsonElement arm, int index)
    {
        if (!Index(index, MaxArms) || !Assert(Knobs.Count <= Knobs.MaxKnobs)) throw Bad("arms", "fewer arms");
        var path = string.Create(CultureInfo.InvariantCulture, $"arms[{index}].");
        if (arm.ValueKind != JsonValueKind.Object) throw Bad(path.TrimEnd('.'), "an object");
        Keys(arm, path, ["name", "engine", "set"]);
        var engine = false;
        if (Get(arm, "engine", out var flag))
            engine = flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? flag.GetBoolean() : throw Bad(path + "engine", "true or false");
        List<BenchSetting> settings = [];
        if (Get(arm, "set", out var set))
        {
            if (set.ValueKind != JsonValueKind.Object) throw Bad(path + "set", "an object of knob names");
            foreach (var entry in set.EnumerateObject().Bounded(MaxKeys))
            {
                var setting = Setting(entry, path + "set.");
                if (settings.Exists(other => other.Key == setting.Key))
                    throw Bad(path + "set." + entry.Name, "named once");
                settings.Add(setting);
            }
        }

        return new BenchArm(Text(arm, "name", path, true), engine, [.. settings]);
    }

    // Another mod's knob registers after the file is read: name checked here, range at boot
    private static BenchSetting Setting(JsonProperty entry, string path)
    {
        var (knob, external) = (Knobs.Find(entry.Name), entry.Name.Contains(':', StringComparison.Ordinal));
        if (!Assert(path.Length > 0) || (external && !Features.IsExternalId(entry.Name)))
            throw Bad(path + entry.Name, "a knob of another mod as modid:name");
        if (knob < 0 && !external) throw Bad(path + entry.Name, "one of the knob names in Komet/Core/Features/Features.cs");
        var value = entry.Value.ValueKind switch
        {
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Number when entry.Value.TryGetInt32(out var number) => number,
            _ => throw Bad(path + entry.Name, "true, false or a whole number")
        };
        return external || (Knobs.InRange(knob, value) && Index(knob, Knobs.Count))
            ? new BenchSetting(entry.Name, value) : throw Bad(path + entry.Name, "a value inside the knob's range");
    }

    private static InvalidDataException Bad(string key, string expected)
    {
        _ = Assert(key.Length > 0) && Assert(expected.Length > 0);
        return new InvalidDataException($"bench.json: {key} must be {expected}");
    }

    private static void Keys(JsonElement obj, string path, string[] allowed)
    {
        if (!Assert(allowed.Length > 0) || !NotNull(path)) return;
        if (obj.ValueKind != JsonValueKind.Object)
            throw Bad(path.Length == 0 ? "the document" : path.TrimEnd('.'), "an object");
        var unknown = obj.EnumerateObject().Bounded(MaxKeys)
            .FirstOrDefault(property => !property.Name.StartsWith('_') && Array.IndexOf(allowed, property.Name) < 0);
        if (unknown.Value.ValueKind != JsonValueKind.Undefined)
            throw new InvalidDataException($"bench.json: unknown key {path}{unknown.Name}");
    }

    // A missing section reads as empty, so its keys keep their defaults
    private static JsonElement Section(JsonElement obj, string key, string[] allowed)
    {
        if (!Assert(key.Length > 0) || !Get(obj, key, out var section)) return default;
        Keys(section, key + ".", allowed);
        return section;
    }

    private static bool Get(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        if (!Assert(key.Length > 0) ||
            !Assert(obj.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined)) return false;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value) &&
               value.ValueKind != JsonValueKind.Null;
    }

    private static double Number(JsonElement obj, string key, string path, double fallback, double min, double max)
    {
        if (!Assert(min <= max) || !Finite(fallback) || !Get(obj, key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Bad(path + key, "a number");
        return number >= min && number <= max
            ? number : throw Bad(path + key, string.Create(CultureInfo.InvariantCulture, $"between {min} and {max}"));
    }

    private static int Integer(JsonElement obj, string key, int fallback, int min, int max)
    {
        if (!Assert(min <= max) || !Assert(fallback >= min || fallback < 0)) return fallback;
        if (Get(obj, key, out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
            throw Bad(key, "a whole number");
        return (int)Number(obj, key, "", fallback, min, max);
    }

    private static string Text(JsonElement obj, string key, string path, bool required)
    {
        if (!Assert(key.Length > 0) || !NotNull(path) || !Get(obj, key, out var value))
            return required ? throw Bad(path + key, "set") : "";
        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : throw Bad(path + key, "a string");
        return text.Length is > 0 and <= MaxText
            ? text : throw Bad(path + key, $"a string of 1 to {MaxText} characters");
    }

    // Unset, or a value the driver acts on: a typo must not silently run without it
    private static string Choice(JsonElement obj, string key, string[] allowed)
    {
        var text = Text(obj, key, "", false);
        if (!Assert(allowed.Length > 0) || !Assert(text.Length <= MaxText)) return "";
        return text.Length == 0 || Array.IndexOf(allowed, text) >= 0
            ? text : throw Bad(key, "one of " + string.Join(", ", allowed));
    }

    private static string FullPath(JsonElement obj, string key, bool required)
    {
        var text = Text(obj, key, "", required);
        if (!Assert(key.Length > 0) || !Assert(text.Length <= MaxText) || text.Length == 0) return text;
        // GetFullPath throws on a NUL
        if (!Path.IsPathFullyQualified(text) || text.Contains('\0', StringComparison.Ordinal))
            throw Bad(key, "an absolute path");
        return Path.GetFullPath(text);
    }
}

internal enum BenchKind : byte
{
    Setup, // fly mode, server commands
    Climb, // to the route's start, kinematically
    Settle, // a fixed time for the world around the start to stream in
    LapSettle, // the same between laps; the lap's arm is switched on as it begins
    Still,
    Rotate,
    Out,
    Turn,
    Back,
    Land, // down to the ground where the world was joined, kinematically
    Ground // standing there
}

// Lap -1 for the segments before the first lap. Far marks the turn at the far end of the route.
internal readonly record struct BenchSegment(BenchKind Kind, int Lap, int Arm, bool Warmup, bool Far, double Seconds)
{
    private static readonly string[] Names =
        Array.ConvertAll(Enum.GetNames<BenchKind>(), kind => kind.ToLowerInvariant());

    public bool Measured => Kind >= BenchKind.Still; // Setup's Seconds is its timeout, Climb's comes from the distance
    public string Name => Far ? "turn-far" : KindName(Kind);

    public static string KindName(BenchKind kind) =>
        Index((int)kind, Names.Length) && Assert(Names.Length == (int)BenchKind.Ground + 1) ? Names[(int)kind] : "";
}

// Offset from the route's start and yaw; forward at yaw y is (sin y, 0, cos y), as CalcMovementVectors at pitch π
internal readonly record struct BenchPose(double X, double Z, double Yaw);

// The run as a flat list; laps fly out and back on one line, arms in mirrored blocks in one process, so drift cancels
internal static class BenchScenario
{
    public const int MaxSegments = 256, SegmentsPerLap = 7, Prelude = 3;

    // At y = Altitude in the player's column, heading +Z, terrain unchecked; each segment's first Discard s are not
    // counted
    public const double ModeTimeout = 10, ClimbSpeed = 40, Altitude = 200, Heading = 0, Discard = 1;

    public static BenchSegment[] Expand(BenchConfig config)
    {
        if (!NotNull(config) || !Assert(config.Arms.Count is > 0 and <= BenchConfig.MaxArms))
            throw new InvalidDataException("bench.json: no arms");
        var laps = config.WarmupLaps + config.Laps;
        if (Prelude + (long)laps * SegmentsPerLap > MaxSegments)
            throw new InvalidDataException(
                $"bench.json: warmupLaps + laps is {laps}, more than {MaxSegments} segments allow");
        List<BenchSegment> list =
        [
            new(BenchKind.Setup, -1, 0, false, false, ModeTimeout),
            new(BenchKind.Climb, -1, 0, false, false, 0),
            new(BenchKind.Settle, -1, 0, false, false, config.Settle)
        ];
        for (var lap = 0; lap < Math.Min(laps, BenchConfig.MaxLaps + BenchConfig.MaxArms); lap++)
        {
            var warmup = lap < config.WarmupLaps;
            var number = warmup ? lap : lap - config.WarmupLaps;
            var arm = warmup ? lap % config.Arms.Count : ArmOf(number, config.Arms.Count);
            AddLap(list, config, number, arm, warmup);
        }

        if (config.LandSeconds > 0 && list.Count + 2 <= MaxSegments)
        {
            list.Add(new BenchSegment(BenchKind.Land, -1, 0, false, false, 0));
            list.Add(new BenchSegment(BenchKind.Ground, -1, 0, false, false, config.LandSeconds));
        }

        return Assert(list.Count <= MaxSegments)
            ? [.. list] : throw new InvalidDataException("bench.json: too many segments");
    }

    // Mirrored blocks: lap order A B B A A B B A for two arms, A B C C B A for three
    public static int ArmOf(int lap, int arms)
    {
        if (!Assert(lap >= 0) || !Assert(arms is > 0 and <= BenchConfig.MaxArms)) return 0;
        var place = lap % arms;
        return lap / arms % 2 == 0 ? place : arms - 1 - place;
    }

    private static void AddLap(List<BenchSegment> list, BenchConfig config, int lap, int arm, bool warmup)
    {
        if (!Index(arm, config.Arms.Count) || !Assert(lap >= 0)) return;
        // Always there, even at zero length: the arm switches on entering it
        list.Add(new BenchSegment(BenchKind.LapSettle, lap, arm, warmup, false, config.LapSettle));
        if (config.Still > 0) list.Add(new BenchSegment(BenchKind.Still, lap, arm, warmup, false, config.Still));
        if (config.Rotate > 0) list.Add(new BenchSegment(BenchKind.Rotate, lap, arm, warmup, false, config.Rotate));
        if (config.Leg <= 0) return;
        list.Add(new BenchSegment(BenchKind.Out, lap, arm, warmup, false, config.Leg));
        list.Add(new BenchSegment(BenchKind.Turn, lap, arm, warmup, true, config.Turn));
        if (!config.Back) return;
        list.Add(new BenchSegment(BenchKind.Back, lap, arm, warmup, false, config.Leg));
        list.Add(new BenchSegment(BenchKind.Turn, lap, arm, warmup, false, config.Turn));
    }

    // t is clamped, so an overshooting frame lands on the next segment's start; leg is the route length in blocks.
    public static BenchPose Pose(BenchSegment segment, double t, double heading, double leg)
    {
        if (!Finite(t) || !Finite(heading) || !Assert(leg >= 0)) return new BenchPose(0, 0, 0);
        var s = segment.Seconds > 0 ? Math.Clamp(t / segment.Seconds, 0, 1) : 1;
        var (sin, cos) = Math.SinCos(heading);
        return segment.Kind switch
        {
            BenchKind.Rotate => new BenchPose(0, 0, heading + 2 * Math.PI * s),
            BenchKind.Out => new BenchPose(leg * s * sin, leg * s * cos, heading),
            BenchKind.Turn when segment.Far => new BenchPose(leg * sin, leg * cos, heading + Math.PI * s),
            BenchKind.Turn => new BenchPose(0, 0, heading + Math.PI + Math.PI * s),
            BenchKind.Back => new BenchPose(leg * (1 - s) * sin, leg * (1 - s) * cos, heading + Math.PI),
            _ => new BenchPose(0, 0, heading)
        };
    }
}
