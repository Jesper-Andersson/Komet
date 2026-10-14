namespace Komet.Gpu;

// GL and Vulkan: #version 460, unique explicit bindings (Vulkan shares one namespace per set), std140 parameters.
internal static class GpuShaders
{
    public const string HiZ = "hiz.comp", Cull = "cull.comp", Rows = "rows.comp";
    public const string Sort = "sort.comp";
    private const string Prefix = "Komet.Gpu.Shaders.";

    public static readonly string[] All = [HiZ, Cull, Rows, Sort];

    public static string? Source(string name)
    {
        if (!NotNull(name) || !Assert(Array.IndexOf(All, name) >= 0)) return null;
        using var stream = typeof(GpuShaders).Assembly.GetManifestResourceStream(Prefix + name);
        if (!NotNull(stream)) return null;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return Assert(text.StartsWith("#version 460", StringComparison.Ordinal)) ? text : null;
    }
}
