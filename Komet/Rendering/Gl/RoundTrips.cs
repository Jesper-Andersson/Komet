using HarmonyLib;
using Komet.Core;
using Komet.Diagnostics;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// ClientSize is a GLFW call (X11 round trip) made a dozen times a frame; size changes only via the setter or OnResize.
internal static class WindowSizeCache
{
    private static Vector2i _size;
    private static bool _valid;
    public static bool Enabled { get; set; } = true;

    public static void Install(Harmony harmony)
    {
        _valid = false; // nothing watched the window between the sessions
        var get = AccessTools.PropertyGetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize));
        var set = AccessTools.PropertySetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize));
        var resize = AccessTools.Method(typeof(NativeWindow), "OnResize", [typeof(ResizeEventArgs)]);
        if (!NotNull(get) || !NotNull(set) || !NotNull(resize)) return;
        if (!NotNull(harmony.Patch(get, new HarmonyMethod(Cached), new HarmonyMethod(Remember)))) return;
        _ = NotNull(harmony.Patch(set, postfix: new HarmonyMethod(Invalidate)));
        _ = NotNull(harmony.Patch(resize, postfix: new HarmonyMethod(Invalidate)));
    }

    private static bool Cached(ref Vector2i __result)
    {
        if (!Enabled || !_valid) return true;
        __result = _size;
        return !(Assert(_size.X >= 0) && Assert(_size.Y >= 0)); // a bad cache hands the call back to OpenTK
    }

    private static void Remember(Vector2i __result)
    {
        if (!Enabled || !Assert(__result.X >= 0) || !Assert(__result.Y >= 0)) return;
        (_size, _valid) = (__result, true);
    }

    private static void Invalidate() => _valid = false;
}

// OnRenderFrame3DPost polls the sun occlusion query every frame (each glGet* a ~10 µs glthread sync). Here the query
// rests and is read once complete, every fourth frame (engine smoothing hides it). Under GL even that read is no glGet:
// a bound query buffer gets availability and result written on the GPU timeline into a persistent mapping, read a frame
// later and marked unwritten before each request. Under Vulkan GlTap answers the query, so it is asked directly.
internal static unsafe class SunOcclusion
{
    private const int RestFrames = 3, MaxSamples = 1500, Unwritten = -1;
    private const GetQueryObjectParam ResultNoWait = (GetQueryObjectParam)0x9194;
    private const BufferStorageFlags Mapped = (BufferStorageFlags)0xC3; // map read, write, persistent, coherent
    private static int _rest, _buffer;
    private static int* _words; // availability, result
    private static bool _issued, _asked, _unbuffered;
    public static bool Enabled { get; set; } = true;

    public static void Install(Harmony harmony)
    {
        (_rest, _issued, _asked) = (0, false, false); // the new world's query name is only reserved until the engine begins it
        var post = AccessTools.Method(typeof(SystemRenderSunMoon), "OnRenderFrame3DPost");
        var prefix = new HarmonyMethod(Prefix);
        if (!NotNull(post) || !Assert(post.GetParameters().Length == 1) ||
            !Assert(Il.Binds(post, prefix.method))) return;
        _ = NotNull(harmony.Patch(post, prefix));
    }

    private static void Prefix(ref bool ___firstTickDone, ref bool ___nowQuerying, ref float ___targetSunSpec,
        int ___occlQueryId)
    {
        if (!Enabled || !Assert(_rest >= 0)) return;
        ___firstTickDone = false; // the engine never polls
        if (_rest > 0) // no new query while the last one matures
        {
            (_rest, ___nowQuerying) = (_rest - 1, true);
            return;
        }

        if (_issued && Assert(___occlQueryId != 0))
        {
            if (!Read(___occlQueryId, out var samples)) // reading it now would wait, and a new query would run over it
            {
                (___nowQuerying, _rest) = (true, 1);
                return;
            }

            ___targetSunSpec = GameMath.Clamp(samples / (float)MaxSamples, 0f, 1f);
        }

        // engine brackets this frame's quad with a query
        (___nowQuerying, _issued, _rest, _asked) = (false, true, RestFrames, false);
    }

    private static bool Read(int query, out int samples)
    {
        samples = 0;
        if (Vulkan.GlTap.Scene || !Buffered())
        {
            GL.GetQueryObject(query, GetQueryObjectParam.QueryResultAvailable, out int ready);
            if (!Assert(ready is 0 or 1) || ready == 0) return false;
            GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out samples);
            return true;
        }

        if (_asked && _words[0] != Unwritten)
        {
            _asked = false;
            if (_words[0] == 1)
            {
                samples = _words[1];
                return true;
            }
        }

        if (_asked) return false; // the GPU has not got to the request yet
        (_words[0], _words[1], _asked) = (Unwritten, Unwritten, true);
        GL.BindBuffer(BufferTarget.QueryBuffer, _buffer);
        GL.GetQueryObject(query, GetQueryObjectParam.QueryResultAvailable, (int*)0);
        GL.GetQueryObject(query, ResultNoWait, (int*)sizeof(int));
        GL.BindBuffer(BufferTarget.QueryBuffer, 0);
        return false;
    }

    // Once per process; without GL 4.5 asked directly
    private static bool Buffered()
    {
        if (_words != null) return true;
        if (_unbuffered) return false;
        // macOS stops at 4.1: DSA and query buffer entry points are null, a call jumps to 0
        _unbuffered = GpuStats.GlVersion < 45;
        if (_unbuffered) return false;
        GL.CreateBuffers(1, out _buffer);
        GL.NamedBufferStorage(_buffer, 2 * sizeof(int), IntPtr.Zero, Mapped);
        _words = (int*)GL.MapNamedBufferRange(_buffer, IntPtr.Zero, 2 * sizeof(int), (BufferAccessMask)Mapped);
        _unbuffered = _words == null;
        return !_unbuffered;
    }
}
