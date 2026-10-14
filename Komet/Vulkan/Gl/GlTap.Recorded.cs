using OpenTK.Graphics.OpenGL;
using Attachments = Komet.Vulkan.GlFramebuffer;

namespace Komet.Vulkan;

// What the record taps (VulkanRenderer.OnlyOn) learn of GL objects as the engine makes them, so Vulkan never asks GL
// what it told GL. Objects made before the record (or a gap) are asked once on first sight, then followed. Answers are
// GL's own: a driver-chosen format (GL_RGBA) is asked once per format and type.
internal static unsafe partial class GlTap
{
    private const int MaxLevels = 16, MaxRecords = 1 << 16, MaxProgramRecords = 4096, MaxNamed = 1024;
    private const int GlTextureTarget = 0x1006, GlTextureWidth = 0x1000, GlTextureHeight = 0x1001;
    private const int GlTextureDepth = 0x8071, GlInternalFormat = 0x1003, GlMaxLevel = 0x813D, GlBorderColor = 0x1004,
        GlBaseLevel = 0x813C;
    private const int GlMinFilter = 0x2801, GlMagFilter = 0x2800, GlWrapS = 0x2802, GlWrapT = 0x2803;
    private const int GlCompareMode = 0x884C, GlCompareFunc = 0x884D;
    private const uint InvalidIndex = uint.MaxValue;

    // What OpenGL holds of a texture; Levels: bit n set while level n is specified
    private sealed class TextureRecord
    {
        public uint Target;
        public int Width, Height, Depth = 1, Format, Levels;
        public uint Type;
        public int Min = 0x2702, Mag = 0x2601, WrapS = 0x2901, WrapT = 0x2901, Compare, CompareFunc = 0x0203;
        public int MaxLevel = 1000, BaseLevel;
        public (float R, float G, float B, float A) Border;

        // Bound to an unfollowed target (3D, rectangle...) or specified by an unheard call: asked from GL each time
        public bool Untracked;
    }

    // Fresh: seen made, draw/read buffers GL defaults until set; Renderbuffers: renderbuffer attachment bits (names)
    private sealed class FramebufferRecord
    {
        public readonly Attachments.Attached[] Attached = new Attachments.Attached[Attachments.Colors + 1];
        public readonly uint[] Renderbuffer = new uint[Attachments.Colors + 1];
        public bool Fresh;
        public int Renderbuffers;
    }

    // What a program was linked from, if every attached stage was recorded; Other: a stage beside vertex and fragment
    public sealed record Linked(string Vertex, string Fragment, int Other, IReadOnlyDictionary<string, int> Attributes);

    private sealed class ProgramRecord
    {
        public readonly List<uint> Shaders = [];
        public readonly Dictionary<string, int> AttributeBinds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Locations = new(StringComparer.Ordinal);
        public readonly Dictionary<string, uint> Blocks = new(StringComparer.Ordinal);
        public readonly Dictionary<uint, int> Points = [];
        public Linked? Stages;
    }

    private sealed class ShaderRecord(uint type)
    {
        public uint Type { get; } = type;
        public string Source { get; set; } = "";
    }

    private static readonly Dictionary<uint, TextureRecord> TextureRecords = [];
    private static readonly Dictionary<uint, FramebufferRecord> FramebufferRecords = [];
    private static readonly Dictionary<uint, ProgramRecord> ProgramRecords = [];
    private static readonly Dictionary<uint, ShaderRecord> ShaderRecords = [];
    private static readonly Dictionary<uint, (int Min, int Mag, int Wrap, int Compare)> SamplerRecords = [];
    private static readonly Dictionary<uint, long> BufferSizes = [];
    // What the driver answers for each format left to it
    private static readonly Dictionary<(int Format, uint Type), int> SizedFormats = [];

    // Whether the record taps are in (Tap's records)
    public static bool Recording { get; private set; }

    // OpenGL was asked for an object the record did not know (made before it); for the report
    public static long RecordMisses { get; private set; }

    public static (int Textures, int Framebuffers, int Programs, int Linked, int Buffers, long Misses) Records =>
        Assert(TextureRecords.Count <= MaxRecords + MaxNames) && Assert(ProgramRecords.Count <= MaxProgramRecords)
            ? (TextureRecords.Count, FramebufferRecords.Count, ProgramRecords.Count,
                ProgramRecords.Values.Count(p => p.Stages is not null), BufferSizes.Count, RecordMisses)
            : default;

    private static void Unrecord()
    {
        TextureRecords.Clear();
        FramebufferRecords.Clear();
        ProgramRecords.Clear();
        ShaderRecords.Clear();
        SamplerRecords.Clear();
        BufferSizes.Clear();
        SizedFormats.Clear();
        Unrenderbuffered();
        (Recording, RecordMisses) = (false, 0);
        _ = Assert(TextureRecords.Count == 0) && Assert(ProgramRecords.Count == 0);
    }

    private static bool Unsized(int format) =>
        Assert(format >= 0) && Assert(Recording) && format is 0x1902 or 0x84F9 or 0x1903 or 0x8227 or 0x1907 or
            0x1908 or 0x1909 or 0x190A or 0x1906 or (>= 1 and <= 4);

    private static TextureRecord? Texture(uint texture)
    {
        if (texture == 0 || !Recording || !Assert(TextureRecords.Count <= MaxRecords)) return null;
        if (texture >= RenderbufferBase) return TextureRecords.GetValueOrDefault(texture); // OpenGL has no such texture to ask
        if (TextureRecords.TryGetValue(texture, out var known) && Assert(known.Levels >= 0) && !known.Untracked &&
            known.Levels != 0) return known;
        if (TextureRecords.Count >= MaxRecords) TextureRecords.Clear();
        var t = SeededTexture((int)texture);
        t.Untracked = (known?.Untracked ?? false) || (t.Target != 0 && !Tracked(t.Target));
        TextureRecords[texture] = t;
        RecordMisses++;
        return t;
    }

    private static TextureRecord SeededTexture(int texture)
    {
        int Get(int name)
        {
            GL.GetTextureParameter(texture, (GetTextureParameter)name, out int value);
            return value;
        }

        int Level(int level, int name)
        {
            GL.GetTextureLevelParameter(texture, level, (GetTextureParameter)name, out int value);
            return value;
        }

        var t = new TextureRecord
        {
            Target = (uint)Get(GlTextureTarget), Width = Level(0, GlTextureWidth), Height = Level(0, GlTextureHeight),
            Depth = Level(0, GlTextureDepth), Format = Level(0, GlInternalFormat), Min = Get(GlMinFilter),
            Mag = Get(GlMagFilter), WrapS = Get(GlWrapS), WrapT = Get(GlWrapT), Compare = Get(GlCompareMode),
            CompareFunc = Get(GlCompareFunc), MaxLevel = Get(GlMaxLevel), BaseLevel = Get(GlBaseLevel)
        };
        var border = new float[4];
        GL.GetTextureParameter(texture, (GetTextureParameter)GlBorderColor, border);
        t.Border = (border[0], border[1], border[2], border[3]);
        for (var level = 0; level < MaxLevels && Level(level, GlTextureWidth) > 0; level++) t.Levels |= 1 << level;
        _ = Assert(texture > 0) && Assert(t.Levels >= 0);
        return t;
    }

    // Made under the record: GL defaults, target unknown until bound (glGenTextures) or given (glCreateTextures)
    private static void TexturesMade(uint target, int count, uint* names)
    {
        if (!Recording || names == null || !Assert(count >= 0) || count > MaxNames) return;
        if (TextureRecords.Count + count > MaxRecords) TextureRecords.Clear();
        for (var i = 0; i < Math.Min(count, MaxNames); i++)
            if (names[i] != 0)
                TextureRecords[names[i]] = new TextureRecord { Target = target };
        _ = Assert(TextureRecords.Count <= MaxRecords + MaxNames);
    }

    // The first bind gives a texture made by glGenTextures its target
    private static void TextureBound(uint texture, uint target)
    {
        if (!TextureRecords.TryGetValue(texture, out var t) || t.Target != 0 || !Assert(texture > 0)) return;
        (t.Target, t.Untracked) = (target, !Tracked(target));
        _ = Assert(t.Target == target);
    }

    // The targets whose bound texture the units follow (BoundName)
    private static bool Tracked(uint target) =>
        Assert(target != uint.MaxValue) && Assert(UnitArrays.Length == MaxUnits) &&
        target is Texture2D or Texture2DArray or TextureCube or (>= CubeFirst and <= CubeLast);

    // The targets a glTexImage* or glTexStorage* names a texture by that the units follow (no proxies)
    private static bool Specifiable(uint target) => Assert(Recording) && Tracked(target);

    private static uint Plain(uint target) =>
        Assert(target > 0) && Assert(Recording) && target is >= CubeFirst and <= CubeLast ? TextureCube : target;

    // A level specified anew (glTexImage*, glTexStorage* for levels at once); width 0 takes the level away
    private static void Specified(uint texture, uint target, (int Level, int Count) levels,
        (int Format, int Width, int Height, int Depth) spec, uint type)
    {
        if (!Recording || !TextureRecords.TryGetValue(texture, out var t)) return;
        if (!Assert(levels.Count is >= 1 and <= 64) || !Index(levels.Level, MaxLevels)) return;
        if (t.Target == 0 && target != 0) t.Target = Plain(target);
        var bits = (int)(((1L << Math.Min(levels.Count, MaxLevels)) - 1) << levels.Level) & 0xFFFF;
        t.Levels = spec.Width > 0 ? t.Levels | bits : t.Levels & ~bits;
        if (levels.Level != 0) return;
        var format = Unsized(spec.Format) && SizedFormats.TryGetValue((spec.Format, type), out var sized)
            ? sized
            : spec.Format;
        (t.Width, t.Height, t.Depth) = (spec.Width, spec.Height, Math.Max(spec.Depth, 1));
        (t.Format, t.Type) = (format, type);
    }

    // glGenerateMipmap: every level from the base on, up to the smallest or GL_TEXTURE_MAX_LEVEL
    private static void MipmapsMade(uint texture)
    {
        if (!Recording || !TextureRecords.TryGetValue(texture, out var t) || t.Width <= 0) return;
        var side = Math.Max(Math.Max(t.Width, t.Height), t.Target == 0x806F ? t.Depth : 1); // a 3D one in depth too
        var log = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)side);
        var top = Math.Min(Math.Min(log, t.MaxLevel), MaxLevels - 1);
        t.Levels |= (int)((2L << Math.Max(top, 0)) - 1);
        _ = Assert(top < MaxLevels) && Assert(t.Levels > 0);
    }

    private static void TextureTuned(uint texture, uint name, float value)
    {
        if (!Recording || !TextureRecords.TryGetValue(texture, out var t) || !Finite(value)) return;
        var whole = (int)value;
        switch (name)
        {
            case GlMinFilter: t.Min = whole; break;
            case GlMagFilter: t.Mag = whole; break;
            case GlWrapS: t.WrapS = whole; break;
            case GlWrapT: t.WrapT = whole; break;
            case GlCompareMode: t.Compare = whole; break;
            case GlCompareFunc: t.CompareFunc = whole; break;
            case GlMaxLevel: t.MaxLevel = whole; break;
            case GlBaseLevel: t.BaseLevel = whole; break;
        }

        _ = Assert(name > 0);
    }

    // What OpenGL would answer for the texture's target, level 0 and format; null when not recording
    public static GlTexture.Description? Described(int texture)
    {
        if (texture <= 0 || Texture((uint)texture) is not { } t) return null;
        if (!Assert(t.Depth >= 0) || !Assert(t.Width >= 0)) return null;
        if (Unsized(t.Format) && t.Width > 0)
        {
            GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)GlInternalFormat, out int asked);
            if (SizedFormats.Count < MaxNamed) SizedFormats[(t.Format, t.Type)] = asked;
            (t.Format, RecordMisses) = (asked, RecordMisses + 1);
        }

        return new GlTexture.Description((int)t.Target, t.Width, t.Height, t.Depth, t.Format);
    }

    // The levels that exist from level 0 on, up to GL_TEXTURE_MAX_LEVEL (at least one, as OpenGL's answer counts)
    public static int? LevelsOf(int texture)
    {
        if (texture <= 0 || Texture((uint)texture) is not { } t || !Assert(t.MaxLevel >= 0)) return null;
        var levels = 1;
        for (var level = 1; level < Math.Min(MaxLevels, t.MaxLevel + 1); level++)
        {
            if ((t.Levels & (1 << level)) == 0) break;
            levels++;
        }

        _ = Assert(levels <= MaxLevels);
        return levels;
    }

    public static Samplers.State? SamplingOf(int texture) =>
        texture > 0 && Texture((uint)texture) is { } t && Assert(t.Min >= 0) && Finite(t.Border.R)
            ? Samplers.FromGl(t.Min, t.Mag, t.WrapS, t.Compare != 0) with { WhiteBorder = t.Border.R > 0.5f }
            : null;

    // One of the parameters the record follows (filters, wraps, comparison, GL_TEXTURE_MAX_LEVEL), else null
    public static int? ParameterOf(int texture, int name)
    {
        if (texture <= 0 || !Assert(name > 0) || Texture((uint)texture) is not { } t) return null;
        _ = Assert(t.Min >= 0);
        return name switch
        {
            GlMinFilter => t.Min, GlMagFilter => t.Mag, GlWrapS => t.WrapS, GlWrapT => t.WrapT,
            GlCompareMode => t.Compare, GlCompareFunc => t.CompareFunc, GlMaxLevel => t.MaxLevel, GlBaseLevel => t.BaseLevel,
            _ => null
        };
    }

    // glTex(ture)Parameter*v's GL_TEXTURE_BORDER_COLOR, integers normalized
    private static void Bordered(uint texture, float* values, bool integer)
    {
        if (!Recording || values == null || !TextureRecords.TryGetValue(texture, out var t)) return;
        _ = Assert(texture > 0);
        var scale = integer ? 1f / int.MaxValue : 1f;
        t.Border = (values[0] * scale, values[1] * scale, values[2] * scale, values[3] * scale);
        _ = Finite(t.Border.R);
    }

    // The border color as the record follows it
    public static float[]? BorderOf(int texture) =>
        texture > 0 && Recording && Texture((uint)texture) is { } t && Finite(t.Border.R) && Finite(t.Border.A)
            ? [t.Border.R, t.Border.G, t.Border.B, t.Border.A]
            : null;

    // Whether the name is a texture: known to the record, or else asked; null when not recording
    public static bool? TextureExists(int texture)
    {
        if (!Recording || texture <= 0 || !Assert(TextureRecords.Count <= MaxRecords + MaxNames)) return null;
        return TextureRecords.ContainsKey((uint)texture) || (Assert(Recording) && GL.IsTexture(texture));
    }
}
