using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using unsafe StencilFuncFn = delegate* unmanaged<uint, int, uint, void>;
using unsafe StencilFuncSeparateFn = delegate* unmanaged<uint, uint, int, uint, void>;
using unsafe StencilOpFn = delegate* unmanaged<uint, uint, uint, void>;
using unsafe StencilOpSeparateFn = delegate* unmanaged<uint, uint, uint, uint, void>;
using unsafe StencilMaskFn = delegate* unmanaged<uint, void>;
using unsafe StencilMaskSeparateFn = delegate* unmanaged<uint, uint, void>;
using unsafe ClearStencilFn = delegate* unmanaged<int, void>;
using unsafe DeleteNamesFn = delegate* unmanaged<int, uint*, void>;
using unsafe ClearTexSubImageFn = delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, nint, void>;

namespace Komet.Vulkan;

// Vulkan-only record of mod-only state: stencil (pipeline takes compare and three ops per face; reference and masks are
// dynamic), stencil clear value, deleted renderbuffers, glClearTexSubImage. A renderbuffer stands as its own texture
// under RenderbufferName, so Vulkan images it like a texture (attached, blitted, read back); GL keeps the renderbuffer.
internal static unsafe partial class GlTap
{
    private const int StencilAt = SpecsAt + 13;
    private const uint GlFrontFace = 0x404, GlBackFace = 0x405;

    // A renderbuffer's name in the textures' namespace (above any name OpenGL gives a texture)
    public const uint RenderbufferBase = 1u << 30;

    public static uint RenderbufferName(uint renderbuffer) =>
        Assert(renderbuffer < RenderbufferBase) && Assert(renderbuffer > 0) ? RenderbufferBase + renderbuffer : 0;

    public static bool IsRenderbuffer(int texture) => Assert(texture >= 0) && Assert(RenderbufferBase > 0) && texture >= RenderbufferBase;

    // Per face (front, back): compare function, reference, value mask, fail, depth-fail and pass ops, write mask
    private struct StencilFace
    {
        public uint Func, Mask, Fail, DepthFail, Pass, Write;
        public int Reference;
    }

    private static readonly StencilFace[] Stencils = new StencilFace[2];
    private static int _clearStencil;

    // The dynamic part a draw sets: reference, compare mask and write mask of each face
    public static ((uint Reference, uint Compare, uint Write) Front, (uint Reference, uint Compare, uint Write) Back) StencilValues =>
        Assert(Stencils.Length == 2) && Assert(_quiet >= 0)
            ? (((uint)Stencils[0].Reference & 0xFF, Stencils[0].Mask & 0xFF, Stencils[0].Write & 0xFF),
                ((uint)Stencils[1].Reference & 0xFF, Stencils[1].Mask & 0xFF, Stencils[1].Write & 0xFF))
            : default;

    private static Entry[] StencilEntries() =>
        Assert(StencilAt + 9 <= RecordCount) && Assert(Stencils.Length == 2) ? StencilTaps() : [];

    private static Entry[] StencilTaps() =>
    [
        new("glStencilFunc", (nint)(StencilFuncFn)(&StencilFunc), Group.Records),
        new("glStencilFuncSeparate", (nint)(StencilFuncSeparateFn)(&StencilFuncSeparate), Group.Records),
        new("glStencilOp", (nint)(StencilOpFn)(&StencilOp), Group.Records),
        new("glStencilOpSeparate", (nint)(StencilOpSeparateFn)(&StencilOpSeparate), Group.Records),
        new("glStencilMask", (nint)(StencilMaskFn)(&StencilMask), Group.Records),
        new("glStencilMaskSeparate", (nint)(StencilMaskSeparateFn)(&StencilMaskSeparate), Group.Records),
        new("glClearStencil", (nint)(ClearStencilFn)(&ClearStencil), Group.Records),
        new("glDeleteRenderbuffers", (nint)(DeleteNamesFn)(&DeleteRenderbuffers), Group.Records),
        new("glClearTexSubImage", (nint)(ClearTexSubImageFn)(&ClearTexSubImage), Group.Records)
    ];

    // The faces a GL_FRONT, GL_BACK or GL_FRONT_AND_BACK names: bit 0 the front, bit 1 the back
    private static int Faces(uint face) =>
        Assert(face > 0) && Assert(Stencils.Length == 2) ? face switch { GlFrontFace => 1, GlBackFace => 2, _ => 3 } : 0;

    [UnmanagedCallersOnly]
    private static void StencilFunc(uint func, int reference, uint mask)
    {
        if (Kept(StencilAt) && Assert(Recording) && Assert(Stencils.Length == 2)) ((StencilFuncFn)Original[RecordsAt + StencilAt])(func, reference, mask);
        StencilSet(GlFrontAndBack, func, reference, mask);
    }

    [UnmanagedCallersOnly]
    private static void StencilFuncSeparate(uint face, uint func, int reference, uint mask)
    {
        if (Kept(StencilAt + 1) && Assert(Recording) && Assert(face > 0))
            ((StencilFuncSeparateFn)Original[RecordsAt + StencilAt + 1])(face, func, reference, mask);
        StencilSet(face, func, reference, mask);
    }

    private static void StencilSet(uint face, uint func, int reference, uint mask)
    {
        if (!Assert(face > 0) || func is < 0x200 or > 0x207) return;
        var faces = Faces(face);
        for (var i = 0; i < 2; i++)
            if ((faces & (1 << i)) != 0)
                (Stencils[i].Func, Stencils[i].Reference, Stencils[i].Mask) = (func, reference, mask);
        Version++;
        _ = Assert(Stencils.Length == 2);
    }

    [UnmanagedCallersOnly]
    private static void StencilOp(uint fail, uint depthFail, uint pass)
    {
        if (Kept(StencilAt + 2) && Assert(Recording) && Assert(fail != uint.MaxValue)) ((StencilOpFn)Original[RecordsAt + StencilAt + 2])(fail, depthFail, pass);
        StencilOps(GlFrontAndBack, (fail, depthFail, pass));
    }

    [UnmanagedCallersOnly]
    private static void StencilOpSeparate(uint face, uint fail, uint depthFail, uint pass)
    {
        if (Kept(StencilAt + 3) && Assert(Recording) && Assert(face > 0))
            ((StencilOpSeparateFn)Original[RecordsAt + StencilAt + 3])(face, fail, depthFail, pass);
        StencilOps(face, (fail, depthFail, pass));
    }

    private static void StencilOps(uint face, (uint Fail, uint DepthFail, uint Pass) ops)
    {
        if (!Assert(face > 0) || !Assert(Stencils.Length == 2)) return;
        var faces = Faces(face);
        for (var i = 0; i < 2; i++)
            if ((faces & (1 << i)) != 0)
                (Stencils[i].Fail, Stencils[i].DepthFail, Stencils[i].Pass) = ops;
        Version++;
    }

    [UnmanagedCallersOnly]
    private static void StencilMask(uint mask)
    {
        if (Kept(StencilAt + 4) && Assert(Recording) && Assert(Stencils.Length == 2)) ((StencilMaskFn)Original[RecordsAt + StencilAt + 4])(mask);
        StencilWrites(GlFrontAndBack, mask);
    }

    [UnmanagedCallersOnly]
    private static void StencilMaskSeparate(uint face, uint mask)
    {
        if (Kept(StencilAt + 5) && Assert(Recording) && Assert(face > 0)) ((StencilMaskSeparateFn)Original[RecordsAt + StencilAt + 5])(face, mask);
        StencilWrites(face, mask);
    }

    private static void StencilWrites(uint face, uint mask)
    {
        if (!Assert(face > 0) || !Assert(Stencils.Length == 2)) return;
        var faces = Faces(face);
        for (var i = 0; i < 2; i++)
            if ((faces & (1 << i)) != 0)
                Stencils[i].Write = mask;
        Version++;
    }

    [UnmanagedCallersOnly]
    private static void ClearStencil(int value)
    {
        if (Kept(StencilAt + 6) && Assert(Recording) && Assert(Stencils.Length == 2)) ((ClearStencilFn)Original[RecordsAt + StencilAt + 6])(value);
        _clearStencil = value;
    }

    [UnmanagedCallersOnly]
    private static void DeleteRenderbuffers(int count, uint* names)
    {
        for (var i = 0; names != null && i < Math.Min(count, MaxDeleted); i++)
            if (names[i] is > 0 and < RenderbufferBase)
                Heard(static n => RenderbufferGone(n), names[i]);
        if (Kept(StencilAt + 7) && Assert(Recording) && Assert(count >= 0))
            ((DeleteNamesFn)Original[RecordsAt + StencilAt + 7])(count, names);
    }

    // Its image goes as a deleted texture's does
    private static void RenderbufferGone(uint renderbuffer)
    {
        var name = RenderbufferName(renderbuffer);
        _ = Assert(name > 0) && RenderbufferRecords.Remove(renderbuffer) && Assert(RenderbufferRecords.Count <= MaxRenderbuffers);
        _ = TextureRecords.Remove(name);
        if (Deleting is { } deleting) deleting(name);
        if (_renderbuffer == renderbuffer) _renderbuffer = 0;
    }

    // Vulkan-only: a clear Vulkan made (TextureClearing) is not GL's to make; one Vulkan left GL makes unless it rests
    [UnmanagedCallersOnly]
    private static void ClearTexSubImage(uint texture, int level, int x, int y, int z, int width, int height, int depth,
        uint format, uint type, nint data)
    {
        var taken = TextureClearing is { } clearing && _quiet == 0 &&
                    Asked(clearing, new TextureClear(texture, level, (x, y, z, width, height, depth), (format, type), data), false);
        if (taken) return;
        Touched(Touch.ClearTexture, texture);
        if (!Rests) Changed(texture);
        if (!Rests && Kept(StencilAt + 8) && Assert(Recording) && Assert(level >= 0))
            ((ClearTexSubImageFn)Original[RecordsAt + StencilAt + 8])(texture, level, x, y, z, width, height, depth, format,
                type, data);
    }

    // glClearTexImage (Box Width -1: whole level) or glClearTexSubImage: level/box set to data's value (null: 0)
    public readonly record struct TextureClear(uint Name, int Level, (int X, int Y, int Z, int Width, int Height, int Depth) Box,
        (uint Format, uint Type) Data, nint Value);

    public static System.Func<TextureClear, bool>? TextureClearing { get; set; }

    // OpenGL's stencil state as the record starts: asked once
    private static void SeedStencil()
    {
        for (var i = 0; i < 2; i++)
        {
            var back = i == 1;
            Stencils[i] = new StencilFace
            {
                Func = (uint)GL.GetInteger(back ? GetPName.StencilBackFunc : GetPName.StencilFunc),
                Reference = GL.GetInteger(back ? GetPName.StencilBackRef : GetPName.StencilRef),
                Mask = (uint)GL.GetInteger(back ? GetPName.StencilBackValueMask : GetPName.StencilValueMask),
                Fail = (uint)GL.GetInteger(back ? GetPName.StencilBackFail : GetPName.StencilFail),
                DepthFail = (uint)GL.GetInteger(back ? GetPName.StencilBackPassDepthFail : GetPName.StencilPassDepthFail),
                Pass = (uint)GL.GetInteger(back ? GetPName.StencilBackPassDepthPass : GetPName.StencilPassDepthPass),
                Write = (uint)GL.GetInteger(back ? GetPName.StencilBackWritemask : GetPName.StencilWritemask)
            };
        }

        _clearStencil = GL.GetInteger(GetPName.StencilClearValue);
        _ = Assert(Stencils.Length == 2) && Assert(Version >= 0);
    }

    // Compare and face ops, Vulkan numbering: front bits 0-11 (compare 0-2, fail 3-5, zfail 6-8, pass 9-11), back 12-23
    private static uint StencilWord()
    {
        if ((_caps & Caps.Stencil) == 0 || !Assert(Stencils.Length == 2)) return 0;
        var word = 0u;
        for (var i = 0; i < 2; i++)
        {
            var s = Stencils[i];
            var face = ((s.Func - 0x200) & 7) | (StencilOperation(s.Fail) << 3) | (StencilOperation(s.DepthFail) << 6) |
                       (StencilOperation(s.Pass) << 9);
            word |= face << (12 * i);
        }

        return Assert(word < 1u << 24) ? word : 0;
    }

    // GL_KEEP, GL_ZERO, GL_REPLACE, GL_INCR, GL_DECR, GL_INVERT, GL_INCR_WRAP, GL_DECR_WRAP as VkStencilOp
    private static uint StencilOperation(uint gl) => Assert(gl != uint.MaxValue) && Assert(Stencils.Length == 2)
        ? gl switch { 0 => 1u, 0x1E01 => 2u, 0x1E02 => 3u, 0x1E03 => 4u, 0x150A => 5u, 0x8507 => 6u, 0x8508 => 7u, _ => 0u }
        : 0;
}
