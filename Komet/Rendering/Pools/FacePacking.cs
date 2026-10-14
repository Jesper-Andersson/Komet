using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Core;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// UpdateSSBOMesh packs every quad into a FaceData on the main thread (thousands per chunk part, most of a streaming
// frame's upload time). Workers pack via the engine's FaceData constructor after its UV checks.
internal delegate bool FacesThrough(VAO vao, int buffer, long offset, ReadOnlySpan<byte> data);

internal static class FacePacking
{
    private const int ShaderStorage = 37074, ArrayBuffer = 34962, MaxFaces = 1 << 22;
    private const int ParallelFaces = 4096, FacesPerPart = 2048, MaxParts = 32;
    private const float Low = -1.5E-05f, High = 1.000015f;

    private static readonly Action<int> PackJob = PackPart;
    private static FaceData[] _faces = [];
    private static int[] _pruned = [];

    private static float[] _xyz = [], _uv = [];
    private static int[] _flags = [];
    private static int[]? _ints;
    private static int _stride, _count, _parts;

    public static bool Enabled { get; set; } = true;

    // Takes pool uploads instead of OpenGL (Vulkan); true when it did
    public static FacesThrough? Through { get; set; }

    private static VAO? _uploading;

    private static unsafe FaceData* _into; // where the parts pack: _faces pinned
    public static long Parallel { get; private set; } // uploads the workers helped pack

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateVAO")]
    private static extern void UpdateVao(ClientPlatformWindows platform, float[] data, int offset, int count, int vboId,
        nint vboPtr, bool pers);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateVAO")]
    private static extern void UpdateVao(ClientPlatformWindows platform, int[] data, int offset, int count, int vboId,
        nint vboPtr, bool pers);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateVAO")]
    private static extern void UpdateVao(ClientPlatformWindows platform, short[] data, int offset, int count, int vboId,
        nint vboPtr, bool pers);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateVAO")]
    private static extern void UpdateVao(ClientPlatformWindows platform, byte[] data, int offset, int count, int vboId,
        nint vboPtr, bool pers);

    public static void Install(Harmony harmony)
    {
        var upload = AccessTools.DeclaredMethod(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.UpdateSSBOMesh));
        var prefix = new HarmonyMethod(typeof(FacePacking), nameof(Uploading)) { priority = Priority.Last };
        if (!NotNull(harmony) || !NotNull(upload) || !Assert(Il.Binds(upload, prefix.method)) ||
            !Assert(Marshal.SizeOf<FaceData>() == 64)) return;
        _ = NotNull(harmony.Patch(upload, prefix));
    }

    internal static bool Uploading(ClientPlatformWindows __instance, MeshRef modelRef, MeshData data)
    {
        if (modelRef is VAO uploaded && data?.xyz is not null) FaceSorting.Uploaded(uploaded.VaoId, data); // whoever uploads it
        if (!Enabled || data?.xyz is null || modelRef is not VAO vao || !NotNull(__instance) ||
            (__instance.GlErrorChecking && __instance.GlDebugMode)) return true;
        var vertices = data.VerticesCount;
        var faces = vertices / 4;
        if (!Readable(data, faces) || !Assert(vertices >= 0)) return true; // the engine throws as it would
        var (offset, bytes) = (data.XyzOffset / 12 * 16, 16 * vertices);
        _uploading = vao;
        var bound = false;
        Pack(data, faces);
        if (!Taken(vao.xyzVboId, offset, MemoryMarshal.AsBytes(_faces.AsSpan())[..bytes]))
        {
            GL.BindBuffer((BufferTarget)ShaderStorage, vao.xyzVboId);
            GL.BufferSubData((BufferTarget)ShaderStorage, offset, bytes, _faces);
            bound = true;
        }

        // Unbind only after our bind: alone it held the main thread 9 % of a streaming frame; Vulkan uploads never bind
        if (bound) GL.BindBuffer((BufferTarget)ShaderStorage, 0);
        bool opengl;
        try
        {
            opengl = Attributes(__instance, vao, data);
        }
        finally
        {
            _uploading = null;
        }

        if (opengl) GL.BindBuffer((BufferTarget)ArrayBuffer, 0); // bound only by an upload OpenGL made
        vao.IndicesCount = data.IndicesCount;
        return false;
    }

    // Arrays long enough for the faces; else the engine's loop throws where it would
    private static bool Readable(MeshData data, int faces)
    {
        if (!Assert(faces >= 0) || faces > MaxFaces || data.Uv is not { } uv || data.Flags is not { } flags) return false;
        var (ints, stride) = data.CustomInts is { Count: > 0 } c ? (c.Values, c.InterleaveStride / 4) : (null, 1);
        var last = 4 * faces - 4;
        return faces == 0 || (data.xyz.Length >= 3 * last + 12 && uv.Length >= 2 * last + 6 && flags.Length >= last + 4 &&
                              (ints is null || (stride > 0 && ints.Length > last * stride)));
    }

    private static unsafe void Pack(MeshData data, int faces)
    {
        var size = (16 * data.VerticesCount + 63) / 64; // what the upload reads
        if (_faces.Length < size) _faces = new FaceData[Math.Max(size, _faces.Length * 2)];
        fixed (FaceData* into = _faces)
        {
            if (!Assert(into != null) || !NotNull(data)) return;
            _into = into;
            (_xyz, _uv, _flags, _count) = (data.xyz, data.Uv, data.Flags, faces);
            (_ints, _stride) = data.CustomInts is { Count: > 0 } c ? (c.Values, c.InterleaveStride / 4) : (null, 1);
            _parts = Math.Clamp(faces / FacesPerPart, 1, MaxParts);
            var helped = faces >= ParallelFaces && _parts > 1 && WorkerPool.Free &&
                         WorkerPool.RunFrame(PackJob, _parts, _parts - 1) == WorkerPool.FrameResult.Done;
            if (helped && Counting.Hud) Parallel++;
            if (!helped)
                for (var part = 0; part < Math.Min(_parts, MaxParts); part++) PackPart(part); // a failed batch runs again
            (_xyz, _uv, _flags, _ints) = ([], [], [], null);
            _into = null;
        }

        _ = Assert(_faces.Length >= faces);
    }

    private static unsafe void PackPart(int part)
    {
        var faces = _into;
        if (!Index(part, _parts) || !Assert(faces != null)) return;
        var (from, to) = (_count * part / _parts, _count * (part + 1) / _parts);
        var (xyz, uv, flags, ints, stride) = (_xyz, _uv, _flags, _ints, _stride);
        for (var f = from; f < Math.Min(to, MaxFaces); f++)
            faces[f] = Face(xyz, uv, flags, ints is not null ? ints[4 * f * stride] : 0, f);

        _ = Assert(to <= _count);
    }

    // As the engine's loop packs a face, after its UV checks
    private static FaceData Face(float[] xyz, float[] uv, int[] flags, int colorMap, int f)
    {
        var i = 4 * f;
        float u1 = uv[i * 2], v1 = uv[i * 2 + 1], v1b = uv[i * 2 + 3], u2 = uv[i * 2 + 4], v2 = uv[i * 2 + 5];
        if (u1 < Low || u1 > High || v1 < Low || v1 > High) (u1, v1) = (0, 0);
        if (u2 < Low || u2 > High || v2 < Low || v2 > High) (u2, v2) = (0, 0);
        var rotate = Same(v1, v1b) && Same(u2, uv[i * 2 + 2]);
        return new FaceData(xyz, i * 3, u1, v1, u2 - u1, v2 - v1, flags, i, colorMap, rotate);
    }

    // As the engine compares UV corners
    private static bool Same(float a, float b) => a.CompareTo(b) == 0 && !float.IsNaN(a);

    // Engine uploads of the other attributes; persistent mappings filled as a block. True when OpenGL uploaded.
    private static bool Attributes(ClientPlatformWindows platform, VAO vao, MeshData data)
    {
        if (!NotNull(vao) || !NotNull(data)) return false;
        var (persistent, opengl) = (vao.Persistent, false);
        if (data.Rgba is { } rgba && data.RgbaCount > 0)
            opengl |= Send(platform, rgba, (data.RgbaOffset, data.RgbaCount), (vao.rgbaVboId, vao.rgbaPtr), persistent);
        if (data.CustomFloats is { Count: > 0 } floats)
            opengl |= Send(platform, floats.Values, (floats.BaseOffset, floats.Count),
                (vao.customDataFloatVboId, vao.customDataFloatPtr), persistent);
        if (data.CustomShorts is { Count: > 0 } shorts)
            opengl |= Send(platform, shorts.Values, (shorts.BaseOffset, shorts.Count),
                (vao.customDataShortVboId, vao.customDataShortPtr), persistent);
        if (data.CustomInts is { Count: > 0, InterleaveStride: > 4 } ints) opengl |= Pruned(platform, vao, ints);
        if (data.CustomBytes is { Count: > 0 } bytes)
            opengl |= Send(platform, bytes.Values, (bytes.BaseOffset, bytes.Count),
                (vao.customDataByteVboId, vao.customDataBytePtr), persistent);
        return opengl;
    }

    // Two custom ints per vertex: the second of each goes up, at half the offset
    private static bool Pruned(ClientPlatformWindows platform, VAO vao, CustomMeshDataPartInt ints)
    {
        if (ints.InterleaveStride / 4 != 2) throw new InvalidOperationException("We are assuming 2 customInts per vertex if it is not 1");
        var n = ints.Count / 2;
        if (!Assert(n >= 0) || !NotNull(ints.Values)) return false;
        if (_pruned.Length < n) _pruned = new int[n];
        var values = ints.Values;
        for (var j = 0; j < Math.Min(n, MaxFaces * 4); j++) _pruned[j] = values[j * 2 + 1];
        return Send(platform, _pruned, (ints.BaseOffset / 2, n), (vao.customDataIntVboId, vao.customDataIntPtr), vao.Persistent);
    }

    // True when OpenGL uploaded (and bound GL_ARRAY_BUFFER)
    private static bool Send<T>(ClientPlatformWindows platform, T[] data, (int Offset, int Count) at, (int Vbo, nint Ptr) to,
        bool persistent) where T : unmanaged
    {
        if (!NotNull(data) || !Assert(at.Count >= 0) || !Assert(at.Count <= data.Length)) return false;
        var values = data.AsSpan(0, at.Count);
        if (persistent && Mapped(to, at.Offset, at.Count * Unsafe.SizeOf<T>()))
        {
            values.CopyTo(Target<T>(to.Ptr, at));
            return false;
        }

        if (!persistent && Taken(to.Vbo, at.Offset, MemoryMarshal.AsBytes(values))) return false;
        Upload(platform, data, at, to, persistent);
        return true;
    }

    // Engine updateVAO for its attribute array types (PoolUpdates routes GL uploads here too)
    internal static void Upload(ClientPlatformWindows platform, Array data, (int Offset, int Count) at, (int Vbo, nint Ptr) to,
        bool persistent)
    {
        switch (data)
        {
            case float[] floats: UpdateVao(platform, floats, at.Offset, at.Count, to.Vbo, to.Ptr, persistent); break;
            case int[] ints: UpdateVao(platform, ints, at.Offset, at.Count, to.Vbo, to.Ptr, persistent); break;
            case short[] shorts: UpdateVao(platform, shorts, at.Offset, at.Count, to.Vbo, to.Ptr, persistent); break;
            case byte[] bytes: UpdateVao(platform, bytes, at.Offset, at.Count, to.Vbo, to.Ptr, persistent); break;
        }

        _ = Assert(data is float[] or int[] or short[] or byte[]);
    }

    // GL upload taken by Through (GL_ARRAY_BUFFER bound to 0 at the end either way)
    private static bool Taken(int buffer, long offset, ReadOnlySpan<byte> data)
    {
        if (Through is not { } through || _uploading is not { } vao || !Assert(offset >= 0)) return false;
        return Assert(buffer >= 0) && through(vao, buffer, offset, data);
    }

    // Persistent attribute for the block copy. No bind: nothing in GL follows, and the binding is not VAO state.
    private static bool Mapped((int Vbo, nint Ptr) to, int offset, int bytes) =>
        to.Ptr != 0 && Assert(offset >= 0) && Assert(bytes >= 0) && Assert(to.Vbo >= 0);

    // Mapping at the byte offset (updateVAO steps a typed pointer by offset / size), count elements
    private static unsafe Span<T> Target<T>(nint pointer, (int Offset, int Count) at) where T : unmanaged
    {
        _ = Assert(pointer != 0) && Assert(at.Count >= 0);
        return new Span<T>((T*)pointer + at.Offset / sizeof(T), at.Count);
    }
}
