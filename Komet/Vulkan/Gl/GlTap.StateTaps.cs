using System.Runtime.InteropServices;

namespace Komet.Vulkan;

internal static unsafe partial class GlTap
{
    [UnmanagedCallersOnly]
    private static void Enable(uint cap)
    {
        Toggle(cap, true);
        if (Assert(Original[EnableAt] != IntPtr.Zero) && Index(EnableAt, _entries.Length))
            ((delegate* unmanaged<uint, void>)Original[EnableAt])(cap);
    }

    [UnmanagedCallersOnly]
    private static void Disable(uint cap)
    {
        Toggle(cap, false);
        if (Assert(Original[DisableAt] != IntPtr.Zero) && Index(DisableAt, _entries.Length))
            ((delegate* unmanaged<uint, void>)Original[DisableAt])(cap);
    }

    [UnmanagedCallersOnly]
    private static void Enablei(uint cap, uint index)
    {
        if (cap == GlBlend) SetBlending((int)index, true);
        if (Assert(Original[EnableiAt] != IntPtr.Zero) && Index(EnableiAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, void>)Original[EnableiAt])(cap, index);
    }

    [UnmanagedCallersOnly]
    private static void Disablei(uint cap, uint index)
    {
        if (cap == GlBlend) SetBlending((int)index, false);
        if (Assert(Original[DisableiAt] != IntPtr.Zero) && Index(DisableiAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, void>)Original[DisableiAt])(cap, index);
    }

    [UnmanagedCallersOnly]
    private static void BlendFunc(uint source, uint target)
    {
        SetFunc(AllBuffers, (source, target, source, target));
        if (Assert(Original[FuncAt] != IntPtr.Zero) && Index(FuncAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, void>)Original[FuncAt])(source, target);
    }

    [UnmanagedCallersOnly]
    private static void BlendFuncSeparate(uint sc, uint dc, uint sa, uint da)
    {
        SetFunc(AllBuffers, (sc, dc, sa, da));
        if (Assert(Original[FuncSeparateAt] != IntPtr.Zero) && Index(FuncSeparateAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, uint, uint, void>)Original[FuncSeparateAt])(sc, dc, sa, da);
    }

    [UnmanagedCallersOnly]
    private static void BlendFunci(uint buffer, uint source, uint target)
    {
        SetFunc((int)buffer, (source, target, source, target));
        if (Assert(Original[FunciAt] != IntPtr.Zero) && Index(FunciAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, uint, void>)Original[FunciAt])(buffer, source, target);
    }

    [UnmanagedCallersOnly]
    private static void BlendFuncSeparatei(uint buffer, uint sc, uint dc, uint sa, uint da)
    {
        SetFunc((int)buffer, (sc, dc, sa, da));
        if (Assert(Original[FuncSeparateiAt] != IntPtr.Zero) && Index(FuncSeparateiAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, uint, uint, uint, void>)Original[FuncSeparateiAt])(buffer, sc, dc, sa,
                da);
    }

    [UnmanagedCallersOnly]
    private static void BlendEquation(uint mode)
    {
        SetEquation(AllBuffers, mode, mode);
        if (Assert(Original[EquationAt] != IntPtr.Zero) && Index(EquationAt, _entries.Length))
            ((delegate* unmanaged<uint, void>)Original[EquationAt])(mode);
    }

    [UnmanagedCallersOnly]
    private static void BlendEquationSeparate(uint color, uint alpha)
    {
        SetEquation(AllBuffers, color, alpha);
        if (Assert(Original[EquationSeparateAt] != IntPtr.Zero) && Index(EquationSeparateAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, void>)Original[EquationSeparateAt])(color, alpha);
    }

    [UnmanagedCallersOnly]
    private static void BlendEquationi(uint buffer, uint mode)
    {
        SetEquation((int)buffer, mode, mode);
        if (Assert(Original[EquationiAt] != IntPtr.Zero) && Index(EquationiAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, void>)Original[EquationiAt])(buffer, mode);
    }

    [UnmanagedCallersOnly]
    private static void BlendEquationSeparatei(uint buffer, uint color, uint alpha)
    {
        SetEquation((int)buffer, color, alpha);
        if (Assert(Original[EquationSeparateiAt] != IntPtr.Zero) && Index(EquationSeparateiAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, uint, void>)Original[EquationSeparateiAt])(buffer, color, alpha);
    }

    [UnmanagedCallersOnly]
    private static void DepthFunc(uint function)
    {
        if (function is >= 0x200 and <= 0x207 && function != _depthFunc) (_depthFunc, Version) = (function, Version + 1);
        if (Assert(Original[DepthFuncAt] != IntPtr.Zero) && Index(DepthFuncAt, _entries.Length))
            ((delegate* unmanaged<uint, void>)Original[DepthFuncAt])(function);
    }

    [UnmanagedCallersOnly]
    private static void DepthMask(byte on)
    {
        if (_depthMask != (on != 0)) (_depthMask, Version) = (on != 0, Version + 1);
        if (Assert(Original[DepthMaskAt] != IntPtr.Zero) && Index(DepthMaskAt, _entries.Length))
            ((delegate* unmanaged<byte, void>)Original[DepthMaskAt])(on);
    }

    [UnmanagedCallersOnly]
    private static void ColorMask(byte r, byte g, byte b, byte a)
    {
        SetMask(AllBuffers, r != 0, g != 0, b != 0, a != 0);
        if (Assert(Original[ColorMaskAt] != IntPtr.Zero) && Index(ColorMaskAt, _entries.Length))
            ((delegate* unmanaged<byte, byte, byte, byte, void>)Original[ColorMaskAt])(r, g, b, a);
    }

    [UnmanagedCallersOnly]
    private static void ColorMaski(uint buffer, byte r, byte g, byte b, byte a)
    {
        SetMask((int)buffer, r != 0, g != 0, b != 0, a != 0);
        if (Assert(Original[ColorMaskiAt] != IntPtr.Zero) && Index(ColorMaskiAt, _entries.Length))
            ((delegate* unmanaged<uint, byte, byte, byte, byte, void>)Original[ColorMaskiAt])(buffer, r, g, b, a);
    }

    [UnmanagedCallersOnly]
    private static void CullFace(uint face)
    {
        if (_cullFace != face) (_cullFace, Version) = (face, Version + 1);
        if (Assert(Original[CullFaceAt] != IntPtr.Zero) && Index(CullFaceAt, _entries.Length))
            ((delegate* unmanaged<uint, void>)Original[CullFaceAt])(face);
    }

    [UnmanagedCallersOnly]
    private static void FrontFace(uint face)
    {
        if (_frontFace != face) (_frontFace, Version) = (face, Version + 1);
        if (Assert(Original[FrontFaceAt] != IntPtr.Zero) && Index(FrontFaceAt, _entries.Length))
            ((delegate* unmanaged<uint, void>)Original[FrontFaceAt])(face);
    }

    [UnmanagedCallersOnly]
    private static void SetViewport(int x, int y, int width, int height)
    {
        if (_viewport != (x, y, width, height)) (_viewport, Version) = ((x, y, width, height), Version + 1);
        if (Assert(Original[ViewportAt] != IntPtr.Zero) && Index(ViewportAt, _entries.Length))
            ((delegate* unmanaged<int, int, int, int, void>)Original[ViewportAt])(x, y, width, height);
    }

    [UnmanagedCallersOnly]
    private static void BindFramebuffer(uint target, uint framebuffer)
    {
        Version++;
        framebuffer = Framed(framebuffer); // the window's stand-in for the window
        if (target is GlFramebuffer or GlDrawFramebuffer) _framebuffer = framebuffer;
        if (target is GlFramebuffer or GlReadFramebuffer) _readFramebuffer = framebuffer;
        if (Assert(Original[BindFramebufferAt] != IntPtr.Zero) && Index(BindFramebufferAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, void>)Original[BindFramebufferAt])(target, framebuffer);
    }

    [UnmanagedCallersOnly]
    private static void PolygonOffset(float factor, float units)
    {
        if ((_offsetFactor, _offsetUnits) != (factor, units))
            (_offsetFactor, _offsetUnits, Version) = (factor, units, Version + 1);
        if (Assert(Original[OffsetAt] != IntPtr.Zero) && Index(OffsetAt, _entries.Length))
            ((delegate* unmanaged<float, float, void>)Original[OffsetAt])(factor, units);
    }

    [UnmanagedCallersOnly]
    private static void DrawBuffers(int count, uint* buffers)
    {
        var windowed = stackalloc uint[16];
        if (_framebuffer != 0 && _framebuffer == Window && buffers != null && count is > 0 and <= 16)
        {
            for (var i = 0; i < Math.Min(count, 16); i++) windowed[i] = Windowed(buffers[i]);
            buffers = windowed;
        }

        if (_framebuffer > 0 && buffers != null && count is > 0 and <= 16)
            SetBuffers(_framebuffer, new ReadOnlySpan<uint>(buffers, count));
        if (Assert(Original[DrawBuffersAt] != IntPtr.Zero) && Index(DrawBuffersAt, _entries.Length))
            ((delegate* unmanaged<int, uint*, void>)Original[DrawBuffersAt])(count, buffers);
    }

    [UnmanagedCallersOnly]
    private static void DrawBuffer(uint buffer)
    {
        if (_framebuffer != 0 && _framebuffer == Window) buffer = Windowed(buffer);
        if (_framebuffer > 0) SetBuffers(_framebuffer, [buffer]);
        if (Assert(Original[DrawBufferAt] != IntPtr.Zero) && Index(DrawBufferAt, _entries.Length))
            ((delegate* unmanaged<uint, void>)Original[DrawBufferAt])(buffer);
    }

    [UnmanagedCallersOnly]
    private static void NamedDrawBuffers(uint framebuffer, int count, uint* buffers)
    {
        var windowed = stackalloc uint[16];
        if (framebuffer == 0 && Window != 0 && buffers != null && count is > 0 and <= 16)
        {
            for (var i = 0; i < Math.Min(count, 16); i++) windowed[i] = Windowed(buffers[i]);
            framebuffer = Window;
            buffers = windowed;
        }

        if (framebuffer > 0 && buffers != null && count is > 0 and <= 16)
            SetBuffers(framebuffer, new ReadOnlySpan<uint>(buffers, count));
        if (Assert(Original[NamedDrawBuffersAt] != IntPtr.Zero) && Index(NamedDrawBuffersAt, _entries.Length))
            ((delegate* unmanaged<uint, int, uint*, void>)Original[NamedDrawBuffersAt])(framebuffer, count, buffers);
    }

    [UnmanagedCallersOnly]
    private static void NamedDrawBuffer(uint framebuffer, uint buffer)
    {
        if (framebuffer == 0 && Window != 0) (framebuffer, buffer) = (Window, Windowed(buffer));
        if (framebuffer > 0) SetBuffers(framebuffer, [buffer]);
        if (Assert(Original[NamedDrawBufferAt] != IntPtr.Zero) && Index(NamedDrawBufferAt, _entries.Length))
            ((delegate* unmanaged<uint, uint, void>)Original[NamedDrawBufferAt])(framebuffer, buffer);
    }

    [UnmanagedCallersOnly]
    private static void DeleteProgram(uint program)
    {
        Version++;
        if (program != 0 && ProgramGone is { } gone) Heard(gone, program);
        if (Recording) _ = ProgramRecords.Remove(program);
        _ = SamplerUnits.Remove(program);
        _ = Programs.Remove(program);
        if (program == _current) (_using, _samplers) = (null, null);
        if (Assert(Original[DeleteProgramAt] != IntPtr.Zero) && Index(DeleteProgramAt, _entries.Length))
            ((delegate* unmanaged<uint, void>)Original[DeleteProgramAt])(program);
    }

    [UnmanagedCallersOnly]
    private static void DeleteFramebuffers(int count, uint* framebuffers)
    {
        Version++;
        for (var i = 0; framebuffers != null && i < Math.Min(count, MaxDeleted); i++)
        {
            _ = Buffers.Remove(framebuffers[i]);
            _ = Outputs.Remove(framebuffers[i]);
            _ = Reads.Remove(framebuffers[i]);
            if (Recording) _ = FramebufferRecords.Remove(framebuffers[i]);
            if (framebuffers[i] != 0 && FramebufferGone is { } gone) Heard(gone, framebuffers[i]);
        }
        if (Assert(Original[DeleteFramebuffersAt] != IntPtr.Zero) && Index(DeleteFramebuffersAt, _entries.Length))
            ((delegate* unmanaged<int, uint*, void>)Original[DeleteFramebuffersAt])(count, framebuffers);
    }

    // Whoever shares a texture about to go lets go first (the name may come back for another texture at once)
    [UnmanagedCallersOnly]
    private static void DeleteTextures(int count, uint* textures)
    {
        (Version, TextureVersion, DeleteVersion) = (Version + 1, TextureVersion + 1, DeleteVersion + 1);
        _ = Assert(count >= 0);
        for (var i = 0; textures != null && Deleting is { } deleting && i < Math.Min(count, MaxDeleted); i++)
            if (textures[i] != 0)
                Heard(deleting, textures[i]);
        for (var i = 0; textures != null && Scene && i < Math.Min(count, MaxDeleted); i++) Unbound(textures[i]);
        for (var i = 0; textures != null && Leaned > 0 && i < Math.Min(count, MaxDeleted); i++) LeanGone(textures[i], true);
        for (var i = 0; textures != null && GlBehind.Count > 0 && i < Math.Min(count, MaxDeleted); i++) _ = GlBehind.Remove(textures[i]);
        if (Assert(Original[DeleteTexturesAt] != IntPtr.Zero) && Index(DeleteTexturesAt, _entries.Length))
            ((delegate* unmanaged<int, uint*, void>)Original[DeleteTexturesAt])(count, textures);
    }

    // Every unit that held a deleted texture holds none
    private static void Unbound(uint texture)
    {
        if (texture == 0 || !Assert(UnitArrays.Length == MaxUnits)) return;
        _ = TextureTargets.Remove(texture);
        if (Recording) _ = TextureRecords.Remove(texture);
        for (var unit = 0; unit < MaxUnits; unit++)
        {
            if (UnitTextures[unit] == texture) UnitTextures[unit] = 0;
            if (UnitArrays[unit] == texture) UnitArrays[unit] = 0;
            if (UnitCubes[unit] == texture) UnitCubes[unit] = 0;
        }

        _ = Assert(!TextureTargets.ContainsKey(texture));
    }
}
