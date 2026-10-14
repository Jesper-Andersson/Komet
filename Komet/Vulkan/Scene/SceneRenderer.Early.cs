using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// The engine clears window and primary framebuffer between frames: such a clear is kept as Vulkan's first work in the
// next frame's first segment; if the scene goes off first, OpenGL clears after all (FlushEarly).
internal sealed partial class SceneRenderer
{
    private const int MaxEarly = 8;
    private const uint ColorMaskBits = 0xFu << 22;

    private readonly List<Early> _early = [];
    private readonly Early[] _kept = new Early[MaxEarly]; // Opened's: a segment Recorded opens runs Opened again

    private sealed record Early(int Fbo, GlTap.ClearCall Call, bool Colors, bool Depth);

    // Frame end to the next frame's stages: clears may be kept for the next segment
    public bool Between { get; set; }

    public long EarlyClears { get; private set; }

    public void Opened()
    {
        Mirrors.Opened();
        _terrain.PoolsOpened();
        if (_early.Count == 0 || !Assert(_early.Count <= MaxEarly)) return;
        var count = _early.Count;
        _early.CopyTo(_kept);
        _early.Clear();
        var failed = 0;
        for (var i = 0; i < Math.Min(count, MaxEarly); i++)
            if (failed > 0 || !Recorded(_kept[i]))
            {
                _early.Add(_kept[i]); // and every one after it, in order
                failed++;
            }

        FlushEarly(); // those it could not record: OpenGL's after all
    }

    // True when OpenGL must not run the clear
    private bool Kept(GlTap.ClearCall call)
    {
        if (!Between || !Assert(call.Into >= -1)) return false;
        var fbo = call.Into >= 0 ? call.Into : GlTap.DrawFramebuffer;
        var state = GlTap.State;
        var depth = (call.Mask & GlTap.ClearDepthMask) != 0 && state.DepthWrite;
        var colors = (call.Mask & GlTap.ClearColorMask) != 0;
        var plain = fbo > 0 && _terrain.Ours(fbo) && (state.Caps & (uint)GlTap.Caps.Scissor) == 0 &&
                    (call.Mask & ~(GlTap.ClearDepthMask | GlTap.ClearColorMask)) == 0 &&
                    _early.Count < MaxEarly;
        for (var i = 0; plain && colors && i < GlTap.MaxBuffers; i++)
            plain = (state.Blend[i] & ColorMaskBits) == ColorMaskBits;
        if (!plain) return false; // the scene's own clear, in its segment

        if (depth || colors) _early.Add(new Early(fbo, call, colors, depth));
        _ = Assert(fbo > 0);
        return Assert(_early.Count <= MaxEarly); // a clear OpenGL would write nothing with is done too
    }

    // Into the framebuffer's current images: Start may have moved them
    private bool Recorded(Early early)
    {
        if (!NotNull(early) || !Assert(early.Fbo > 0)) return false;
        var target = _terrain.Target(GlTap.State, out _, early.Fbo, !early.Depth, foreign: true);
        if (target is null) return false;
        Span<Vk.ClearAttachment> attachments = stackalloc Vk.ClearAttachment[GlTap.MaxBuffers + 1];
        var count = Attachments(attachments, target, early.Call, (early.Colors, early.Depth, false), null);
        if (count == 0) return true;
        _sampled.Clear();
        if (!_terrain.Segment(target, _sampled, out _)) return false;
        Cleared(target, attachments[..count], false);
        EarlyClears++;
        return true;
    }

    // OpenGL touches none of the kept clears' images: it rests
    public void Touching(GlTap.Touch kind, uint id)
    {
        if (Assert(kind <= GlTap.Touch.Mipmap) && Assert(id < uint.MaxValue)) Untaken(kind);
    }

    // As the engine called them: no scissor, all color components and depth written
    public void FlushEarly()
    {
        if (_early.Count == 0 || !Assert(_early.Count <= MaxEarly)) return;
        var state = GlTap.State;
        using (GlTap.Quietly())
        {
            if ((state.Caps & (uint)GlTap.Caps.Scissor) != 0) GL.Disable(EnableCap.ScissorTest);
            GL.ColorMask(true, true, true, true);
            GL.DepthMask(true);
            foreach (var early in _early.Bounded(MaxEarly)) Cleared(early);
            if ((state.Caps & (uint)GlTap.Caps.Scissor) != 0) GL.Enable(EnableCap.ScissorTest);
            for (var i = 0; i < GlTap.MaxBuffers; i++)
            {
                var mask = (state.Blend[i] & ColorMaskBits) >> 22;
                GL.ColorMask(i, (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
            }

            GL.DepthMask(state.DepthWrite);
        }

        _early.Clear();
    }

    private static void Cleared(Early early)
    {
        if (!NotNull(early) || !Assert(early.Fbo > 0)) return;
        var call = early.Call;
        var buffers = GlTap.DrawBuffersOf(early.Fbo);
        Span<float> color = [call.Color.R, call.Color.G, call.Color.B, call.Color.A];
        for (var i = 0; early.Colors && i < Math.Min(buffers.Length, GlTap.MaxBuffers); i++)
            if (buffers[i] >= 0 && (call.Output < 0 || call.Output == i))
                GL.ClearNamedFramebuffer(early.Fbo, ClearBuffer.Color, i, ref color[0]);
        var depth = (float)call.Depth;
        if (early.Depth) GL.ClearNamedFramebuffer(early.Fbo, ClearBuffer.Depth, 0, ref depth);
    }

    private bool _asked; // the call OpenGL is about to run was judged and left (Leave), so it is counted already

    private void Waited()
    {
        _asked = !GlTap.LaterRange; // a later range of a multi-draw: no Touched comes to take it
        _terrain.MissWaiting();
        _ = Assert(_asked || GlTap.LaterRange);
    }

    // GPU work no scene judge saw (multi-draw/indirect, integer clears, window blits, compute) is missing.
    // Copies and texture clears are judged as they run (TerrainRenderer.Copied, TextureCleared).
    private void Untaken(GlTap.Touch kind)
    {
        var asked = _asked;
        _asked = false;
        if (asked || !Framing || !Assert(!_asked)) return;
        var what = kind switch
        {
            GlTap.Touch.Draw => "a multi-draw or an indirect draw",
            GlTap.Touch.Clear or GlTap.Touch.ClearNamed => "a clear of integers",
            GlTap.Touch.Blit => "a blit from or into the window itself",
            GlTap.Touch.Compute => "a compute dispatch",
            _ => ""
        };
        if (what.Length > 0) _terrain.Miss(what);
        _ = Assert(!_asked);
    }
}
