using System.Reflection;
using HarmonyLib;
using System.Text;
using Komet.Core;
using Newtonsoft.Json;

namespace Komet.Shapes;

// Creature items and wearables' full-body meshes parse whole shape files on first draw only to tesselate elements
// (812 MB and a 2 s frame per creative row). These builders get shapes with the root's "animations" cut before
// Newtonsoft, and no clone of the wearer's animations.
internal static class InventoryShapes
{
    private const int MaxInstructions = 4096, MaxChars = 1 << 26;
    private const string Creature = "Vintagestory.GameContent.ItemCreature";
    private const string Wearable = "Vintagestory.GameContent.CollectibleBehaviorWearableAttachment";

    public static bool Enabled { get; set; } = true;
    public static bool Creatures { get; private set; } // the creature item's builder rewritten
    public static bool Wearables { get; private set; }

    [ThreadStatic] private static bool _cloned; // genFullBodyMesh works on a clone of the wearer's shape, not the shape itself

    public static void Install(Harmony harmony)
    {
        (Creatures, Wearables) = (false, false);
        if (!NotNull(harmony)) return;
        if (Method(Creature, "CreateOverlaidMeshRef") is { } creature)
            _ = NotNull(harmony.Patch(creature, transpiler: new HarmonyMethod(RewriteCreature)));
        if (Method(Wearable, "genFullBodyMesh") is { } wearable)
            _ = NotNull(harmony.Patch(wearable, new HarmonyMethod(Entering), transpiler: new HarmonyMethod(RewriteWearable)));
    }

    internal static MethodInfo? Method(string type, string name) =>
        NotNull(type) && AccessTools.TypeByName(type) is { } found && Assert(name.Length > 0) ? AccessTools.DeclaredMethod(found, name) : null;

    // Both IAsset.ToObject<Shape> calls: the creature's shape and each of its overlays
    internal static List<CodeInstruction> RewriteCreature(IEnumerable<CodeInstruction> instructions)
    {
        var lean = AccessTools.Method(typeof(InventoryShapes), nameof(Lean));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(lean)) return code;
        var done = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].operand is MethodInfo { Name: "ToObject", IsGenericMethod: true } m && m.GetGenericArguments()[0] == typeof(Shape) &&
                Il.Substitute(code, i, lean))
                done++;
        Creatures = Assert(done <= 2) && done == 2;
        return code;
    }

    // Every Shape.TryGet (inventory, attached and overlay shapes) and the clone of the wearer's animations
    internal static List<CodeInstruction> RewriteWearable(IEnumerable<CodeInstruction> instructions)
    {
        var (tryGet, none) = (AccessTools.Method(typeof(InventoryShapes), nameof(TryGet)),
            AccessTools.Method(typeof(InventoryShapes), nameof(NoAnimations)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(tryGet) || !NotNull(none)) return code;
        var (shapes, clones) = (0, 0);
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].operand is MethodInfo { Name: "TryGet" } get && get.DeclaringType == typeof(Shape) &&
                get.GetParameters() is [_, { ParameterType: var p }] && p == typeof(AssetLocation) && Il.Substitute(code, i, tryGet))
                shapes++;
            else if (code[i].operand is MethodInfo { Name: nameof(Shape.CloneAnimations) } && Il.Substitute(code, i, none))
                clones++;
        Wearables = Assert(clones <= 1) && shapes >= 2 && clones == 1;
        return code;
    }

    private static void Entering(bool ___attachableToEntity)
    {
        _cloned = ___attachableToEntity;
        _ = Assert(MaxInstructions > 0) && Assert(MaxChars > 0);
    }

    // IAsset.ToObject<Shape> minus animations, from a pooled buffer: no whole-file string on the LOH
    internal static Shape? Lean(IAsset asset, JsonSerializerSettings? settings)
    {
        if (!Enabled || !NotNull(asset) || asset.Data is not { Length: > 0 } data || Utf16(data)) return asset.ToObject<Shape>(settings);
        var bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        var chars = System.Buffers.ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(data.Length - bom));
        try
        {
            var length = Encoding.UTF8.GetChars(data, bom, data.Length - bom, chars, 0);
            var skip = Animations(chars.AsSpan(0, length), out var from, out var to) ? (from, to) : (length, length);
            var serializer = JsonUtil.CreateSerializerForDomain(asset.Location.Domain, settings);
            serializer.CheckAdditionalContent = true; // as JsonConvert.DeserializeObject has it
            using var reader = new JsonTextReader(new Skipping(chars, length, skip));
            return serializer.Deserialize<Shape>(reader);
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or InvalidCastException)
        {
            throw new JsonReaderException("Failed deserializing " + asset.Name + ": " + e.Message);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(chars);
        }
    }

    // A UTF-16 byte order mark: left to the engine's own reader, which detects it
    private static bool Utf16(byte[] data) => NotNull(data) && Assert(data.Length <= MaxChars * 4) && data.Length >= 2 && data[0] + data[1] == 0xFF + 0xFE && data[0] is 0xFF or 0xFE;

    // The buffer's text with [Skip.From, Skip.To) read as "null"
    private sealed class Skipping(char[] chars, int length, (int From, int To) skip) : TextReader
    {
        private const string Nothing = "null";
        private int _at, _null;

        public override int Read(char[] buffer, int index, int count)
        {
            if (!NotNull(buffer) || !Assert(index >= 0 && count >= 0 && index + count <= buffer.Length)) return 0;
            var written = 0;
            for (var step = 0; step < 4 && written < count; step++) written += Next(buffer.AsSpan(index + written, count - written));
            return written;
        }

        public override int Read() => Assert(_one.Length == 1) && Read(_one, 0, 1) == 1 ? _one[0] : -1;

        public override int Peek()
        {
            if (!Assert(_null <= Nothing.Length) || !Assert(_at <= length || _at == skip.To)) return -1;
            if (_at == skip.From && _null < Nothing.Length) return Nothing[_null];
            if (_at == skip.From) _at = skip.To;
            return _at < length && Assert(_at >= 0) ? chars[_at] : -1;
        }

        private readonly char[] _one = new char[1];

        // One run: the text up to the skipped range, the null in its place, or the text after it
        private int Next(Span<char> into)
        {
            if (!Assert(into.Length > 0) || _at >= length) return 0;
            if (_at == skip.From && _null < Nothing.Length)
            {
                var n = Math.Min(Nothing.Length - _null, into.Length);
                Nothing.AsSpan(_null, n).CopyTo(into);
                _null += n;
                if (_null == Nothing.Length) _at = skip.To;
                return n;
            }

            var end = _at < skip.From ? skip.From : length;
            var count = Math.Min(end - _at, into.Length);
            chars.AsSpan(_at, count).CopyTo(into);
            _at += count;
            return count;
        }
    }

    // Shape.TryGet; lean only for the wearer's clone (else step parenting merges animations into the wearer's shape)
    internal static Shape? TryGet(ICoreAPI api, AssetLocation path)
    {
        if (!Enabled || !_cloned || !NotNull(api)) return Shape.TryGet(api, path);
        ShapeElement.locationForLogging = path;
        try
        {
            return api.Assets.TryGet(path) is { } asset ? Lean(asset, null) : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or InvalidCastException)
        {
            api.World.Logger.Error("Exception thrown when trying to load shape file {0}\n{1}", path, e.Message);
            return null;
        }
    }

    internal static Animation[]? NoAnimations(Shape shape) => Enabled || !NotNull(shape) || !Assert(MaxChars > 0) ? null : shape.CloneAnimations();

    // Where the root's "animations" value lies; strings, escapes and comments skipped as Newtonsoft does
    internal static bool Animations(ReadOnlySpan<char> json, out int from, out int to)
    {
        (from, to) = (0, 0);
        if (!Assert(json.Length <= MaxChars) || !Assert(MaxChars > 0)) return false;
        var (depth, i) = (0, 0);
        for (var steps = 0; steps < MaxChars && i < json.Length; steps++)
        {
            var c = json[i];
            if (c is '"' or '\'' && depth == 1 && Key(json, i, out var value)) return Value(json, value, out from, out to);
            if (c is '"' or '\'' or '/') i = Skipped(json, i);
            else
            {
                depth += Nesting(c);
                i++;
            }
        }

        return false;
    }

    // A key "animations" (any case) at json[at]: where its value starts
    private static bool Key(ReadOnlySpan<char> json, int at, out int value)
    {
        var end = Skipped(json, at);
        value = end;
        if (!Index(at, json.Length)) return false;
        if (end - at != "\"animations\"".Length || !json.Slice(at + 1, 10).Equals("animations", StringComparison.OrdinalIgnoreCase))
            return false;
        value = Blank(json, end);
        if (value >= json.Length || json[value] != ':') return false;
        value = Blank(json, value + 1);
        return Assert(value <= json.Length);
    }

    private static int Blank(ReadOnlySpan<char> json, int at)
    {
        for (var i = 0; i < Math.Min(json.Length - at, 256) && at < json.Length; i++)
            if (char.IsWhiteSpace(json[at])) at++;
            else break;
        return Assert(at >= 0) ? at : 0;
    }

    // The array or object starting at start, to its end; false for a literal (null already, or nothing worth cutting)
    private static bool Value(ReadOnlySpan<char> json, int start, out int from, out int to)
    {
        (from, to) = (start, start);
        if (!Index(start, json.Length) || json[start] is not ('[' or '{')) return false;
        var (depth, i) = (0, start);
        for (var steps = 0; steps < MaxChars && i < json.Length; steps++)
        {
            var c = json[i];
            if (c is '"' or '\'' or '/')
            {
                i = Skipped(json, i);
                continue;
            }

            depth += Nesting(c);
            i++;
            if (depth != 0) continue;
            to = i;
            return Assert(to > from);
        }

        return false;
    }

    // Past a string (quoted either way, escapes honoured) or a comment at json[at]; a lone slash is one character
    private static int Skipped(ReadOnlySpan<char> json, int at)
    {
        if (!Index(at, json.Length)) return json.Length;
        var c = json[at];
        if (c == '/' && at + 1 < json.Length && json[at + 1] is '/' or '*')
        {
            var block = json[at + 1] == '*';
            var rest = json[(at + 2)..];
            var end = block ? rest.IndexOf("*/", StringComparison.Ordinal) : rest.IndexOf('\n');
            var after = block ? 2 : 1;
            return end < 0 ? json.Length : at + 2 + end + after;
        }

        if (c is not ('"' or '\'')) return at + 1;
        var escaped = false;
        for (var i = at + 1; i < Math.Min(json.Length, MaxChars); i++)
        {
            if (escaped) escaped = false;
            else if (json[i] == '\\') escaped = true;
            else if (json[i] == c) return i + 1;
        }

        return Assert(json.Length <= MaxChars) ? json.Length : 0;
    }

    private static int Nesting(char c) => !Assert(c != '\uFFFF') ? 0 : c switch
    {
        '{' or '[' => 1,
        '}' or ']' => -1,
        _ => 0
    };
}
