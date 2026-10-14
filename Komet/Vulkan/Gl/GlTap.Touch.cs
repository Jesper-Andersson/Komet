using System.Runtime.InteropServices;
using unsafe DrawArraysFn = delegate* unmanaged<uint, int, int, void>;
using unsafe DrawElementsFn = delegate* unmanaged<uint, int, uint, nint, void>;
using unsafe DrawElementsInstancedFn = delegate* unmanaged<uint, int, uint, nint, int, void>;
using unsafe DrawArraysInstancedFn = delegate* unmanaged<uint, int, int, int, void>;
using unsafe DrawElementsBaseVertexFn = delegate* unmanaged<uint, int, uint, nint, int, void>;
using unsafe DrawElementsInstancedBaseVertexFn = delegate* unmanaged<uint, int, uint, nint, int, int, void>;
using unsafe DrawElementsBasesFn = delegate* unmanaged<uint, int, uint, nint, int, int, uint, void>;
using unsafe DrawArraysInstancedBaseInstanceFn = delegate* unmanaged<uint, int, int, int, uint, void>;
using unsafe DrawRangeElementsFn = delegate* unmanaged<uint, uint, uint, int, uint, nint, void>;
using unsafe MultiDrawArraysFn = delegate* unmanaged<uint, int*, int*, int, void>;
using unsafe MultiDrawElementsFn = delegate* unmanaged<uint, int*, uint, nint*, int, void>;
using unsafe MultiDrawElementsIndirectFn = delegate* unmanaged<uint, uint, nint, int, int, void>;
using unsafe MultiDrawElementsIndirectCountFn = delegate* unmanaged<uint, uint, nint, nint, int, int, void>;
using unsafe DrawElementsIndirectFn = delegate* unmanaged<uint, uint, nint, void>;
using unsafe DrawArraysIndirectFn = delegate* unmanaged<uint, nint, void>;
using unsafe MultiDrawArraysIndirectFn = delegate* unmanaged<uint, nint, int, int, void>;
using unsafe ClearFn = delegate* unmanaged<uint, void>;
using unsafe ClearBufferfvFn = delegate* unmanaged<uint, int, float*, void>;
using unsafe ClearBufferivFn = delegate* unmanaged<uint, int, int*, void>;
using unsafe ClearBufferuivFn = delegate* unmanaged<uint, int, uint*, void>;
using unsafe ClearBufferfiFn = delegate* unmanaged<uint, int, float, int, void>;
using unsafe ClearNamedFramebufferfvFn = delegate* unmanaged<uint, uint, int, float*, void>;
using unsafe ClearNamedFramebufferfiFn = delegate* unmanaged<uint, uint, int, float, int, void>;
using unsafe ClearTexImageFn = delegate* unmanaged<uint, int, uint, uint, nint, void>;
using unsafe BlitFramebufferFn = delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void>;
using unsafe BlitNamedFramebufferFn = delegate* unmanaged<uint, uint, int, int, int, int, int, int, int, int, uint,
    uint, void>;
using unsafe CopyImageSubDataFn = delegate* unmanaged<uint, uint, int, int, int, int, uint, uint, int, int, int, int,
    int, int, int, void>;
using unsafe CopyTexSubImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, int, int, void>;
using unsafe ReadPixelsFn = delegate* unmanaged<int, int, int, int, uint, uint, nint, void>;
using unsafe GetTexImageFn = delegate* unmanaged<uint, int, uint, uint, nint, void>;
using unsafe DispatchComputeFn = delegate* unmanaged<uint, uint, uint, void>;
using unsafe BufferDataFn = delegate* unmanaged<uint, nint, nint, uint, void>;
using unsafe BufferSubDataFn = delegate* unmanaged<uint, nint, nint, nint, void>;
using unsafe NamedBufferDataFn = delegate* unmanaged<uint, nint, nint, uint, void>;
using unsafe NamedBufferSubDataFn = delegate* unmanaged<uint, nint, nint, nint, void>;
using unsafe MapBufferRangeFn = delegate* unmanaged<uint, nint, nint, uint, nint>;
using unsafe MapNamedBufferRangeFn = delegate* unmanaged<uint, nint, nint, uint, nint>;
using unsafe MapBufferFn = delegate* unmanaged<uint, uint, nint>;
using unsafe CopyBufferSubDataFn = delegate* unmanaged<uint, uint, nint, nint, nint, void>;
using unsafe CopyNamedBufferSubDataFn = delegate* unmanaged<uint, uint, nint, nint, nint, void>;
using unsafe TexSubImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, uint, uint, nint, void>;
using unsafe TexImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, uint, uint, nint, void>;
using unsafe TextureSubImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, uint, uint, nint, void>;
using unsafe GenerateMipmapFn = delegate* unmanaged<uint, void>;
using unsafe GenerateTextureMipmapFn = delegate* unmanaged<uint, void>;
using unsafe ClearNamedBufferSubDataFn = delegate* unmanaged<uint, uint, nint, nint, uint, uint, nint, void>;
using unsafe TexImage3DFn = delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, nint, void>;
using unsafe TexSubImage3DFn = delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, nint, void>;
using unsafe TexStorage2DFn = delegate* unmanaged<uint, int, uint, int, int, void>;
using unsafe TexStorage3DFn = delegate* unmanaged<uint, int, uint, int, int, int, void>;
using unsafe GetTextureImageFn = delegate* unmanaged<uint, int, uint, uint, int, nint, void>;
using unsafe GetTextureSubImageFn = delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, int, nint,
    void>;

namespace Komet.Vulkan;

// Draws and clears go to Drawing/Clearing first, which may take them over (GL skips them); anything GL runs goes to
// Touching first (its buffer, texture or named framebuffer, 0 for the bound one) so Vulkan's work is handed over first.
// While GL rests (Vulkan-only, Resting) it also skips untaken draws, clears, blits, copies and dispatches.
internal static unsafe partial class GlTap
{
    private const int TouchAt = 50, TouchCount = 53;

    // A texture call the record follows, else one more it does not
    private static bool Follows(bool tracked)
    {
        if (!tracked) UntrackedCalls++;
        return Assert(UntrackedCalls >= 0) && tracked;
    }
    private const uint ClearColorBit = 0x4000, ClearDepthBit = 0x100, GlColor = 0x1800, GlDepth = 0x1801;
    private const uint GlDepthStencil = 0x84F9, MapWrite = 2, PersistentStorage = 0x40, GlRenderbuffer = 0x8D41;

    public enum Touch
    {
        Draw,
        Clear, // the draw framebuffer's attachments
        ClearNamed, // a framebuffer's
        ClearTexture, Blit, Copy, Read, Compute,
        Upload, // into a buffer
        TextureUpload, Mipmap
    }

    // One draw (index type 0: glDrawArrays*); Indirect: Vulkan-only, parameters in a buffer (Count, Instances 1)
    public readonly record struct DrawCall(uint Mode, int Count, uint IndexType, nint Indices, int Instances, int First,
        int BaseVertex, uint BaseInstance, Indirect? Indirect = null);

    // Commands' buffer (GL_DRAW_INDIRECT_BUFFER), offset, count, stride; count buffer (GL_PARAMETER_BUFFER) and offset
    public readonly record struct Indirect(uint Buffer, nint Offset, int Draws, int Stride, uint CountBuffer = 0,
        nint CountOffset = 0);

    private const int ArraysCommand = 16, ElementsCommand = 20, MaxIndirect = 1 << 12;

    // Vulkan-only (the record): an indirect draw for Drawing, from the commands' buffer bound now
    private static bool IndirectDrawn((uint Mode, uint IndexType) draw, nint offset, (int Draws, int Stride) commands,
        (uint Buffer, nint Offset) count = default)
    {
        if (!Recording || _indirect == 0 || commands.Draws is <= 0 or > MaxIndirect || offset < 0 || !Assert(commands.Stride >= 0))
            return false;
        var tight = draw.IndexType == 0 ? ArraysCommand : ElementsCommand; // a stride of 0: the commands one after another
        var stride = commands.Stride > 0 ? commands.Stride : tight;
        var indirect = new Indirect(_indirect, offset, commands.Draws, stride, count.Buffer, count.Offset);
        return Assert(stride > 0) && Drawn(new DrawCall(draw.Mode, 1, draw.IndexType, 0, 1, 0, 0, 0, indirect));
    }

    // One clear: framebuffer (-1: bound), GL_*_BUFFER_BIT mask, draw buffer (glClearBuffer*, -1: all), values
    public readonly record struct ClearCall(int Into, uint Mask, int Output, (float R, float G, float B, float A) Color,
        double Depth, int Stencil = 0);

    public const uint ClearColorMask = ClearColorBit, ClearDepthMask = ClearDepthBit, ClearStencilMask = ClearStencilBit;
    private const uint ClearStencilBit = 0x400, GlStencilBuffer = 0x1802;

    // A depth and stencil clear: the stencil too where the record follows its value (Vulkan-only), else the depth alone
    private static uint DepthStencilBits => Recording ? ClearDepthBit | ClearStencilBit : ClearDepthBit;

    public static System.Func<DrawCall, bool>? Drawing { get; set; }

    // Clears of color or depth only
    public static System.Func<ClearCall, bool>? Clearing { get; set; }

    public static Action<Touch, uint>? Touching { get; set; }

    private static bool Drawn(DrawCall call) =>
        _quiet == 0 && Drawing is { } drawing && Assert(call.Count >= 0) && Assert(_table is not null) && Asked(drawing, call, false);

    private static bool Cleared(ClearCall call) =>
        _quiet == 0 && Clearing is { } clearing && Assert(call.Into >= -1) && Assert(_table is not null) && Asked(clearing, call, false);

    // Texture 0 was specified, copied or mipmapped into: it may be complete, so empty units no longer read (0, 0, 0, 1)
    public static bool DefaultTextureTouched { get; private set; }

    private static void Touched(Touch kind, uint id)
    {
        if (!Assert(_table is not null) || !Assert(kind <= Touch.Mipmap)) return;
        if (id == 0 && kind is Touch.TextureUpload or Touch.Mipmap or Touch.Copy) DefaultTextureTouched = true;
        if (_quiet == 0 && Touching is { } touching) Heard(static s => s.touching(s.kind, s.id), (touching, kind, id));
    }

    private static ClearCall ClearOf(int into, uint buffer, int drawBuffer, float* values)
    {
        _ = Assert(values != null) && Assert(drawBuffer >= 0) && Assert(into >= -1);
        if (buffer == GlDepth) return new ClearCall(into, ClearDepthBit, -1, default, values[0]);
        var mask = buffer == GlColor ? ClearColorBit : 0;
        return new ClearCall(into, mask, drawBuffer, (values[0], values[1], values[2], values[3]), 1);
    }

    // Why a buffer Komet specified itself is unseen: its size is not what the listener heard either
    public const string OwnData = "Komet's own data";

    // A buffer write the listener replays; Komet's own (Quietly) is only heard of as unseen
    private static void DataWritten(uint buffer, nint size, nint data, bool persistent)
    {
        if (!Assert(size >= 0) || !Assert(_quiet >= 0)) return;
        if (Recording) Sized(buffer, size);
        if (_quiet > 0) Unseen(buffer, OwnData);
        else if (Writes is { } writes) Heard(static s => s.writes.BufferData(s.buffer, s.size, s.data, s.persistent), (writes, buffer, size, data, persistent));
    }

    private static void SubDataWritten(uint buffer, nint offset, nint size, nint data)
    {
        if (!Assert(offset >= 0) || !Assert(size >= 0)) return;
        if (_quiet > 0) Unseen(buffer, "Komet's own sub-data");
        else if (Writes is { } writes) Heard(static s => s.writes.BufferSubData(s.buffer, s.offset, s.size, s.data), (writes, buffer, offset, size, data));
    }

    private static void CopyWritten(uint from, uint to, nint fromOffset, nint toOffset, nint size)
    {
        if (!Assert(size >= 0) || !Assert(fromOffset >= 0 && toOffset >= 0)) return;
        if (_quiet > 0) Unseen(to, "Komet's own copy");
        else if (Writes is { } writes)
            Heard(static s => s.writes.BufferCopy(s.from, s.to, s.fromOffset, s.toOffset, s.size), (writes, from, to, fromOffset, toOffset, size));
    }

    // Draws, clears, copies and reads, then the uploads: TouchAt on, in this order
    private static Entry[] Drawings() =>
    [
        new("glDrawArrays", (IntPtr)(DrawArraysFn)(&DrawArrays), Group.Touch),
        new("glDrawElements", (IntPtr)(DrawElementsFn)(&DrawElements), Group.Touch),
        new("glDrawElementsInstanced", (IntPtr)(DrawElementsInstancedFn)(&DrawElementsInstanced), Group.Touch),
        new("glDrawArraysInstanced", (IntPtr)(DrawArraysInstancedFn)(&DrawArraysInstanced), Group.Touch),
        new("glDrawElementsBaseVertex", (IntPtr)(DrawElementsBaseVertexFn)(&DrawElementsBaseVertex), Group.Touch),
        new("glDrawElementsInstancedBaseVertex",
            (IntPtr)(DrawElementsInstancedBaseVertexFn)(&DrawElementsInstancedBaseVertex), Group.Touch),
        new("glDrawElementsInstancedBaseVertexBaseInstance", (IntPtr)(DrawElementsBasesFn)(&DrawElementsBases),
            Group.Touch),
        new("glDrawArraysInstancedBaseInstance",
            (IntPtr)(DrawArraysInstancedBaseInstanceFn)(&DrawArraysInstancedBaseInstance), Group.Touch),
        new("glDrawRangeElements", (IntPtr)(DrawRangeElementsFn)(&DrawRangeElements), Group.Touch),
        new("glMultiDrawArrays", (IntPtr)(MultiDrawArraysFn)(&MultiDrawArrays), Group.Touch),
        new("glMultiDrawElements", (IntPtr)(MultiDrawElementsFn)(&MultiDrawElements), Group.Touch),
        new("glMultiDrawElementsIndirect", (IntPtr)(MultiDrawElementsIndirectFn)(&MultiDrawElementsIndirect),
            Group.Touch),
        new("glMultiDrawElementsIndirectCount",
            (IntPtr)(MultiDrawElementsIndirectCountFn)(&MultiDrawElementsIndirectCount), Group.Touch),
        new("glDrawElementsIndirect", (IntPtr)(DrawElementsIndirectFn)(&DrawElementsIndirect), Group.Touch),
        new("glDrawArraysIndirect", (IntPtr)(DrawArraysIndirectFn)(&DrawArraysIndirect), Group.Touch),
        new("glMultiDrawArraysIndirect", (IntPtr)(MultiDrawArraysIndirectFn)(&MultiDrawArraysIndirect), Group.Touch),
        new("glClear", (IntPtr)(ClearFn)(&Clear), Group.Touch),
        new("glClearBufferfv", (IntPtr)(ClearBufferfvFn)(&ClearBufferfv), Group.Touch),
        new("glClearBufferiv", (IntPtr)(ClearBufferivFn)(&ClearBufferiv), Group.Touch),
        new("glClearBufferuiv", (IntPtr)(ClearBufferuivFn)(&ClearBufferuiv), Group.Touch),
        new("glClearBufferfi", (IntPtr)(ClearBufferfiFn)(&ClearBufferfi), Group.Touch),
        new("glClearNamedFramebufferfv", (IntPtr)(ClearNamedFramebufferfvFn)(&ClearNamedFramebufferfv), Group.Touch),
        new("glClearNamedFramebufferfi", (IntPtr)(ClearNamedFramebufferfiFn)(&ClearNamedFramebufferfi), Group.Touch),
        new("glClearTexImage", (IntPtr)(ClearTexImageFn)(&ClearTexImage), Group.Touch),
        new("glBlitFramebuffer", (IntPtr)(BlitFramebufferFn)(&BlitFramebuffer), Group.Touch),
        new("glBlitNamedFramebuffer", (IntPtr)(BlitNamedFramebufferFn)(&BlitNamedFramebuffer), Group.Touch),
        new("glCopyImageSubData", (IntPtr)(CopyImageSubDataFn)(&CopyImageSubData), Group.Touch),
        new("glCopyTexSubImage2D", (IntPtr)(CopyTexSubImage2DFn)(&CopyTexSubImage2D), Group.Touch),
        new("glReadPixels", (IntPtr)(ReadPixelsFn)(&ReadPixels), Group.Touch),
        new("glGetTexImage", (IntPtr)(GetTexImageFn)(&GetTexImage), Group.Touch),
        new("glDispatchCompute", (IntPtr)(DispatchComputeFn)(&DispatchCompute), Group.Touch)
    ];

    private static Entry[] Uploads() =>
    [
        new("glBufferData", (IntPtr)(BufferDataFn)(&BufferData), Group.Touch),
        new("glBufferSubData", (IntPtr)(BufferSubDataFn)(&BufferSubData), Group.Touch),
        new("glNamedBufferData", (IntPtr)(NamedBufferDataFn)(&NamedBufferData), Group.Touch),
        new("glNamedBufferSubData", (IntPtr)(NamedBufferSubDataFn)(&NamedBufferSubData), Group.Touch),
        new("glMapBufferRange", (IntPtr)(MapBufferRangeFn)(&MapBufferRange), Group.Touch),
        new("glMapNamedBufferRange", (IntPtr)(MapNamedBufferRangeFn)(&MapNamedBufferRange), Group.Touch),
        new("glMapBuffer", (IntPtr)(MapBufferFn)(&MapBuffer), Group.Touch),
        new("glCopyBufferSubData", (IntPtr)(CopyBufferSubDataFn)(&CopyBufferSubData), Group.Touch),
        new("glCopyNamedBufferSubData", (IntPtr)(CopyNamedBufferSubDataFn)(&CopyNamedBufferSubData), Group.Touch),
        new("glTexSubImage2D", (IntPtr)(TexSubImage2DFn)(&TexSubImage2D), Group.Touch),
        new("glTexImage2D", (IntPtr)(TexImage2DFn)(&TexImage2D), Group.Touch),
        new("glTextureSubImage2D", (IntPtr)(TextureSubImage2DFn)(&TextureSubImage2D), Group.Touch),
        new("glGenerateMipmap", (IntPtr)(GenerateMipmapFn)(&GenerateMipmap), Group.Touch),
        new("glGenerateTextureMipmap", (IntPtr)(GenerateTextureMipmapFn)(&GenerateTextureMipmap), Group.Touch),
        new("glClearNamedBufferSubData", (IntPtr)(ClearNamedBufferSubDataFn)(&ClearNamedBufferSubData), Group.Touch),
        new("glTexImage3D", (IntPtr)(TexImage3DFn)(&TexImage3D), Group.Touch),
        new("glTexSubImage3D", (IntPtr)(TexSubImage3DFn)(&TexSubImage3D), Group.Touch),
        new("glTexStorage2D", (IntPtr)(TexStorage2DFn)(&TexStorage2D), Group.Touch),
        new("glTexStorage3D", (IntPtr)(TexStorage3DFn)(&TexStorage3D), Group.Touch),
        new("glBufferStorage", (IntPtr)(BufferDataFn)(&BufferStorage), Group.Touch),
        new("glGetTextureImage", (IntPtr)(GetTextureImageFn)(&GetTextureImage), Group.Touch),
        new("glGetTextureSubImage", (IntPtr)(GetTextureSubImageFn)(&GetTextureSubImage), Group.Touch)
    ];

    [UnmanagedCallersOnly]
    private static void DrawArrays(uint a, int b, int c)
    {
        if (Drawn(new DrawCall(a, c, 0, 0, 1, b, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt] != IntPtr.Zero) && Index(TouchAt, _entries.Length))
            ((DrawArraysFn)Original[TouchAt])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void DrawElements(uint a, int b, uint c, nint d)
    {
        if (Drawn(new DrawCall(a, b, c, d, 1, 0, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 1] != IntPtr.Zero) && Index(TouchAt + 1, _entries.Length))
            ((DrawElementsFn)Original[TouchAt + 1])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsInstanced(uint a, int b, uint c, nint d, int e)
    {
        if (Drawn(new DrawCall(a, b, c, d, e, 0, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 2] != IntPtr.Zero) && Index(TouchAt + 2, _entries.Length))
            ((DrawElementsInstancedFn)Original[TouchAt + 2])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void DrawArraysInstanced(uint a, int b, int c, int d)
    {
        if (Drawn(new DrawCall(a, c, 0, 0, d, b, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 3] != IntPtr.Zero) && Index(TouchAt + 3, _entries.Length))
            ((DrawArraysInstancedFn)Original[TouchAt + 3])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsBaseVertex(uint a, int b, uint c, nint d, int e)
    {
        if (Drawn(new DrawCall(a, b, c, d, 1, 0, e, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 4] != IntPtr.Zero) && Index(TouchAt + 4, _entries.Length))
            ((DrawElementsBaseVertexFn)Original[TouchAt + 4])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsInstancedBaseVertex(uint a, int b, uint c, nint d, int e, int f)
    {
        if (Drawn(new DrawCall(a, b, c, d, e, 0, f, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 5] != IntPtr.Zero) && Index(TouchAt + 5, _entries.Length))
            ((DrawElementsInstancedBaseVertexFn)Original[TouchAt + 5])(a, b, c, d, e, f);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsBases(uint a, int b, uint c, nint d, int e, int f, uint g)
    {
        if (Drawn(new DrawCall(a, b, c, d, e, 0, f, g))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 6] != IntPtr.Zero) && Index(TouchAt + 6, _entries.Length))
            ((DrawElementsBasesFn)Original[TouchAt + 6])(a, b, c, d, e, f, g);
    }

    [UnmanagedCallersOnly]
    private static void DrawArraysInstancedBaseInstance(uint a, int b, int c, int d, uint e)
    {
        if (Drawn(new DrawCall(a, c, 0, 0, d, b, 0, e))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 7] != IntPtr.Zero) && Index(TouchAt + 7, _entries.Length))
            ((DrawArraysInstancedBaseInstanceFn)Original[TouchAt + 7])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void DrawRangeElements(uint a, uint b, uint c, int d, uint e, nint f)
    {
        if (Drawn(new DrawCall(a, d, e, f, 1, 0, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 8] != IntPtr.Zero) && Index(TouchAt + 8, _entries.Length))
            ((DrawRangeElementsFn)Original[TouchAt + 8])(a, b, c, d, e, f);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawArrays(uint a, int* b, int* c, int d)
    {
        if (Recording && MultiDrawn((a, 0), c, null, b, d)) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 9] != IntPtr.Zero) && Index(TouchAt + 9, _entries.Length))
            ((MultiDrawArraysFn)Original[TouchAt + 9])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawElements(uint a, int* b, uint c, nint* d, int e)
    {
        if (Recording && MultiDrawn((a, c), b, d, null, e)) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 10] != IntPtr.Zero) && Index(TouchAt + 10, _entries.Length))
            ((MultiDrawElementsFn)Original[TouchAt + 10])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawElementsIndirect(uint a, uint b, nint c, int d, int e)
    {
        if (IndirectDrawn((a, b), c, (d, e))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 11] != IntPtr.Zero) && Index(TouchAt + 11, _entries.Length))
            ((MultiDrawElementsIndirectFn)Original[TouchAt + 11])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawElementsIndirectCount(uint a, uint b, nint c, nint d, int e, int f)
    {
        if (_parameters != 0 && IndirectDrawn((a, b), c, (e, f), (_parameters, d))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 12] != IntPtr.Zero) && Index(TouchAt + 12, _entries.Length))
            ((MultiDrawElementsIndirectCountFn)Original[TouchAt + 12])(a, b, c, d, e, f);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsIndirect(uint a, uint b, nint c)
    {
        if (IndirectDrawn((a, b), c, (1, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 13] != IntPtr.Zero) && Index(TouchAt + 13, _entries.Length))
            ((DrawElementsIndirectFn)Original[TouchAt + 13])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void DrawArraysIndirect(uint a, nint b)
    {
        if (IndirectDrawn((a, 0), b, (1, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 14] != IntPtr.Zero) && Index(TouchAt + 14, _entries.Length))
            ((DrawArraysIndirectFn)Original[TouchAt + 14])(a, b);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawArraysIndirect(uint a, nint b, int c, int d)
    {
        if (IndirectDrawn((a, 0), b, (c, d))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 15] != IntPtr.Zero) && Index(TouchAt + 15, _entries.Length))
            ((MultiDrawArraysIndirectFn)Original[TouchAt + 15])(a, b, c, d);
    }

    private const int MaxMultiDraws = 1 << 16;

    // For the report: the most ranges one multi-draw had, and the ranges taken one by one so far
    public static int LargestMultiDraw { get; private set; }
    public static long MultiDrawRanges { get; private set; }

    // Vulkan-only (record): each multi-draw range is a draw, taken with the first (same state; leftovers missing)
    private static bool MultiDrawn((uint Mode, uint IndexType) draw, int* counts, nint* offsets, int* firsts, int count)
    {
        if (count is <= 0 or > MaxMultiDraws || counts == null || (offsets == null && firsts == null)) return false;
        (LargestMultiDraw, MultiDrawRanges) = (Math.Max(LargestMultiDraw, count), MultiDrawRanges + count);
        try
        {
            for (var i = 0; i < Math.Min(count, MaxMultiDraws); i++)
            {
                var call = offsets != null
                    ? new DrawCall(draw.Mode, counts[i], draw.IndexType, offsets[i], 1, 0, 0, 0)
                    : new DrawCall(draw.Mode, counts[i], 0, 0, 1, firsts[i], 0, 0);
                LaterRange = i > 0;
                if (!Drawn(call) && i == 0) return false;
            }
        }
        finally
        {
            LaterRange = false;
        }

        return Assert(Recording) && Assert(count > 0);
    }

    // A multi-draw range past the first is being judged: no Touched follows any verdict (the whole call is taken)
    public static bool LaterRange { get; private set; }

    [UnmanagedCallersOnly]
    private static void Clear(uint a)
    {
        if (Cleared(new ClearCall(-1, a, -1, _clearColor, _clearDepth, _clearStencil))) return;
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 16] != IntPtr.Zero) && Index(TouchAt + 16, _entries.Length))
            ((ClearFn)Original[TouchAt + 16])(a);
    }

    [UnmanagedCallersOnly]
    private static void ClearBufferfv(uint a, int b, float* c)
    {
        if (c != null && Cleared(ClearOf(-1, a, b, c))) return;
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 17] != IntPtr.Zero) && Index(TouchAt + 17, _entries.Length))
            ((ClearBufferfvFn)Original[TouchAt + 17])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void ClearBufferiv(uint a, int b, int* c)
    {
        if (Recording && a == GlStencilBuffer && c != null && Cleared(new ClearCall(-1, ClearStencilBit, -1, default, 1, c[0])))
            return;
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 18] != IntPtr.Zero) && Index(TouchAt + 18, _entries.Length))
            ((ClearBufferivFn)Original[TouchAt + 18])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void ClearBufferuiv(uint a, int b, uint* c)
    {
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 19] != IntPtr.Zero) && Index(TouchAt + 19, _entries.Length))
            ((ClearBufferuivFn)Original[TouchAt + 19])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void ClearBufferfi(uint a, int b, float c, int d)
    {
        if (a == GlDepthStencil && Cleared(new ClearCall(-1, DepthStencilBits, -1, default, c, d))) return;
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 20] != IntPtr.Zero) && Index(TouchAt + 20, _entries.Length))
            ((ClearBufferfiFn)Original[TouchAt + 20])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void ClearNamedFramebufferfv(uint a, uint b, int c, float* d)
    {
        a = Framed(a);
        if (a != 0 && d != null && Cleared(ClearOf((int)a, b, c, d))) return;
        Touched(Touch.ClearNamed, a);
        if (!Rests && Assert(Original[TouchAt + 21] != IntPtr.Zero) && Index(TouchAt + 21, _entries.Length))
            ((ClearNamedFramebufferfvFn)Original[TouchAt + 21])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void ClearNamedFramebufferfi(uint a, uint b, int c, float d, int e)
    {
        a = Framed(a);
        if (a != 0 && b == GlDepthStencil && Cleared(new ClearCall((int)a, DepthStencilBits, -1, default, d, e))) return;
        Touched(Touch.ClearNamed, a);
        if (!Rests && Assert(Original[TouchAt + 22] != IntPtr.Zero) && Index(TouchAt + 22, _entries.Length))
            ((ClearNamedFramebufferfiFn)Original[TouchAt + 22])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void ClearTexImage(uint a, int b, uint c, uint d, nint e)
    {
        if (TextureClearing is { } clearing && _quiet == 0 &&
            Asked(clearing, new TextureClear(a, b, (0, 0, 0, -1, -1, -1), (c, d), e), false)) return; // Vulkan's
        Touched(Touch.ClearTexture, a);
        if (!Rests) Changed(a); // resting, OpenGL clears it no more than Vulkan: missing, not stale
        if (!Rests && Assert(Original[TouchAt + 23] != IntPtr.Zero) && Index(TouchAt + 23, _entries.Length))
            ((ClearTexImageFn)Original[TouchAt + 23])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void BlitFramebuffer(int a, int b, int c, int d, int e, int f, int g, int h, uint i, uint j)
    {
        if (Blitted(_readFramebuffer, _framebuffer, [a, b, c, d, e, f, g, h], i, j)) return;
        Touched(Touch.Blit, 0);
        if (!Rests && Assert(Original[TouchAt + 24] != IntPtr.Zero) && Index(TouchAt + 24, _entries.Length))
            ((BlitFramebufferFn)Original[TouchAt + 24])(a, b, c, d, e, f, g, h, i, j);
    }

    [UnmanagedCallersOnly]
    private static void BlitNamedFramebuffer(uint a, uint b, int c, int d, int e, int f, int g, int h, int i, int j,
        uint k, uint l)
    {
        (a, b) = (Framed(a), Framed(b));
        if (Blitted(a, b, [c, d, e, f, g, h, i, j], k, l)) return;
        Touched(Touch.Blit, b);
        if (!Rests && Assert(Original[TouchAt + 25] != IntPtr.Zero) && Index(TouchAt + 25, _entries.Length))
            ((BlitNamedFramebufferFn)Original[TouchAt + 25])(a, b, c, d, e, f, g, h, i, j, k, l);
    }

    [UnmanagedCallersOnly]
    private static void CopyImageSubData(uint a, uint b, int c, int d, int e, int f, uint g, uint h, int i, int j,
        int k, int l, int m, int n, int o)
    {
        Touched(Touch.Copy, g);
        if (Recording && h != GlRenderbuffer && b != GlRenderbuffer) ImageCopied((a, c, d, e, f), (g, i, j, k, l), (m, n, o));
        else if (h != GlRenderbuffer && !Rests) Changed(g); // into a texture, not a renderbuffer
        if (!Rests && Assert(Original[TouchAt + 26] != IntPtr.Zero) && Index(TouchAt + 26, _entries.Length))
            ((CopyImageSubDataFn)Original[TouchAt + 26])(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o);
    }

    [UnmanagedCallersOnly]
    private static void CopyTexSubImage2D(uint a, int b, int c, int d, int e, int f, int g, int h)
    {
        Touched(Touch.Copy, BoundName(a));
        if (Follows(Specifiable(a))) FramebufferRead(BoundName(a), a, b, (c, d), (e, f, g, h));
        if (!Rests && Assert(Original[TouchAt + 27] != IntPtr.Zero) && Index(TouchAt + 27, _entries.Length))
            ((CopyTexSubImage2DFn)Original[TouchAt + 27])(a, b, c, d, e, f, g, h);
    }

    [UnmanagedCallersOnly]
    private static void ReadPixels(int a, int b, int c, int d, uint e, uint f, nint g)
    {
        var read = Framed(_readFramebuffer);
        if (Reading is not null && ReadAnswered(new ReadCall((int)read, ReadBufferOf(read), 0, 0, (a, b, c, d), (e, f), g)))
            return;
        Touched(Touch.Read, 0);
        if (Assert(Original[TouchAt + 28] != IntPtr.Zero) && Index(TouchAt + 28, _entries.Length))
            ((ReadPixelsFn)Original[TouchAt + 28])(a, b, c, d, e, f, g);
    }

    [UnmanagedCallersOnly]
    private static void GetTexImage(uint a, int b, uint c, uint d, nint e)
    {
        if (Reading is not null && a == 0x0DE1 && BoundName(a) != 0 && ReadAnswered(new ReadCall(0, 0, BoundName(a), b, (0, 0, -1, -1), (c, d), e)))
            return;
        Touched(Touch.Read, BoundName(a));
        if (Assert(Original[TouchAt + 29] != IntPtr.Zero) && Index(TouchAt + 29, _entries.Length))
            ((GetTexImageFn)Original[TouchAt + 29])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void DispatchCompute(uint a, uint b, uint c)
    {
        Touched(Touch.Compute, 0);
        if (!Rests && Assert(Original[TouchAt + 30] != IntPtr.Zero) && Index(TouchAt + 30, _entries.Length))
            ((DispatchComputeFn)Original[TouchAt + 30])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void BufferData(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, Bound(a));
        var small = BufferLean(Bound(a), b, 0, d) == Leaning.Small; // first: the mirrors keep all of a lean one
        DataWritten(Bound(a), b, c, false);
        if (Assert(Original[TouchAt + 31] != IntPtr.Zero) && Index(TouchAt + 31, _entries.Length))
            ((BufferDataFn)Original[TouchAt + 31])(a, small ? LeanBufferKept : b, small ? 0 : c, d);
    }

    [UnmanagedCallersOnly]
    private static void BufferSubData(uint a, nint b, nint c, nint d)
    {
        Touched(Touch.Upload, Bound(a));
        SubDataWritten(Bound(a), b, c, d);
        if (!BufferHeld(Bound(a)) && Assert(Original[TouchAt + 32] != IntPtr.Zero) && Index(TouchAt + 32, _entries.Length))
            ((BufferSubDataFn)Original[TouchAt + 32])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void NamedBufferData(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, a);
        var small = BufferLean(a, b, 0, d) == Leaning.Small;
        DataWritten(a, b, c, false);
        if (Assert(Original[TouchAt + 33] != IntPtr.Zero) && Index(TouchAt + 33, _entries.Length))
            ((NamedBufferDataFn)Original[TouchAt + 33])(a, small ? LeanBufferKept : b, small ? 0 : c, d);
    }

    [UnmanagedCallersOnly]
    private static void NamedBufferSubData(uint a, nint b, nint c, nint d)
    {
        Touched(Touch.Upload, a);
        SubDataWritten(a, b, c, d);
        if (!BufferHeld(a) && Assert(Original[TouchAt + 34] != IntPtr.Zero) && Index(TouchAt + 34, _entries.Length))
            ((NamedBufferSubDataFn)Original[TouchAt + 34])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static nint MapBufferRange(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, Bound(a));
        if ((d & MapWrite) != 0) Unseen(Bound(a), "a mapping");
        Unleaned(Bound(a), a);
        var gl = Assert(Original[TouchAt + 35] != IntPtr.Zero) && Index(TouchAt + 35, _entries.Length)
            ? ((MapBufferRangeFn)Original[TouchAt + 35])(a, b, c, d)
            : 0;
        return Mapped(Bound(a), b, c, d, gl);
    }

    [UnmanagedCallersOnly]
    private static nint MapNamedBufferRange(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, a);
        if ((d & MapWrite) != 0) Unseen(a, "a mapping");
        Unleaned(a, 0);
        var gl = Assert(Original[TouchAt + 36] != IntPtr.Zero) && Index(TouchAt + 36, _entries.Length)
            ? ((MapNamedBufferRangeFn)Original[TouchAt + 36])(a, b, c, d)
            : 0;
        return Mapped(a, b, c, d, gl);
    }

    [UnmanagedCallersOnly]
    private static nint MapBuffer(uint a, uint b)
    {
        Touched(Touch.Upload, Bound(a));
        if (b != 0x88B8) Unseen(Bound(a), "a mapping"); // anything but GL_READ_ONLY writes
        Unleaned(Bound(a), a);
        return Assert(Original[TouchAt + 37] != IntPtr.Zero) && Index(TouchAt + 37, _entries.Length)
            ? ((MapBufferFn)Original[TouchAt + 37])(a, b)
            : 0;
    }

    [UnmanagedCallersOnly]
    private static void CopyBufferSubData(uint a, uint b, nint c, nint d, nint e)
    {
        Touched(Touch.Upload, Bound(b));
        CopyWritten(Bound(a), Bound(b), c, d, e);
        if (!BufferHeld(Bound(a)) && !BufferHeld(Bound(b)) && Assert(Original[TouchAt + 38] != IntPtr.Zero) && Index(TouchAt + 38, _entries.Length))
            ((CopyBufferSubDataFn)Original[TouchAt + 38])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void CopyNamedBufferSubData(uint a, uint b, nint c, nint d, nint e)
    {
        Touched(Touch.Upload, b);
        CopyWritten(a, b, c, d, e);
        if (!BufferHeld(a) && !BufferHeld(b) && Assert(Original[TouchAt + 39] != IntPtr.Zero) && Index(TouchAt + 39, _entries.Length))
            ((CopyNamedBufferSubDataFn)Original[TouchAt + 39])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void TexSubImage2D(uint a, int b, int c, int d, int e, int f, uint g, uint h, nint i)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        var taken = Follows(Specifiable(a)) && SubImaged(BoundName(a), a, b, (c, d, Face(a), e, f, 1), (g, h), i);
        if (Skipped(BoundName(a), a, taken, (b, c, d, e, f))) return;
        if (!TextureHeld(BoundName(a)) && Assert(Original[TouchAt + 40] != IntPtr.Zero) && Index(TouchAt + 40, _entries.Length))
            ((TexSubImage2DFn)Original[TouchAt + 40])(a, b, c, d, e, f, g, h, i);
    }

    [UnmanagedCallersOnly]
    private static void TexImage2D(uint a, int b, int c, int d, int e, int f, uint g, uint h, nint i)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        var lean = Specifiable(a) ? TextureLean(BoundName(a), b, (d, e, 1)) : Leaning.Given;
        if (lean != Leaning.Held && Assert(Original[TouchAt + 41] != IntPtr.Zero) && Index(TouchAt + 41, _entries.Length))
            ((TexImage2DFn)Original[TouchAt + 41])(a, b, c, lean == Leaning.Small ? 1 : d, lean == Leaning.Small ? 1 : e, f, g, h,
                lean == Leaning.Small ? 0 : i);
        if (Recording && Follows(Specifiable(a)))
        {
            Heard(static s => Specified(s.Item1, s.a, (s.b, 1), (s.c, s.d, s.e, 1), s.h),
                (BoundName(a), a, b, c, d, e, h));
            Imaged(BoundName(a), a, (b, 1), (c, d, e, 1), (g, h), i);
        }
    }

    [UnmanagedCallersOnly]
    private static void TextureSubImage2D(uint a, int b, int c, int d, int e, int f, uint g, uint h, nint i)
    {
        Touched(Touch.TextureUpload, a);
        if (Follows(Tracked(TargetOf(a)))) _ = SubImaged(a, TargetOf(a), b, (c, d, 0, e, f, 1), (g, h), i);
        if (!TextureHeld(a) && Assert(Original[TouchAt + 42] != IntPtr.Zero) && Index(TouchAt + 42, _entries.Length))
            ((TextureSubImage2DFn)Original[TouchAt + 42])(a, b, c, d, e, f, g, h, i);
    }

    [UnmanagedCallersOnly]
    private static void GenerateMipmap(uint a)
    {
        Touched(Touch.Mipmap, BoundName(a));
        if (Assert(Original[TouchAt + 43] != IntPtr.Zero) && Index(TouchAt + 43, _entries.Length))
            ((GenerateMipmapFn)Original[TouchAt + 43])(a);
        if (Recording && Follows(Specifiable(a)))
        {
            Heard(MipmapsMade, BoundName(a));
            Remipmapped(BoundName(a));
        }
    }

    [UnmanagedCallersOnly]
    private static void GenerateTextureMipmap(uint a)
    {
        Touched(Touch.Mipmap, a);
        if (Assert(Original[TouchAt + 44] != IntPtr.Zero) && Index(TouchAt + 44, _entries.Length))
            ((GenerateTextureMipmapFn)Original[TouchAt + 44])(a);
        if (!Recording) return; // untapped meanwhile
        Heard(MipmapsMade, a);
        if (Follows(Tracked(TargetOf(a)))) Remipmapped(a);
    }

    [UnmanagedCallersOnly]
    private static void ClearNamedBufferSubData(uint a, uint b, nint c, nint d, uint e, uint f, nint g)
    {
        Touched(Touch.Upload, a);
        Unseen(a, "a clear");
        if (!BufferHeld(a) && Assert(Original[TouchAt + 45] != IntPtr.Zero) && Index(TouchAt + 45, _entries.Length))
            ((ClearNamedBufferSubDataFn)Original[TouchAt + 45])(a, b, c, d, e, f, g);
    }

    [UnmanagedCallersOnly]
    private static void TexImage3D(uint a, int b, int c, int d, int e, int f, int g, uint h, uint i, nint j)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        var lean = Specifiable(a) ? TextureLean(BoundName(a), b, (d, e, f)) : Leaning.Given;
        var small = lean == Leaning.Small;
        if (lean != Leaning.Held && Assert(Original[TouchAt + 46] != IntPtr.Zero) && Index(TouchAt + 46, _entries.Length))
            ((TexImage3DFn)Original[TouchAt + 46])(a, b, c, small ? 1 : d, small ? 1 : e, f, g, h, i, small ? 0 : j);
        if (Recording && Follows(Specifiable(a)))
        {
            Heard(static s => Specified(s.Item1, s.a, (s.b, 1), (s.c, s.d, s.e, s.f), s.i),
                (BoundName(a), a, b, c, d, e, f, i));
            Imaged(BoundName(a), a, (b, 1), (c, d, e, f), (h, i), j);
        }
    }

    [UnmanagedCallersOnly]
    private static void TexSubImage3D(uint a, int b, int c, int d, int e, int f, int g, int h, uint i, uint j, nint k)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        if (Follows(Specifiable(a))) _ = SubImaged(BoundName(a), a, b, (c, d, e, f, g, h), (i, j), k);
        if (!TextureHeld(BoundName(a)) && Assert(Original[TouchAt + 47] != IntPtr.Zero) && Index(TouchAt + 47, _entries.Length))
            ((TexSubImage3DFn)Original[TouchAt + 47])(a, b, c, d, e, f, g, h, i, j, k);
    }

    [UnmanagedCallersOnly]
    private static void TexStorage2D(uint a, int b, uint c, int d, int e)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        LeanGone(BoundName(a), true); // immutable storage is never lean: OpenGL could not be given its size back
        if (Assert(Original[TouchAt + 48] != IntPtr.Zero) && Index(TouchAt + 48, _entries.Length))
            ((TexStorage2DFn)Original[TouchAt + 48])(a, b, c, d, e);
        if (Recording && Follows(Specifiable(a)))
        {
            Heard(static s => Specified(s.Item1, s.a, (0, s.b), ((int)s.c, s.d, s.e, 1), 0),
                (BoundName(a), a, b, c, d, e));
            Imaged(BoundName(a), a, (0, Math.Max(b, 1)), ((int)c, d, e, 1), default, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static void TexStorage3D(uint a, int b, uint c, int d, int e, int f)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        LeanGone(BoundName(a), true);
        if (Assert(Original[TouchAt + 49] != IntPtr.Zero) && Index(TouchAt + 49, _entries.Length))
            ((TexStorage3DFn)Original[TouchAt + 49])(a, b, c, d, e, f);
        if (Recording && Follows(Specifiable(a)))
        {
            Heard(static s => Specified(s.Item1, s.a, (0, s.b), ((int)s.c, s.d, s.e, s.f), 0),
                (BoundName(a), a, b, c, d, e, f));
            Imaged(BoundName(a), a, (0, Math.Max(b, 1)), ((int)c, d, e, f), default, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static void BufferStorage(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, Bound(a));
        var small = BufferLean(Bound(a), b, d, UsageOf(d)) == Leaning.Small;
        DataWritten(Bound(a), b, c, (d & PersistentStorage) != 0);
        if (small && Assert(Original[TouchAt + 31] != IntPtr.Zero) && Index(TouchAt + 31, _entries.Length))
            ((BufferDataFn)Original[TouchAt + 31])(a, LeanBufferKept, 0, UsageOf(d)); // mutable: its size can come back
        else if (!small && Assert(Original[TouchAt + 50] != IntPtr.Zero) && Index(TouchAt + 50, _entries.Length))
            ((BufferDataFn)Original[TouchAt + 50])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void GetTextureImage(uint a, int b, uint c, uint d, int e, nint f)
    {
        Touched(Touch.Read, a);
        if (Assert(Original[TouchAt + 51] != IntPtr.Zero) && Index(TouchAt + 51, _entries.Length))
            ((GetTextureImageFn)Original[TouchAt + 51])(a, b, c, d, e, f);
    }

    [UnmanagedCallersOnly]
    private static void GetTextureSubImage(uint a, int b, int c, int d, int e, int f, int g, int h, uint i, uint j, int k,
        nint l)
    {
        Touched(Touch.Read, a);
        if (Assert(Original[TouchAt + 52] != IntPtr.Zero) && Index(TouchAt + 52, _entries.Length))
            ((GetTextureSubImageFn)Original[TouchAt + 52])(a, b, c, d, e, f, g, h, i, j, k, l);
    }
}
