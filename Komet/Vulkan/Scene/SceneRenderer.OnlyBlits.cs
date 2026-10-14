namespace Komet.Vulkan;

// Blits as OpenGL does them: scissor and bounds clip both sides' corners,
// stencil where both hold one; linear scaling with Vulkan's filter; format conversion as vkCmdBlitImage allows; any
// level and layer. A linear depth blit is an OpenGL error that blits nothing, and so here.
internal sealed partial class SceneRenderer
{
    private (int Level, int Layer) _blittedAt; // the first source's: what the blit's size is measured at

    private string OnlyBlitted(GlTap.BlitCall call)
    {
        var (color, depth, stencil) = ((call.Mask & ColorBlit) != 0, (call.Mask & DepthBlit) != 0, (call.Mask & StencilBlit) != 0);
        if (!Assert(call.Read > 0) || !Assert(call.Draw > 0)) return "with no framebuffer";
        if ((!color && !depth && !stencil) || ((depth || stencil) && call.Filter != Nearest)) return ""; // OpenGL blits nothing either
        if (call.Filter is not (Nearest or Linear)) return "with that filter";
        if (color && call.ReadAttachment < 0) return "from no known read buffer";
        _blitted.Clear();
        string why;
        if (color && SourceAt(call.Read, call.ReadAttachment, out why) is null) return why;
        if ((depth || stencil) && SourceAt(call.Read, GlFramebuffer.Depth, out why) is null) return why;
        var target = _terrain.Target(GlTap.State, out why, call.Draw, !depth && !stencil, foreign: true);
        if (target is null) return why;
        var (source, at) = (_blitted[0], _blittedAt);
        var size = source.LevelSize(at.Level);
        if (Clipped(call, size, (target.Width, target.Height)) is not { } region) return ""; // nothing left inside
        var count = 0;
        if (color && OnlyPaired((source, at), target, region, ref count) is { Length: > 0 } c) return c;
        var colors = count;
        var aspect = 0u;
        if ((depth || stencil) && target.Depth is { } into)
        {
            if (into.Format != _blitted[^1].Format || into == _blitted[^1]) return "between depth formats or within one image";
            aspect = into.Format.Aspect & ((depth ? Vk.AspectDepth : 0) | (stencil ? Vk.AspectStencil : 0));
            if (aspect != 0) _pairs[count++] = (_blitted[^1], into);
        }

        if (count == 0) return ""; // nothing attached where OpenGL would write either
        if (!_terrain.Segment(target, _blitted, out why, render: false)) return why;
        return OnlyBlit((colors, count, aspect), region, (call.Filter == Linear, target)) ? "" : "the segment lost an image";
    }

    // Source image with level and layer, added to _blitted
    private SharedImage? SourceAt(int fbo, int attachment, out string why)
    {
        var source = _terrain.ReadAt(fbo, attachment, out var at, out why);
        if (source is null || !Assert(_blitted.Count < 2)) return null;
        if (_blitted.Count == 0) _blittedAt = at;
        _blitted.Add(source);
        return Assert(at.Level >= 0) ? source : null;
    }

    // "" when every written color fits the source
    private string OnlyPaired((SharedImage Image, (int Level, int Layer) At) source, VulkanFrame.Target target,
        Vk.ImageBlit region, ref int count)
    {
        if (!NotNull(source.Image)) return "from nothing";
        for (var i = 0; i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
        {
            if (target.Colors[i] is not { } into) continue;
            var to = i < target.At.Length ? target.At[i] : default;
            if (into == source.Image) return "within one image";
            if (!TerrainRenderer.Converts(source.Image.Format, into.Format))
                return $"between formats {source.Image.Format.Vulkan} and {into.Format.Vulkan}";
            if (source.Image.OpaqueAlpha && !into.OpaqueAlpha) return "from RGB into RGBA";
            _pairs[count] = (source.Image, into);
            _regions[count++] = region with
            {
                Source = new Vk.Layers { Level = (uint)source.At.Level, BaseLayer = (uint)source.At.Layer, Count = 1 },
                Target = new Vk.Layers { Level = (uint)to.Level, BaseLayer = (uint)to.Layer, Count = 1 }
            };
        }

        return Assert(count <= _pairs.Length) ? "" : "into too many images";
    }

    private readonly Vk.ImageBlit[] _regions = new Vk.ImageBlit[GlTap.MaxBuffers + 1];

    // One blit per pair: colors at their attached levels/layers, depth (and stencil: Aspect) at level 0
    private bool OnlyBlit((int Colors, int All, uint Aspect) count, Vk.ImageBlit region,
        (bool Linear, VulkanFrame.Target Target) blit)
    {
        var colors = count.Colors;
        for (var i = 0; i < Math.Min(count.All, GlTap.MaxBuffers + 1); i++)
            if (!_terrain.Frame.Blit(_pairs.AsSpan(i, 1), i < colors ? _regions[i] : region, blit.Linear && i < colors,
                    i < colors ? 0 : count.Aspect))
                return false;
        _terrain.Drawn(blit.Target);
        _terrain.Unprepared();
        Blits++;
        return Assert(count.All > 0) && Assert(colors <= count.All);
    }

    // Clipped as OpenGL: destination to target and scissor, source to its image, losses mirrored; null when empty
    private static Vk.ImageBlit? Clipped(GlTap.BlitCall call, (int Width, int Height) source, (int Width, int Height) target)
    {
        var scissor = (GlTap.State.Caps & (uint)GlTap.Caps.Scissor) != 0;
        if (!Assert(source.Width > 0 && source.Height > 0) || !Assert(target.Width > 0 && target.Height > 0)) return null;
        var (x, y, width, height) = scissor ? GlTap.Scissor : (0, 0, target.Width, target.Height);
        var (from, to) = (call.From, call.To);
        if (from.X0 == from.X1 || from.Y0 == from.Y1 || to.X0 == to.X1 || to.Y0 == to.Y1) return null;
        var across = Along((from.X0, from.X1), (to.X0, to.X1), (0, source.Width), (Math.Max(x, 0), Math.Min(x + width, target.Width)));
        var up = Along((from.Y0, from.Y1), (to.Y0, to.Y1), (0, source.Height), (Math.Max(y, 0), Math.Min(y + height, target.Height)));
        if (across is not var (fx0, fx1, tx0, tx1) || up is not var (fy0, fy1, ty0, ty1)) return null;
        return new Vk.ImageBlit
        {
            SourceX0 = fx0, SourceY0 = fy0, SourceX1 = fx1, SourceY1 = fy1, SourceZ1 = 1, TargetX0 = tx0, TargetY0 = ty0,
            TargetX1 = tx1, TargetY1 = ty1, TargetZ1 = 1
        };
    }

    // One axis: u 0..1 runs both sides' corners at once, narrowed to where both lie within bounds
    internal static (int From0, int From1, int To0, int To1)? Along((int A, int B) from, (int A, int B) to, (int Lo, int Hi) fromIn,
        (int Lo, int Hi) toIn)
    {
        var u = Within(to, toIn, Within(from, fromIn, (0, 1)));
        if (u.Lo >= u.Hi || !Assert(from.A != from.B) || !Assert(to.A != to.B)) return null;
        var clipped = (At(from, u.Lo), At(from, u.Hi), At(to, u.Lo), At(to, u.Hi));
        return clipped.Item1 == clipped.Item2 || clipped.Item3 == clipped.Item4 ? null : clipped;
    }

    private static int At((int A, int B) corners, double u) =>
        Finite(u) && Assert(u is >= 0 and <= 1) ? (int)Math.Round(corners.A + (u * (corners.B - corners.A))) : corners.A;

    private static (double Lo, double Hi) Within((int A, int B) corners, (int Lo, int Hi) bounds, (double Lo, double Hi) u)
    {
        var span = (double)(corners.B - corners.A);
        if (!Assert(span != 0) || !Finite(u.Lo)) return (1, 0);
        var (p, q) = ((bounds.Lo - corners.A) / span, (bounds.Hi - corners.A) / span);
        return (Math.Max(u.Lo, Math.Min(p, q)), Math.Min(u.Hi, Math.Max(p, q)));
    }
}
