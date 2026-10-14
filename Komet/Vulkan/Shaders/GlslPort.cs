using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Komet.Vulkan;

// Runs after the preprocessor. Storage buffers sit at their OpenGL binding + 40, uniform blocks + 56 (OpenGL has a
// namespace per kind, Vulkan one per set). Vertex outputs get locations in order, fragment inputs by name, as OpenGL's
// linker matches. Line count is kept so compiler messages point into the preprocessed source.
internal static partial class GlslPort
{
    public const int VertexBlock = 0, FragmentBlock = 1, FirstSampler = 8, MaxSamplers = 32, FirstStorage = 40,
        FirstUniformBlock = 56, MaxBinding = 16;
    private const int MaxStatements = 1 << 14, MaxNames = 64, MaxLength = 1 << 21, MaxLocations = 64;

    public sealed record Uniform(string Name, string Type, int Count, float[]? Default, int Offset = -1,
        int Stride = 0);
    public sealed record Sampler(string Name, string Type, int Binding, int Count);
    public readonly record struct Attribute(string Name, string Type, int Location);
    public readonly record struct BlockBinding(string Name, bool Storage, int GlBinding, int Binding);

    private sealed class Context(IReadOnlyDictionary<string, int>? attributes)
    {
        // Loose uniforms put into the push constants both stages share (KometHot)
        public IReadOnlySet<string> Hot { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, Sampler> Samplers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Locations { get; } = new(StringComparer.Ordinal);
        public List<Attribute> Attributes { get; } = [];
        public List<BlockBinding> Blocks { get; } = [];
        public List<int> Outputs { get; } = [];
        public IReadOnlyDictionary<string, int>? Bound { get; } = attributes;
        public int NextLocation { get; set; }
        public string Error { get; set; } = "";
    }

    private static string? Stage(string text, bool vertex, Context context, List<Uniform> uniforms)
    {
        if (!NotNull(text) || !Assert(text.Length < MaxLength) || !NotNull(uniforms)) return null;
        var output = new StringBuilder(text.Length + 4096);
        var (copied, blockAt) = (0, -1);
        foreach (var (start, end, brace) in Statements(text).Bounded(MaxStatements))
        {
            var statement = text[start..end];
            var loose = !brace && Loose(statement);
            var replaced = brace ? BlockHead(statement, context) : Declaration(statement, vertex, context, uniforms);
            if (replaced is null || context.Error.Length > 0) continue;
            _ = output.Append(text, copied, start - copied);
            if (loose && blockAt < 0) blockAt = output.Length;
            _ = output.Append(replaced);
            copied = end;
        }

        _ = output.Append(text, copied, text.Length - copied);
        if (context.Error.Length > 0) return null;
        if (blockAt >= 0) _ = output.Insert(blockAt, LooseBlocks(vertex, uniforms, context.Hot));
        var ported = Version().Replace(output.ToString(), Versioned, 1);
        if (!vertex) return ported;
        ported = VertexId().Replace(ported, m => m.Value == "gl_VertexID" ? "gl_VertexIndex" : "gl_InstanceIndex");
        return PointSized ? Sized(ported) : ported;
    }

    private static string LooseBlocks(bool vertex, List<Uniform> uniforms, IReadOnlySet<string> hot)
    {
        if (!NotNull(uniforms) || !NotNull(hot)) return "";
        var cold = uniforms.Where(u => !hot.Contains(u.Name)).ToList();
        var block = new StringBuilder();
        if (cold.Count > 0)
        {
            _ = block.Append(CultureInfo.InvariantCulture,
                $"layout(std140, set = 0, binding = {(vertex ? VertexBlock : FragmentBlock)}) uniform ");
            _ = block.Append(vertex ? "KometVertex { " : "KometFragment { ");
            foreach (var u in cold.Bounded(MaxStatements))
                _ = block.Append(CultureInfo.InvariantCulture, $"{u.Type} {u.Name}{(u.Count > 1 ? $"[{u.Count}]" : "")}; ");
            _ = block.Append("}; ");
        }

        return (uniforms.Count > cold.Count ? block.Append(HotMarker) : block).ToString();
    }

    private const string HotMarker = "/*KOMET_HOT*/";

    private static List<(int Start, int End, bool Brace)> Statements(string text)
    {
        var found = new List<(int, int, bool)>();
        var (depth, start, lineStart, directive) = (0, 0, true, false);
        for (var i = 0; i < Math.Min(text.Length, MaxLength); i++)
        {
            var c = text[i];
            if (directive)
            {
                if (c == '\n') (directive, start, lineStart) = (false, i + 1, true);
                continue;
            }

            if (c == '#' && lineStart && depth == 0)
            {
                directive = true;
                continue;
            }

            lineStart = c == '\n' || (lineStart && char.IsWhiteSpace(c));
            if (c == '{')
            {
                if (depth == 0) found.Add((start, i, true));
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) start = i + 1;
            }
            else if (c == ';' && depth == 0)
            {
                found.Add((start, i + 1, false));
                start = i + 1;
            }
        }

        _ = Assert(depth == 0) && Assert(found.Count <= MaxStatements);
        return found;
    }

    private static bool Loose(string statement)
    {
        if (!Assert(statement.Length > 0)) return false;
        var match = DeclarationPattern().Match(statement);
        return match.Success && Assert(match.Groups["type"].Length > 0) && Words(match).Contains("uniform") &&
               !Opaque(match.Groups["type"].Value);
    }

    private static HashSet<string> Words(Match declaration)
    {
        var qualifiers = declaration.Groups["qualifiers"].Value;
        _ = Assert(declaration.Success);
        return [.. qualifiers.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Bounded(MaxNames)];
    }

    private static bool Opaque(string type) =>
        Assert(type.Length > 0) && (type.Contains("sampler", StringComparison.Ordinal) ||
                                    type.Contains("image", StringComparison.Ordinal) || type == "atomic_uint");

    // Replacement with as many lines, or null to keep it
    private static string? Declaration(string statement, bool vertex, Context context, List<Uniform> uniforms)
    {
        var match = DeclarationPattern().Match(statement);
        if (!match.Success || !Assert(statement.EndsWith(';'))) return null;
        var (words, type, layout) = (Words(match), match.Groups["type"].Value, match.Groups["layout"].Value);
        var names = Declarators(match.Groups["names"].Value, context);
        if (match.Groups["size"].Success) names = SizedByType(names, match.Groups["size"].Value, context);
        var lines = new string('\n', statement.Count(c => c == '\n'));
        var leading = match.Groups["space"].Value.Count(c => c == '\n');
        var after = new string('\n', statement.Count(c => c == '\n') - leading);
        if (words.Contains("uniform") && !Opaque(type))
        {
            foreach (var d in names.Bounded(MaxNames))
                uniforms.Add(new Uniform(d.Name, type, d.Count, Initial(d.Init, type, d.Name, context)));
            return lines;
        }

        var qualifiers = Spaces().Replace(match.Groups["qualifiers"].Value.Trim(), " ");
        var ported = new StringBuilder(match.Groups["space"].Value);
        foreach (var d in names.Bounded(MaxNames))
        {
            var binding = words.Contains("uniform") ? SamplerBinding(d, type, context) : LocationOf(d, type, layout,
                (vertex, words.Contains("out")), context);
            var array = d.Size is null ? "" : $"[{d.Count}]";
            var qualified = words.Contains("uniform") ? "uniform" : qualifiers;
            _ = ported.Append(CultureInfo.InvariantCulture,
                $"layout({Layout(layout, binding)}) {qualified} {type} {d.Name}{array}; ");
        }

        return ported.Append(after).ToString();
    }

    private sealed record Declarator(string Name, string? Size, int Count, string? Init);

    // float[4] a, b: the size on the type, for every name
    private static List<Declarator> SizedByType(List<Declarator> names, string size, Context context)
    {
        var count = ArraySize(size);
        if (count < 1) context.Error = $"the array size {size} is no constant";
        _ = Assert(names.Count <= MaxNames);
        return [.. names.Select(d => d with { Size = size, Count = Math.Max(count, 1) })];
    }

    private static List<Declarator> Declarators(string names, Context context)
    {
        var found = new List<Declarator>();
        var (depth, start) = (0, 0);
        if (!NotNull(context) || !Assert(names.Length < MaxLength)) return found;
        for (var i = 0; i <= Math.Min(names.Length, MaxLength); i++)
        {
            var c = i < names.Length ? names[i] : ',';
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            if (c != ',' || depth != 0) continue;
            var part = DeclaratorPattern().Match(names[start..Math.Min(i, names.Length)]);
            start = i + 1;
            if (!Assert(found.Count < MaxNames) || !part.Success)
            {
                context.Error = $"no port for the declaration of {names.Trim()}";
                return found;
            }

            var size = part.Groups["size"].Success ? part.Groups["size"].Value : null;
            var count = size is null ? 1 : ArraySize(size);
            if (count < 1) context.Error = $"the array size of {part.Groups["name"].Value} is no constant: {size}";
            var init = part.Groups["init"].Success ? part.Groups["init"].Value.Trim() : null;
            found.Add(new Declarator(part.Groups["name"].Value, size, Math.Max(count, 1), init));
        }

        return found;
    }

    // Sums and products of integers (what the engine's macros leave); -1 otherwise
    private static int ArraySize(string size)
    {
        var tokens = SizeToken().Matches(size);
        var spelled = size.Count(c => !char.IsWhiteSpace(c));
        if (!Assert(size.Length < MaxLength) || tokens.Sum(t => t.Length) != spelled) return -1;
        var (sum, term, add) = (0L, 1L, Assert(tokens.Count < MaxLength));
        for (var i = 0; i < Math.Min(tokens.Count, MaxNames); i++)
        {
            var token = tokens[i].Value;
            if (i % 2 == 0 && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                term *= value;
            else if (i % 2 == 1 && token is "+" or "*") (sum, term) = token == "+" ? (sum + term, 1L) : (sum, term);
            else add = false;
        }

        return add && tokens.Count % 2 == 1 && sum + term is > 0 and < MaxLength ? (int)(sum + term) : -1;
    }
}
