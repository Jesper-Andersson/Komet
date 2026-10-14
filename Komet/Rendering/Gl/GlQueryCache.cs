using System.Runtime.InteropServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;

namespace Komet.Rendering;

// A glGet makes a threaded driver (Mesa glthread, on via the launch script; NVIDIA threaded optimization) drain its
// queue. CloudRendererMap.OnRenderFrame reads draw framebuffer and viewport each frame to restore them: two waits, a
// twentieth of the render thread. Both are set only via OpenTK entry points, so wrapping those keeps a copy answering
// glGetIntegerv; anything else (other pname, indexed viewport, unknown state) goes to the driver, seeding the copy.
// Installed once per process before GlTap (which wraps these wrappers); always follows, answers only while Enabled.
internal static unsafe class GlQueryCache
{
    private const uint Framebuffer = 0x8D40, Read = 0x8CA8, Draw = 0x8CA9;
    private const uint DrawBinding = 0x8CA6, ReadBinding = 0x8CAA, Viewport = 0x0BA2;

    private static readonly string[] Names =
    [
        "glGetIntegerv", "glBindFramebuffer", "glBindFramebufferEXT", "glDeleteFramebuffers", "glDeleteFramebuffersEXT",
        "glViewport", "glViewportIndexedf", "glViewportIndexedfv", "glViewportArrayv"
    ];

    private const int GetAt = 0, BindAt = 1, BindExtAt = 2, DeleteAt = 3, DeleteExtAt = 4, ViewportAt = 5;

    private static readonly IntPtr[] Next = new IntPtr[Names.Length];
    private static readonly int[] Slots = new int[Names.Length];
    private static IntPtr[]? _table;
    private static uint _draw, _read;
    private static bool _drawKnown, _readKnown, _viewportKnown;
    private static (int X, int Y, int Width, int Height) _viewport;

    public static bool Enabled { get; set; } = true;
    public static bool Installed => _table is not null;

    public static void Install()
    {
        if (_table is not null) return;
        var table = AccessTools.Field(typeof(GL), "EntryPoints")?.GetValue(null) as IntPtr[];
        var names = AccessTools.Field(typeof(GL), "EntryPointNames")?.GetValue(null) as string[];
        if (table is null || names is null || !Assert(table.Length == names.Length)) return;
        for (var i = 0; i < Names.Length; i++)
        {
            Slots[i] = Array.IndexOf(names, Names[i]);
            Next[i] = Slots[i] >= 0 ? table[Slots[i]] : IntPtr.Zero;
        }

        // Without the query, a binding or the viewport call there is nothing to answer or follow
        if (Next[GetAt] == IntPtr.Zero || Next[BindAt] == IntPtr.Zero || Next[DeleteAt] == IntPtr.Zero ||
            Next[ViewportAt] == IntPtr.Zero) return;
        IntPtr[] mine =
        [
            (IntPtr)(delegate* unmanaged<uint, int*, void>)&GetIntegerv,
            (IntPtr)(delegate* unmanaged<uint, uint, void>)&Bind, (IntPtr)(delegate* unmanaged<uint, uint, void>)&BindExt,
            (IntPtr)(delegate* unmanaged<int, uint*, void>)&Delete, (IntPtr)(delegate* unmanaged<int, uint*, void>)&DeleteExt,
            (IntPtr)(delegate* unmanaged<int, int, int, int, void>)&SetViewport,
            (IntPtr)(delegate* unmanaged<uint, float, float, float, float, void>)&ViewportIndexed,
            (IntPtr)(delegate* unmanaged<uint, float*, void>)&ViewportIndexedv,
            (IntPtr)(delegate* unmanaged<uint, int, float*, void>)&ViewportArray
        ];
        Forget();
        for (var i = 0; i < Names.Length; i++)
            if (Slots[i] >= 0 && Next[i] != IntPtr.Zero)
                table[Slots[i]] = mine[i];
        _table = table;
    }

    private static void Forget() => (_drawKnown, _readKnown, _viewportKnown) = (false, false, false);

    [UnmanagedCallersOnly]
    private static void GetIntegerv(uint name, int* data)
    {
        if (Enabled && data != null && _table is not null)
            switch (name)
            {
                case DrawBinding when _drawKnown:
                    *data = (int)_draw;
                    return;
                case ReadBinding when _readKnown:
                    *data = (int)_read;
                    return;
                case Viewport when _viewportKnown:
                    (data[0], data[1], data[2], data[3]) = _viewport;
                    return;
            }

        ((delegate* unmanaged<uint, int*, void>)Next[GetAt])(name, data);
        if (data == null) return;
        switch (name)
        {
            case DrawBinding:
                (_draw, _drawKnown) = ((uint)*data, true);
                break;
            case ReadBinding:
                (_read, _readKnown) = ((uint)*data, true);
                break;
            case Viewport:
                (_viewport, _viewportKnown) = ((data[0], data[1], data[2], data[3]), true);
                break;
        }
    }

    [UnmanagedCallersOnly]
    private static void Bind(uint target, uint framebuffer)
    {
        Bound(target, framebuffer);
        ((delegate* unmanaged<uint, uint, void>)Next[BindAt])(target, framebuffer);
    }

    [UnmanagedCallersOnly]
    private static void BindExt(uint target, uint framebuffer)
    {
        Bound(target, framebuffer);
        ((delegate* unmanaged<uint, uint, void>)Next[BindExtAt])(target, framebuffer);
    }

    private static void Bound(uint target, uint framebuffer)
    {
        if (target is Framebuffer or Draw) (_draw, _drawKnown) = (framebuffer, true);
        if (target is Framebuffer or Read) (_read, _readKnown) = (framebuffer, true);
        if (target is not (Framebuffer or Draw or Read)) Forget();
    }

    [UnmanagedCallersOnly]
    private static void Delete(int count, uint* framebuffers)
    {
        Deleted(count, framebuffers);
        ((delegate* unmanaged<int, uint*, void>)Next[DeleteAt])(count, framebuffers);
    }

    [UnmanagedCallersOnly]
    private static void DeleteExt(int count, uint* framebuffers)
    {
        Deleted(count, framebuffers);
        ((delegate* unmanaged<int, uint*, void>)Next[DeleteExtAt])(count, framebuffers);
    }

    // Deleting a bound framebuffer binds the window's (0)
    private static void Deleted(int count, uint* framebuffers)
    {
        if (framebuffers == null || count <= 0) return;
        for (var i = 0; i < Math.Min(count, 1 << 16); i++)
        {
            if (framebuffers[i] == 0) continue;
            if (framebuffers[i] == _draw) _draw = 0;
            if (framebuffers[i] == _read) _read = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void SetViewport(int x, int y, int width, int height)
    {
        // A negative size is an error that leaves the viewport as it was
        if (width >= 0 && height >= 0) (_viewport, _viewportKnown) = ((x, y, width, height), true);
        ((delegate* unmanaged<int, int, int, int, void>)Next[ViewportAt])(x, y, width, height);
    }

    [UnmanagedCallersOnly]
    private static void ViewportIndexed(uint index, float x, float y, float width, float height)
    {
        _viewportKnown = false;
        ((delegate* unmanaged<uint, float, float, float, float, void>)Next[ViewportAt + 1])(index, x, y, width, height);
    }

    [UnmanagedCallersOnly]
    private static void ViewportIndexedv(uint index, float* values)
    {
        _viewportKnown = false;
        ((delegate* unmanaged<uint, float*, void>)Next[ViewportAt + 2])(index, values);
    }

    [UnmanagedCallersOnly]
    private static void ViewportArray(uint first, int count, float* values)
    {
        _viewportKnown = false;
        ((delegate* unmanaged<uint, int, float*, void>)Next[ViewportAt + 3])(first, count, values);
    }
}
