using OpenTK.Graphics.OpenGL;
using unsafe TexImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, uint, uint, nint, void>;

namespace Komet.Vulkan;

// Vulkan-only lean (VulkanRenderer.LeanOn): GL keeps a second copy of every uploaded texture and buffer (shadow GL) so
// it can take over when Vulkan-only lets go. Lean withholds the large ones: a texture with >= LeanTexels at level 0
// gets a 1x1 level 0 (name, kind and format kept; sizes and levels answered from the record), no other levels or
// uploads; a never-mapped buffer >= LeanBufferBytes gets 16 bytes, no data, sub-data or copies. That covers atlases,
// shadow maps, screen targets and non-persistent pools, the bulk of GL's copy. Both stay respecifiable (never immutable
// storage) so GL can take over once Vulkan restores size and contents (Restored). GUI textures (Gui: Cairo's, the Ortho
// stage's) always reach GL. Only if nothing can be read from Vulkan (device fault) is GL stranded: resting until the
// world is left, drawing only the GUI over a cleared frame (VulkanRenderer.Overlaid).
internal static unsafe partial class GlTap
{
    private const long LeanTexels = 1 << 20, LeanBufferBytes = 8 << 20, LeanPoolBytes = 1 << 20;
    private const nint LeanBufferKept = 16;
    private const int MaxLean = 1 << 14;
    private const uint MapBits = 0x1 | 0x2 | 0x40 | 0x80; // GL_MAP_READ, _WRITE, _PERSISTENT, _COHERENT

    private static readonly Dictionary<uint, long> LeanTextures = [];
    private static readonly Dictionary<uint, (long Size, uint Usage)> LeanBuffers = [];

    public enum Leaning
    {
        Given, // OpenGL gets the call as the engine made it
        Small, // a 1x1 (or 16-byte) stand-in, no data
        Held // nothing
    }

    // Set while Vulkan-only runs with the lean switch on, from the record's tap
    public static bool Lean { get; set; }

    // A chunk pool is being made (MeshDataPool.AllocateNewPool under VulkanRenderer's birth seam): buffers lean from
    // LeanPoolBytes. A 2M-vertex pool's colour buffer is 8,000,000 bytes, just under LeanBufferBytes, so GL kept ~8 MB
    // per pool (~0.9 GB at view distance 1536) that Vulkan holds too.
    public static bool PoolBirth { get; set; }

    // OpenGL holds back data it would need to take over: it rests (Rests) even with no Vulkan-only running, until Untap
    public static bool Stranded { get; private set; }

    // GUI stage (Ortho): textures reach GL whole; stranded, GL draws it (own textures, meshes) over the cleared frame
    public static bool GuiStage { get; set; }

    // The GUI makes a texture (Cairo's, or one in the Ortho stage): it reaches OpenGL whole, however large
    public static int Gui { get; set; }

    public static int Leaned => LeanTextures.Count + LeanBuffers.Count;

    // What lean kept out of OpenGL's memory (level 0 at 4 bytes a texel, mipmaps a third more, buffers as specified)
    public static long LeanBytes { get; private set; }

    // OpenGL holds none of the texture's or buffer's contents: Komet must not read it back
    public static bool LeanTexture(uint texture) => Leaned > 0 && Assert(texture != uint.MaxValue) && LeanTextures.ContainsKey(texture);
    public static bool LeanBuffer(uint buffer) => Leaned > 0 && Assert(buffer != uint.MaxValue) && LeanBuffers.ContainsKey(buffer);

    // OpenGL is not to take over: no listener is left, and it runs no GPU work in the world until the world is left
    public static void Strand()
    {
        Stranded = Assert(Tapped) && Assert(Recording);
        Unlisten();
        _ = Assert(Writes is null);
    }

    // A level-0 spec >= LeanTexels: 1x1 stand-in; levels past 0 of a held-back texture: nothing. A smaller level-0 spec
    // gives the texture back to GL as specified (earlier contents never were GL's).
    private static Leaning TextureLean(uint texture, int level, (long Width, long Height, long Depth) size)
    {
        if (!Lean || _quiet > 0 || !Recording || Writes is null || texture == 0 || !Assert(level >= 0)) return Leaning.Given;
        if (level > 0) return LeanTextures.ContainsKey(texture) ? Leaning.Held : Leaning.Given;
        var bytes = 4 * Math.Max(size.Width, 1) * Math.Max(size.Height, 1) * Math.Max(size.Depth, 1);
        if (bytes < 4 * LeanTexels || Gui > 0 || GuiStage ||
            (LeanTextures.Count >= MaxLean && !LeanTextures.ContainsKey(texture)))
        {
            if (LeanTextures.Remove(texture, out var was)) LeanBytes -= was;
            return Leaning.Given;
        }

        bytes += bytes / 3; // the mipmaps it may get
        if (LeanTextures.Remove(texture, out var before)) LeanBytes -= before;
        (LeanTextures[texture], LeanBytes) = (bytes, LeanBytes + bytes);
        return Assert(LeanBytes >= 0) ? Leaning.Small : Leaning.Given;
    }

    // Textures GL got whole before lean began: screen targets and shadow maps made at game start (two 6144x6144 depth
    // maps at shadow quality 4) and atlases of a world joined before the record, ~0.7 GB at 1440p (heap dump
    // 2026-10-12). Each gets lean's stand-in once Vulkan holds it (held: a framebuffer target Vulkan draws, or a
    // mipmapped texture with a current Vulkan image): 1x1 level 0, other levels freed (0x0 glTexImage2D). Sizes stay in
    // the record; Strands gives each back. Immutable storage (glTexStorage) cannot be respecified and stays.
    internal static (int Count, long Bytes) LeanEarlier(System.Func<uint, bool, bool> held)
    {
        if (!Lean || _quiet > 0 || !Recording || Stranded || !NotNull(held) || !Index(TouchAt + 41, _entries.Length) ||
            Original[TouchAt + 41] == IntPtr.Zero) return (0, 0);
        var found = new List<(uint Name, TextureRecord Record)>();
        foreach (var (name, record) in TextureRecords.Bounded(MaxRecords))
            if (record is { Target: Texture2D, Untracked: false, Levels: not 0 } && (long)record.Width * record.Height >= LeanTexels &&
                !LeanTextures.ContainsKey(name) && LeanTextures.Count + found.Count < MaxLean && held(name, (record.Levels & ~1) != 0))
                found.Add((name, record));
        if (found.Count == 0) return (0, 0);
        var (count, bytes) = (0, 0L);
        using var quiet = Quietly(); // the record hears none of it: it keeps the full sizes
        var bound = GL.GetInteger(GetPName.TextureBinding2D);
        var unpack = GL.GetInteger(GetPName.PixelUnpackBufferBinding);
        if (unpack != 0) GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0); // no data: not an offset into a bound buffer
        try
        {
            foreach (var (name, record) in found.Bounded(MaxLean))
            {
                GL.GetTextureParameter((int)name, GetTextureParameter.TextureImmutableFormat, out int immutable);
                if (immutable != 0) continue;
                GL.BindTexture(TextureTarget.Texture2D, (int)name);
                var (format, type) = SpecData((uint)record.Format);
                var spec = (TexImage2DFn)Original[TouchAt + 41];
                spec(Texture2D, 0, record.Format, 1, 1, 0, format, type, 0);
                for (var level = 1; level < MaxLevels; level++)
                    if ((record.Levels & (1 << level)) != 0) spec(Texture2D, level, record.Format, 0, 0, 0, format, type, 0);
                var size = 4L * record.Width * record.Height;
                if ((record.Levels & ~1) != 0) size += size / 3;
                (LeanTextures[name], LeanBytes, count, bytes) = (size, LeanBytes + size, count + 1, bytes + size);
            }
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, bound);
            if (unpack != 0) GL.BindBuffer(BufferTarget.PixelUnpackBuffer, unpack);
        }

        return Assert(LeanBytes >= 0) ? (count, bytes) : (0, 0);
    }


    // An upload into a held-back texture: OpenGL has no room for it
    private static bool TextureHeld(uint texture) => _quiet == 0 && LeanTexture(texture) && Assert(Lean || Stranded || Leaned > 0);

    // Buffer storage >= LeanBufferBytes not made for mapping gets a 16-byte stand-in, always mutable (glBufferData,
    // also for glBufferStorage) so size and contents can be given back
    private static Leaning BufferLean(uint buffer, long size, uint flags, uint usage)
    {
        if (!Lean || _quiet > 0 || !Recording || Writes is null || buffer == 0 || !Assert(size >= 0)) return Leaning.Given;
        if (LeanBuffers.Remove(buffer, out var was)) LeanBytes -= was.Size;
        if (size < (PoolBirth ? LeanPoolBytes : LeanBufferBytes) || (flags & MapBits) != 0 || LeanBuffers.Count >= MaxLean) return Leaning.Given;
        (LeanBuffers[buffer], LeanBytes) = ((size, usage), LeanBytes + size);
        return Assert(LeanBytes >= 0) ? Leaning.Small : Leaning.Given;
    }

    // glBufferStorage flags as glBufferData usage: dynamic if it takes sub-data (GL_DYNAMIC_STORAGE_BIT), else static
    private static uint UsageOf(uint flags) =>
        Assert(flags != uint.MaxValue) && Assert(_quiet >= 0) && (flags & 0x100) != 0 ? 0x88E8u : 0x88E4u;

    private static bool BufferHeld(uint buffer) => _quiet == 0 && LeanBuffer(buffer) && Assert(buffer > 0);

    // A held-back buffer mapped after all (glBufferData leaves it mappable): GL gets its full size back, without the
    // contents Vulkan keeps; logged once. target: the bind target, 0 for a named map
    private static void Unleaned(uint buffer, uint target)
    {
        if (!BufferHeld(buffer) || !LeanBuffers.Remove(buffer, out var held) || !Assert(held.Size > 0)) return;
        LeanBytes -= held.Size;
        var (at, name) = target == 0 ? (TouchAt + 33, buffer) : (TouchAt + 31, target); // glNamedBufferData, glBufferData
        if (Assert(Original[at] != IntPtr.Zero) && Index(at, _entries.Length))
            ((delegate* unmanaged<uint, nint, nint, uint, void>)Original[at])(name, (nint)held.Size, 0, held.Usage);
        LeanMapped++;
    }

    // Maps of held-back buffers (each gave OpenGL the buffer's size back)
    public static long LeanMapped { get; private set; }

    private static void LeanGone(uint name, bool texture)
    {
        if (Leaned == 0 || !Assert(name != uint.MaxValue)) return;
        if (texture && LeanTextures.Remove(name, out var bytes)) LeanBytes -= bytes;
        if (!texture && LeanBuffers.Remove(name, out var held)) LeanBytes -= held.Size;
        _ = Assert(LeanBytes >= 0);
    }

    // What lean holds back, for Vulkan to give back (VulkanRenderer.Strands)
    public static uint[] LeanTextureNames() =>
        Assert(LeanTextures.Count <= MaxLean) && Assert(LeanBytes >= 0) ? [.. LeanTextures.Keys] : [];

    public static (uint Buffer, long Size, uint Usage)[] LeanBufferList() =>
        Assert(LeanBuffers.Count <= MaxLean) && Assert(LeanBytes >= 0)
            ? [.. LeanBuffers.Select(b => (b.Key, b.Value.Size, b.Value.Usage))]
            : [];

    // OpenGL holds the texture or buffer whole again (its size and contents given back by Komet)
    public static void Restored(uint name, bool texture)
    {
        LeanGone(name, texture);
        _ = Assert(!LeanTextures.ContainsKey(name) || !texture) && Assert(texture || !LeanBuffers.ContainsKey(name));
    }

    // Vulkan-only lean: 2D textures of one level whose last whole upload only Vulkan got (the cloud map every frame); OpenGL's
    // copy is behind until a whole upload reaches it again, and TextureImages.WriteBack gives it Vulkan's at let-go
    private static readonly HashSet<uint> GlBehind = [];

    public static bool Behind(uint texture) => GlBehind.Count > 0 && Assert(GlBehind.Count <= MaxLean) && GlBehind.Contains(texture);

    // A glTexSubImage2D Vulkan took: true when OpenGL may skip it (a whole single level while resting lean), so it is not sent
    private static bool Skipped(uint texture, uint target, bool taken, (int Level, int X, int Y, int Width, int Height) box)
    {
        if (texture == 0 || target != Texture2D || !TextureRecords.TryGetValue(texture, out var t) || t.Levels != 1 ||
            box != (0, 0, 0, t.Width, t.Height)) return false; // part of a texture: OpenGL gets it, behind or not
        if (taken && Lean && Rests && Writes is not null && GlBehind.Count < MaxLean) return GlBehind.Add(texture) || Assert(Behind(texture));
        _ = GlBehind.Remove(texture); // sent whole: OpenGL's copy is current again
        return !Assert(!Behind(texture));
    }

    private static void Unlean()
    {
        (Stranded, LeanBytes, LeanMapped) = (false, 0, 0);
        GlBehind.Clear();
        LeanTextures.Clear();
        LeanBuffers.Clear();
        _ = Assert(Leaned == 0);
    }
}
