namespace Komet.Vulkan;

// Recipe encoding: GlTap.Fixed as Caps, Depth, offset bits, eight blend words, stencil word (older: twelve words, no
// stencil); attributes nine numbers each (TerrainPools.Attribute). Scene and terrain share the program's sources.
internal static class PipelineRecipes
{
    public const int AttributeWords = 9, StateWords = 13, OldStateWords = 12, MaxInputs = 16, MaxColors = 8;
    public const string Terrain = "t|";

    public static ShaderCache.Recipe Packed((string Inputs, string Target, uint Topology) key, GlTap.Fixed state,
        TerrainPools.Attribute[] attributes, TerrainPipeline.Target into)
    {
        _ = NotNull(attributes) && Assert(attributes.Length <= MaxInputs);
        uint[] words =
        [
            state.Caps, state.Depth, BitConverter.SingleToUInt32Bits(state.OffsetFactor),
            BitConverter.SingleToUInt32Bits(state.OffsetUnits), state.Blend.B0, state.Blend.B1, state.Blend.B2,
            state.Blend.B3, state.Blend.B4, state.Blend.B5, state.Blend.B6, state.Blend.B7, state.Stencil
        ];
        var packed = new long[Math.Min(attributes.Length, MaxInputs) * AttributeWords];
        for (var i = 0; i < Math.Min(attributes.Length, MaxInputs); i++)
        {
            var a = attributes[i];
            ReadOnlySpan<long> one =
            [
                a.Location, a.Buffer, a.Size, a.Type, a.Normalized ? 1 : 0, a.Integer ? 1 : 0, a.Stride, a.Offset, a.Divisor
            ];
            one.CopyTo(packed.AsSpan(i * AttributeWords));
        }

        _ = Assert(words.Length == StateWords);
        return new ShaderCache.Recipe(key.Inputs, key.Target, key.Topology, [.. into.Colors], into.Depth, words, packed);
    }

    public static (GlTap.Fixed State, TerrainPools.Attribute[] Attributes, TerrainPipeline.Target Target)? Unpacked(
        ShaderCache.Recipe recipe)
    {
        if (!NotNull(recipe) || recipe.Fixed.Length is not (StateWords or OldStateWords) ||
            recipe.Attributes.Length % AttributeWords != 0 || recipe.Attributes.Length > MaxInputs * AttributeWords ||
            recipe.Colors.Length > MaxColors) return null;
        var w = recipe.Fixed;
        var state = new GlTap.Fixed(w[0], w[1], BitConverter.UInt32BitsToSingle(w[2]), BitConverter.UInt32BitsToSingle(w[3]),
            new GlTap.BlendState(w[4], w[5], w[6], w[7], w[8], w[9], w[10], w[11]), w.Length == StateWords ? w[12] : 0);
        var a = recipe.Attributes;
        var attributes = new TerrainPools.Attribute[a.Length / AttributeWords];
        for (var i = 0; i < Math.Min(attributes.Length, MaxInputs); i++)
        {
            var at = i * AttributeWords;
            attributes[i] = new TerrainPools.Attribute((int)a[at], (int)a[at + 1], (int)a[at + 2], (int)a[at + 3], a[at + 4] != 0,
                a[at + 5] != 0) { Stride = (int)a[at + 6], Offset = a[at + 7], Divisor = (int)a[at + 8] };
        }

        _ = Assert(attributes.Length <= MaxInputs);
        return (state, attributes, new TerrainPipeline.Target([.. recipe.Colors], recipe.Depth));
    }

    // "" without cache or record
    public static string SourcesOf(int program)
    {
        if (!ShaderCache.Active || !Assert(program > 0) || GlTap.LinkedOf(program) is not { } linked) return "";
        var sources = ShaderCache.Sources(linked.Vertex, linked.Fragment, linked.Attributes);
        _ = Assert(sources.Length is 0 or 32);
        return sources;
    }
}
