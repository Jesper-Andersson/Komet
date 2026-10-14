using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Blits go to Blitting first, which may take them over (GL skips them). Each FBO's read buffer is followed via
// glReadBuffer, asked once for one set before tapping (while bound for reading). A 2D texture's mipmaps may be made in
// its copy too (Writes.TextureMipmapped), else it changed.
internal static unsafe partial class GlTap
{
    public const int ReadNone = -1, ReadUnknown = -2;
    private const int MaxReads = 256;

    private static readonly Dictionary<uint, int> Reads = [];

    // Read: read framebuffer and its read buffer's attachment (or ReadNone, ReadUnknown); From, To as GL takes them
    public readonly record struct BlitCall(int Read, int ReadAttachment, int Draw, (int X0, int Y0, int X1, int Y1) From,
        (int X0, int Y0, int X1, int Y1) To, uint Mask, uint Filter);

    // Only blits between framebuffer objects
    public static System.Func<BlitCall, bool>? Blitting { get; set; }

    // glReadPixels (Name 0: read framebuffer, Framed, read buffer) or glGetTexImage of 2D Name's level, per GL_PACK_*
    public readonly record struct ReadCall(int Fbo, int Attachment, uint Name, int Level,
        (int X, int Y, int Width, int Height) Rect, (uint Format, uint Type) Data, nint Pixels);

    // Vulkan-only answers a read from its own images (OpenGL skips it then)
    public static System.Func<ReadCall, bool>? Reading { get; set; }

    // Vulkan-only runs (VulkanRenderer.GlIdle): GL rests. Draws, clears, blits, copies or dispatches Vulkan did not
    // take go missing rather than run by GL into unseen objects. Uploads, names, state, unanswered reads and Komet's
    // quiet work still reach GL ("shadow GL") so it takes over at once when Vulkan-only lets go
    public static bool Resting
    {
        get => _resting;
        set
        {
            if (!value && _resting) SendUnsent(); // GL draws again: the uniforms it was not sent first
            _resting = value;
        }
    }

    private static bool _resting;

    // Stranded, the GUI's stage is OpenGL's (VulkanRenderer.Overlaid): the way out of the world
    private static bool Rests => (Resting || (Stranded && !GuiStage)) && _quiet == 0 && Assert(_quiet >= 0) && Assert(Recording);

    // VulkanQuietUniforms: resting GL is not sent uniforms the mirror holds (Withheld), only once it draws again
    public static bool QuietUniforms { get; set; } = true;

    private static bool MutedUniforms => QuietUniforms && _resting && _quiet == 0 && Assert(_quiet >= 0) && Assert(Recording);

    private static bool ReadAnswered(ReadCall call) =>
        _quiet == 0 && Reading is { } reading && Assert(call.Level >= 0) && Assert(_table is not null) &&
        Asked(reading, call, false);

    private static bool Blitted(uint read, uint draw, ReadOnlySpan<int> corners, uint mask, uint filter)
    {
        if (_quiet != 0 || Blitting is not { } blitting || read == 0 || draw == 0 || !Assert(corners.Length == 8) ||
            !Assert(_table is not null)) return false;
        return Asked(blitting, new BlitCall((int)read, ReadBufferOf(read), (int)draw, (corners[0], corners[1], corners[2], corners[3]),
            (corners[4], corners[5], corners[6], corners[7]), mask, filter), false);
    }

    public static int ReadBufferOf(uint framebuffer)
    {
        if (framebuffer == 0 || !Assert(Reads.Count <= MaxReads)) return ReadUnknown;
        if (Reads.TryGetValue(framebuffer, out var known)) return known;
        if (Defaulted(framebuffer)) return 0; // GL_COLOR_ATTACHMENT0
        if (framebuffer != _readFramebuffer || Reads.Count >= MaxReads) return ReadUnknown;
        return Reads[framebuffer] = ReadOf((uint)GL.GetInteger(GetPName.ReadBuffer));
    }

    private static int ReadOf(uint mode) => mode switch
    {
        >= GlColor0 and < GlColor0 + 16 => (int)(mode - GlColor0),
        0 => ReadNone,
        _ => Assert(mode != uint.MaxValue) ? ReadUnknown : ReadNone
    };

    // A texture parameter set: texture 0's too, whose sampling is then no longer default. tracked: the record follows
    // the texture (a bind-target call of an unfollowed target names another)
    private static void Tuned(uint texture, uint name, float value, bool tracked = true)
    {
        if (Recording && tracked && texture != 0) TextureTuned(texture, name, value);
        if (texture == 0 && Assert(name > 0)) DefaultTextureTouched = true;
        else if (Writes is { } writes) Heard(static s => s.writes.TextureTuned(s.texture, s.name, s.value), (writes, texture, name, value));
    }

    // Komet's own framebuffers (quiet) are no blit's source: their read buffers are not followed
    private static void ReadSet(uint framebuffer, uint mode)
    {
        if (framebuffer == 0 || _quiet > 0) return;
        System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(Reads, framebuffer, out _) = ReadOf(mode);
        if (Reads.Count > MaxReads) Reads.Clear();
    }
}
