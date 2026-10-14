using System.Globalization;
using System.Text.RegularExpressions;

namespace Komet.Vulkan;

internal static partial class GlslPort
{
    [GeneratedRegex(@"^(?<space>\s*)(?:layout\s*\((?<layout>[^)]*)\)\s*)?" +
                    @"(?<qualifiers>(?:(?:uniform|in|out|flat|smooth|noperspective|centroid|invariant|" +
                    @"highp|mediump|lowp)\s+)+)" +
                    @"(?<type>\w+)\s*(?:\[(?<size>[^\]]*)\])?\s+(?<names>[^;]+);$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"^\s*(?<name>\w+)\s*(?:\[(?<size>[^\]]*)\])?\s*(?:=(?<init>.+))?$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DeclaratorPattern();

    [GeneratedRegex(@"^(?<space>\s*)(?:layout\s*\((?<layout>[^)]*)\)\s*)?" +
                    @"(?<qualifiers>(?:(?:readonly|writeonly|restrict|coherent|volatile)\s+)*)" +
                    @"(?<kind>buffer|uniform)\s+" +
                    @"(?<name>\w+)\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockPattern();

    [GeneratedRegex(@"^\s*(?:layout\s*\([^)]*\)\s*)?(?:\w+\s+)*(?:in|out)\s+\w+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceBlockPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\d+|[+*]", RegexOptions.CultureInvariant)]
    private static partial Regex SizeToken();

    [GeneratedRegex(@"\b(?:shared|packed)\b\s*,?", RegexOptions.None, 1000)]
    private static partial Regex Unshared();

    [GeneratedRegex(@"\b(?:binding|location|set)\s*=\s*\d+\s*,?", RegexOptions.CultureInvariant)]
    private static partial Regex Assigned();

    [GeneratedRegex(@"\b(?<key>binding|location)\s*=\s*(?<value>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Given();

    [GeneratedRegex(@"^#version[ \t]+(?<number>\d+)(?:[ \t]+(?:core|compatibility))?[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Version();

    [GeneratedRegex(@"\bgl_(?:VertexID|InstanceID)\b", RegexOptions.CultureInvariant)]
    private static partial Regex VertexId();

    [GeneratedRegex(@"\bvoid\s+main\s*\(\s*(?:void\s*)?\)", RegexOptions.CultureInvariant)]
    private static partial Regex MainPattern();

    // Vulkan-only: vertex stages without gl_PointSize write OpenGL's default 1 (undefined in Vulkan); cached apart.
    public static bool PointSized { get; set; }

    // main renamed and called from a one-line main appended at the end: line numbers stay
    private static string Sized(string ported)
    {
        if (!Assert(ported.Length > 0) || ported.Contains("gl_PointSize", StringComparison.Ordinal)) return ported;
        var renamed = MainPattern().Replace(ported, "void komet_main()", 1);
        return Assert(renamed.Length > 0) && !ReferenceEquals(renamed, ported)
            ? renamed + "\nvoid main() { komet_main(); gl_PointSize = 1.0; }\n"
            : ported;
    }

    // Version kept (newer reserve words like sample); below 4.20 two extensions allow set/binding and stage locations
    private static string Versioned(Match version)
    {
        var number = int.Parse(version.Groups["number"].Value, CultureInfo.InvariantCulture);
        if (!Assert(version.Success) || number >= 420) return version.Value;
        return $"#version {Math.Max(number, 140)}{(number >= 150 ? " core" : "")}\n" +
               "#extension GL_ARB_shading_language_420pack : enable\n" +
               "#extension GL_ARB_separate_shader_objects : enable\n#line 2";
    }

    private static string? BlockHead(string head, Context context)
    {
        if (InterfaceBlockPattern().IsMatch(head)) context.Error = $"no port for the interface block {head.Trim()}";
        var match = BlockPattern().Match(head);
        if (!match.Success || !Assert(context.Blocks.Count < MaxBinding * 2)) return null;
        var (layout, storage) = (match.Groups["layout"].Value, match.Groups["kind"].Value == "buffer");
        var gl = GivenValue(layout, "binding");
        if (!Index(Math.Max(gl, 0), MaxBinding))
        {
            context.Error = $"the binding of {match.Groups["name"].Value} is out of range";
            return null;
        }

        var binding = (storage ? FirstStorage : FirstUniformBlock) + Math.Max(gl, 0);
        context.Blocks.Add(new BlockBinding(match.Groups["name"].Value, storage, Math.Max(gl, 0), binding));
        var packed = layout.Contains("std140", StringComparison.Ordinal) ||
                     layout.Contains("std430", StringComparison.Ordinal);
        var packing = storage ? "std430, " : "std140, ";
        if (packed) packing = "";
        var unshared = NotNull(layout) && Assert(layout.Length < MaxLength) ? Unshared().Replace(layout, "") : "";
        var args = Layout(unshared, $"{packing}set = 0, binding = {binding}");
        var (space, qualifiers) = (match.Groups["space"].Value, match.Groups["qualifiers"].Value);
        return $"{space}layout({args}) {qualifiers}{match.Groups["kind"].Value} {match.Groups["name"].Value} ";
    }

    private static int GivenValue(string layout, string key)
    {
        if (!NotNull(layout) || !Assert(key is "binding" or "location")) return -1;
        var given = Given().Matches(layout).FirstOrDefault(m => m.Groups["key"].Value == key);
        return given is null ? -1 : int.Parse(given.Groups["value"].Value, CultureInfo.InvariantCulture);
    }

    private static string Layout(string original, string assigned)
    {
        if (!NotNull(original) || !Assert(assigned.Length > 0)) return assigned;
        var rest = Assigned().Replace(original, "").Trim().TrimEnd(',').Trim();
        return rest.Length == 0 ? assigned : $"{rest}, {assigned}";
    }

    private static string SamplerBinding(Declarator d, string type, Context context)
    {
        if (!NotNull(d) || !Assert(d.Count > 0)) return "";
        if (!context.Samplers.TryGetValue(d.Name, out var sampler))
        {
            var next = context.Samplers.Values.Sum(s => s.Count);
            if (next + d.Count > MaxSamplers) context.Error = "more samplers than the port has bindings for";
            sampler = new Sampler(d.Name, type, FirstSampler + next, d.Count);
            context.Samplers[d.Name] = sampler;
        }

        if (sampler.Type != type || sampler.Count != d.Count) context.Error = $"{d.Name} differs between the stages";
        return Assert(sampler.Binding >= FirstSampler) ? $"set = 0, binding = {sampler.Binding}" : "";
    }

    private static string LocationOf(Declarator d, string type, string layout, (bool Vertex, bool Out) side,
        Context context)
    {
        if (!NotNull(d) || !Assert(d.Count > 0)) return "";
        var size = Shape(type).Columns * d.Count;
        if (size < 1) context.Error = $"no port for the type of {d.Name}: {type}";
        var location = GivenValue(layout, "location");
        switch (side)
        {
            case (true, false) when location < 0 && !(context.Bound?.TryGetValue(d.Name, out location) ?? false):
                context.Error = $"the attribute {d.Name} has no location";
                break;
            case (true, false):
                context.Attributes.Add(new Attribute(d.Name, type, location));
                break;
            case (false, true):
                if (location < 0) location = context.Outputs.Count == 0 ? 0 : context.Outputs.Max() + 1;
                context.Outputs.Add(location);
                break;
            case (true, true) or (false, false)
                when location < 0 && context.Locations.TryGetValue(d.Name, out var known):
                location = known;
                break;
            default:
                location = location >= 0 ? location : context.NextLocation;
                context.Locations[d.Name] = location;
                context.NextLocation = Math.Max(context.NextLocation, location + Math.Max(size, 1));
                break;
        }

        if (context.NextLocation > MaxLocations && NotNull(context) && context.Error.Length == 0)
            context.Error = "more interface locations than a stage has";
        return $"location = {Math.Max(location, 0)}";
    }

    // A location per column, 16 bytes per matrix column in std140; (0, 0) when unknown
    internal static (int Columns, int Rows) Shape(string type)
    {
        if (!Assert(type.Length > 0)) return (0, 0);
        if (type is "float" or "int" or "uint" or "bool") return (1, 1);
        var tail = type.Length > 1 && char.IsAsciiDigit(type[^1]) ? type[^1] - '0' : 0;
        if (type is ['v', 'e', 'c', _] or ['i' or 'u' or 'b', 'v', 'e', 'c', _]) return (1, tail);
        if (!Assert(tail is >= 0 and <= 9)) return (0, 0);
        return type is ['m', 'a', 't', _] && tail is >= 2 and <= 4 ? (tail, tail) : (0, 0);
    }

    // One input per matrix column, each with its own OpenGL pointer (instanced mat4 at 5 reads 5..8), or columns past
    // the first read nothing
    public static Attribute[] Columns(Attribute[] inputs)
    {
        if (!NotNull(inputs) || Array.TrueForAll(inputs, static a => Shape(a.Type).Columns <= 1)) return inputs;
        var columns = new List<Attribute>(inputs.Length + 6);
        foreach (var a in inputs.Bounded(MaxLocations))
        {
            var (count, rows) = Shape(a.Type);
            if (count <= 1) columns.Add(a);
            else
                for (var c = 0; c < Math.Min(count, 4); c++)
                    columns.Add(new Attribute($"{a.Name}[{c}]", $"vec{rows}", a.Location + c));
        }

        return [.. columns];
    }

    // std140 element size: vec3 12 bytes, a matrix a vec4 per column
    public static int Bytes(string type)
    {
        if (!Assert(type.Length > 0)) return 0;
        var (columns, rows) = Shape(type);
        _ = Assert(columns >= 0 && rows >= 0);
        return columns > 1 ? 16 * columns : 4 * rows;
    }

    // A float per component (a matrix's diagonal for a single value); null without an initializer
    private static float[]? Initial(string? init, string type, string name, Context context)
    {
        if (init is null || !Assert(name.Length > 0)) return null;
        var (columns, rows) = Shape(type);
        var open = init.IndexOf('(', StringComparison.Ordinal);
        var inner = init.EndsWith(')') && open > 0 ? init[(open + 1)..^1] : init;
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        var values = new float[Math.Max(columns * rows, 1)];
        if (!Assert(values.Length <= 16)) return null;
        for (var i = 0; i < Math.Min(Math.Min(parts.Length, values.Length), 16); i++)
        {
            var part = parts[i] switch { "true" => "1", "false" => "0", var p => p.TrimEnd('f', 'F', 'u', 'U') };
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                context.Error = $"no port for the default of {name}: {init}";
        }

        if (parts.Length != 1)
        {
            if (parts.Length != values.Length) context.Error = $"no port for the default of {name}: {init}";
            return values;
        }

        _ = Assert(columns * rows <= values.Length || columns == 0); // one value fills every component, or a matrix's diagonal
        for (var i = 1; i < Math.Min(values.Length, 16); i++)
            values[i] = columns > 1 && i % (rows + 1) != 0 ? 0 : values[0];
        return values;
    }
}
