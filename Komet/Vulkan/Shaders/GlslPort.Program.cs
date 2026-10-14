namespace Komet.Vulkan;

internal static partial class GlslPort
{
    public sealed record Block(int Binding, int Size, Uniform[] Members);

    public const int HotBlock = -1, MaxHotBytes = 256;

    public sealed record Ported(string Name, string VertexSource, string FragmentSource, uint[] Vertex, uint[] Fragment)
    {
        public required Block VertexUniforms { get; init; }
        public required Block FragmentUniforms { get; init; }

        public Block HotUniforms { get; init; } = new(HotBlock, 0, []);
        public required Sampler[] Samplers { get; init; }
        public required BlockBinding[] Blocks { get; init; }
        public required Attribute[] Attributes { get; init; }
        public required int[] Outputs { get; init; }
    }

    // attributes: glBindAttribLocation locations; hot: loose uniforms changing nearly every draw, pushed
    public static Ported? Build(string name, string vertex, string fragment,
        IReadOnlyDictionary<string, int>? attributes, out string error, IReadOnlySet<string>? hot = null)
    {
        error = "";
        if (!NotNull(vertex) || !NotNull(fragment) || !Assert(name.Length > 0)) return null;
        var context = new Context(attributes) { Hot = hot ?? new HashSet<string>(StringComparer.Ordinal) };
        var (vertexUniforms, fragmentUniforms) = (new List<Uniform>(), new List<Uniform>());
        var vertexText = Translate(vertex, true, name, context, vertexUniforms);
        var fragmentText = vertexText is null ? null : Translate(fragment, false, name, context, fragmentUniforms);
        error = context.Error;
        if (vertexText is null || fragmentText is null) return null;
        var pushed = Pushed(vertexUniforms, fragmentUniforms, context.Hot);
        (vertexText, fragmentText) = (vertexText.Replace(HotMarker, HotDeclaration(pushed), StringComparison.Ordinal),
            fragmentText.Replace(HotMarker, HotDeclaration(pushed), StringComparison.Ordinal));
        var v = Shaderc.Compile(vertexText, Shaderc.Vertex, name + ".vsh", out error, relaxed: false);
        var f = v is null
            ? null
            : Shaderc.Compile(fragmentText, Shaderc.Fragment, name + ".fsh", out error, relaxed: false);
        if (v is null || f is null) return null;
        var declaring = Spirv.Members(v, "KometHot").Length > 0 ? v : f; // a stage that declares the push constants
        var hotBlock = pushed.Count > 0 ? Laid(declaring, "KometHot", HotBlock, pushed) : new Block(HotBlock, 0, []);
        if (hotBlock.Size > MaxHotBytes)
        {
            error = $"the hot uniforms take {hotBlock.Size} bytes, more than push constants hold";
            return null;
        }

        return new Ported(name, vertexText, fragmentText, v, f)
        {
            VertexUniforms = Laid(v, "KometVertex", VertexBlock,
                [.. vertexUniforms.Where(u => !context.Hot.Contains(u.Name))]),
            FragmentUniforms = Laid(f, "KometFragment", FragmentBlock,
                [.. fragmentUniforms.Where(u => !context.Hot.Contains(u.Name))]),
            HotUniforms = hotBlock,
            Samplers = [.. context.Samplers.Values.OrderBy(s => s.Binding)], Blocks = [.. context.Blocks],
            Attributes = [.. context.Attributes], Outputs = [.. context.Outputs.Order()]
        };
    }

    private static List<Uniform> Pushed(List<Uniform> vertex, List<Uniform> fragment, IReadOnlySet<string> hot)
    {
        var pushed = new List<Uniform>();
        if (!NotNull(hot) || hot.Count == 0) return pushed;
        foreach (var u in vertex.Concat(fragment).ToArray().Bounded(MaxStatements))
            if (hot.Contains(u.Name) && !pushed.Exists(p => p.Name == u.Name))
                pushed.Add(u);
        _ = Assert(pushed.Count <= hot.Count);
        return pushed;
    }

    // One line, identical in both stages (members a stage leaves unread too)
    private static string HotDeclaration(List<Uniform> pushed)
    {
        if (!NotNull(pushed) || pushed.Count == 0) return "";
        var members = string.Join(" ", pushed.Select(u => $"{u.Type} {u.Name}{(u.Count > 1 ? $"[{u.Count}]" : "")};"));
        _ = Assert(members.Length > 0);
        return $"layout(push_constant) uniform KometHot {{ {members} }}; "; // std430, as push constants are laid out
    }

    private static string? Translate(string source, bool vertex, string name, Context context, List<Uniform> uniforms)
    {
        if (!NotNull(source) || !NotNull(uniforms)) return null;
        var (kind, file) = vertex ? (Shaderc.Vertex, name + ".vsh") : (Shaderc.Fragment, name + ".fsh");
        var text = Shaderc.Preprocess(source, kind, file, out var error);
        if (text is not null && Assert(context.Error.Length == 0)) return Stage(text, vertex, context, uniforms);
        if (text is null) context.Error = $"{file}: {error}";
        return null;
    }

    private static Block Laid(uint[] words, string block, int binding, List<Uniform> uniforms)
    {
        if (!NotNull(words) || !Assert(binding is VertexBlock or FragmentBlock or HotBlock))
            return new Block(binding, 0, []);
        var members = Spirv.Members(words, block);
        var laid = new Uniform[uniforms.Count];
        var size = 0;
        for (var i = 0; i < Math.Min(laid.Length, MaxStatements); i++)
        {
            var u = uniforms[i];
            var m = i < members.Length && members[i].Name == u.Name ? members[i] : new Spirv.Member(u.Name, -1, 0);
            laid[i] = u with { Offset = m.Offset, Stride = m.Stride };
            size = Math.Max(size, m.Offset + (u.Count > 1 ? m.Stride * u.Count : Bytes(u.Type)));
        }

        _ = Assert(laid.All(u => u.Offset >= 0));
        return new Block(binding, (size + 15) / 16 * 16, laid);
    }
}
