using System.Numerics;
using Komet.Rendering;
using Vintagestory.API.MathTools;

namespace Komet.Vulkan;

// Vulkan-only's far cache (VulkanRenderer.FarCaching): the terrain past FarRadius is drawn into a layer of its own only now
// and then (OcclusionCulling.FarPass, through Redirect) and laid into the frame at the opaque pass's end (SceneRenderer.
// FarLaid). A refresh comes every FarFrames frames, after a step of FarMoved, or with a new lens or size: far chunks new
// since show at most FarFrames frames late, near ones never wait (the camera's pass draws them).
internal sealed partial class TerrainRenderer
{
    public const double FarRadius = 384;
    private const int FarFrames = 12;
    private const double FarMoved = 1, FarKeep = FarRadius - 48;

    // What the layer holds: the view matrix it was drawn with (column-major), the eye, the lens and the cut's planes
    private sealed record FarView(double[] View, Vec3d Eye, Vector4 Optics, double[] Planes, long At);

    private VulkanFrame.Target? _far, _into;
    private FarView? _farView;
    private OcclusionCulling.FarCut? _farCut;
    private (double[] View, double[] Projection, Vec3d Eye)? _farCamera;

    // Reused each frame (a refresh copies what the layer keeps): the cut, and the camera FarBegin saw
    private OcclusionCulling.FarCut? _farCutKept;
    private readonly double[] _farTurned = new double[16], _farProjection = new double[16];
    private readonly Vec3d _farEye = new();
    private readonly Retired<SharedImage> _farRetired = new(16, static image => image.Dispose());

    public long FarRefreshes { get; private set; }
    public long FarLays { get; private set; }

    // The opaque pass begins: the cut its culling splits by, and a cleared layer when this frame refreshes it
    public void FarBegin(float[] view, double[] projection, Vec3d eye, System.Func<VulkanFrame.Target, bool> ready)
    {
        (OcclusionCulling.FarCache, _farCut, _farCamera, _into) = (null, null, null, null);
        if (!Frame.Open || !NotNull(view) || !NotNull(projection) || eye is null || view.Length < 16 ||
            projection.Length < 16) return;
        if (Primary() is not { } primary || !ready(primary) || FarLayer(primary) is not { } far) return;
        var lens = Lens(projection);
        var known = _farView;
        var refresh = known is null || Frame.Number - known.At >= FarFrames || known.Eye.DistanceTo(eye) >= FarMoved ||
                      known.Optics != lens;
        if (refresh && !FarCleared(far)) return;
        var cut = (_farCutKept ??= new OcclusionCulling.FarCut(FarRadius * FarRadius, Redirect)).Begin(refresh);
        if (!refresh && known is not null)
        {
            known.Planes.CopyTo(cut.View, 0);
            cut.Planned = true;
        }

        for (var i = 0; i < 16; i++) (_farTurned[i], _farProjection[i]) = (view[i], projection[i]);
        _farCamera = (_farTurned, _farProjection, _farEye.Set(eye));
        (OcclusionCulling.FarCache, _farCut) = (cut, cut);
        _ = Assert(cut.Refresh == refresh) && Assert(_far is not null);
    }

    // The opaque pass ends: a refresh's view kept, the layer laid into the frame
    public void FarEnd(System.Func<VulkanFrame.Target, VulkanFrame.Target, SceneRenderer.FarWarp, bool> lay)
    {
        var (cut, camera) = (_farCut, _farCamera);
        (OcclusionCulling.FarCache, _farCut, _farCamera, _into, _context) = (null, null, null, null, null);
        if (cut is null || camera is not var (view, projection, eye) || _far is not { } far || !NotNull(lay)) return;
        if (cut.Refresh)
        {
            _farView = cut.Planned ? new FarView([.. view], eye.Clone(), Lens(projection), [.. cut.View], Frame.Number) : null;
            if (_farView is not null && ++FarRefreshes % 2048 == 0)
                _logger?.Notification("Komet: Vulkan's far cache laid into {0} frames, drawn anew {1} times", FarLays, FarRefreshes);
        }

        if (!cut.Planned || _farView is not { } cached || !Frame.Open || Primary() is not { } primary) return;
        if (lay(primary, far, Warp(cached, view, eye, projection))) FarLays++;
        else _farView = null; // the next frame draws it anew
        _ = Assert(FarLays <= Frame.Number) && Assert(FarRefreshes <= Frame.Number);
    }

    // OcclusionCulling's far pass: pool draws into the layer (true) and back
    private void Redirect(bool on)
    {
        _into = on ? _far : null;
        _context = null;
        _ = Assert(!on || _into is not null) && Assert(Frame.Number >= 0);
    }

    private VulkanFrame.Target? Primary()
    {
        if (Targets.Framebuffers is not { Count: > 0 } framebuffers || framebuffers[0] is not { FboId: > 0 } primary) return null;
        var target = Target(GlTap.State, out _, primary.FboId, false);
        return target is { Depth: not null, Width: > 0 } && Assert(target.Height > 0) ? target : null;
    }

    // The layer, alike to the primary target (formats, size), made anew when that changes
    private VulkanFrame.Target? FarLayer(VulkanFrame.Target primary)
    {
        if (!NotNull(primary) || primary.Depth is not { } depth) return null;
        if (_far is { } made && made.Width == primary.Width && made.Height == primary.Height &&
            made.Colors.Length == primary.Colors.Length && made.Depth!.Format == depth.Format &&
            Alike(made.Colors, primary.Colors)) return made;
        Unfar();
        var size = (primary.Width, primary.Height, 1, 1);
        var colors = new SharedImage?[primary.Colors.Length];
        var layer = Rested(SharedImage.Private(Device, size, depth.Format, Vk.DepthAttachment | Vk.Sampled | Vk.TransferDst, out _));
        for (var i = 0; layer is not null && i < Math.Min(colors.Length, GlTap.MaxBuffers); i++)
            if (primary.Colors[i] is { } color &&
                (colors[i] = Rested(SharedImage.Private(Device, size, color.Format, Vk.ColorAttachment | Vk.Sampled, out _))) is null)
                layer = null;
        if (layer is null || colors[0] is null)
        {
            layer?.Dispose();
            foreach (var color in colors.Bounded(GlTap.MaxBuffers)) color?.Dispose();
            return null;
        }

        _far = new VulkanFrame.Target(++_targetKeys, colors, layer, false, primary.Width, primary.Height);
        _logger?.Notification("Komet: Vulkan caches the terrain past {0} blocks ({1}x{2})", FarRadius, primary.Width, primary.Height);
        return _far;
    }

    private static bool Alike(SharedImage?[] made, SharedImage?[] wanted)
    {
        if (!Assert(made.Length == wanted.Length) || !Assert(made.Length <= GlTap.MaxBuffers)) return false;
        for (var i = 0; i < Math.Min(made.Length, GlTap.MaxBuffers); i++)
            if ((made[i] is null) != (wanted[i] is null) || (made[i] is { } m && m.Format != wanted[i]!.Format))
                return false;
        return true;
    }

    private SharedImage? Rested(SharedImage? image)
    {
        _ = Assert(Frame.Number >= 0) && Assert(Device.Handle != IntPtr.Zero);
        if (image is null || image.Rest(Device, Vk.LayoutUndefined, Vk.LayoutShaderRead)) return image;
        image.Dispose();
        return null;
    }

    // Its depth at the far plane: what nothing was drawn into is left out
    private bool FarCleared(VulkanFrame.Target far)
    {
        if (!NotNull(far) || far.Depth is not { } depth || !Assert(Frame.Open)) return false;
        Hold(depth);
        return Frame.ClearImage(depth, new Vk.ColorRange { Aspect = Vk.AspectDepth, Levels = 1, Layers = 1 }, default,
            new Vk.DepthStencilValue { Depth = 1 });
    }

    // The layer's images go once no frame in flight reads them (at once with the device idle: dispose)
    private void Unfar(bool idle = false)
    {
        (_farView, _into) = (null, null);
        if (_far is { } far)
        {
            foreach (var image in far.Colors.Append(far.Depth).Bounded(GlTap.MaxBuffers + 1))
                if (image is not null && (idle || !_farRetired.Add(image, Frame.Number)))
                    image.Dispose();
        }

        _far = null;
        if (idle) _farRetired.Clear();
        _ = Assert(_into is null) && Assert(_farRetired.Count <= 16);
    }

    private static Vector4 Lens(double[] p) =>
        Assert(p.Length >= 16) ? new Vector4((float)p[0], (float)p[5], (float)p[8], (float)p[9]) : Vector4.Zero;

    // The cached view's positions into this one turn by the product of this rotation and the cached one's transpose, and
    // move by this translation, less the cached one turned, plus the eyes' difference turned. Directions go back transposed.
    private static SceneRenderer.FarWarp Warp(FarView cached, double[] view, Vec3d eye, double[] projection)
    {
        _ = Assert(view.Length >= 16) && Assert(cached.View.Length >= 16);
        var (now, then) = (view, cached.View);
        Span<double> n = stackalloc double[9];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                n[3 * i + j] = now[i] * then[j] + now[4 + i] * then[4 + j] + now[8 + i] * then[8 + j];
        var (dx, dy, dz) = (cached.Eye.X - eye.X, cached.Eye.Y - eye.Y, cached.Eye.Z - eye.Z);
        Span<double> shift = stackalloc double[3];
        for (var i = 0; i < 3; i++)
            shift[i] = now[12 + i] - (n[3 * i] * then[12] + n[3 * i + 1] * then[13] + n[3 * i + 2] * then[14]) +
                       now[i] * dx + now[4 + i] * dy + now[8 + i] * dz;
        var back = new Matrix4x4((float)n[0], (float)n[1], (float)n[2], 0, (float)n[3], (float)n[4], (float)n[5], 0,
            (float)n[6], (float)n[7], (float)n[8], 0, 0, 0, 0, 1);
        return new SceneRenderer.FarWarp(Matrix4x4.Transpose(back), back, Lens(projection),
            new Vector4((float)projection[10], (float)projection[14], (float)FarKeep, 0), new Vector3((float)shift[0], (float)shift[1], (float)shift[2]));
    }
}
