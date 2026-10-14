using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Core;
using Komet.Rendering;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Classic-path mesh updates (liquid pools) taken like FacePacking's. Prefixes UpdateMesh, as the JIT inlines updateVAO
// (patching that alone missed every call). GL binds stay the engine's; mapped, empty and GL-error-checked uploads stay
// the engine's. Body pinned (EngineShape).
internal static class PoolUpdates
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x4C06C5B8592915BCUL;

    private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

    private static bool _shaped;

    // null leaves every upload to the engine
    private static FacesThrough? Through => Rendering.FacePacking.Through;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateIndices")]
    private static extern void UpdateIndices(ClientPlatformWindows platform, int[] indices, int offset, int count, VAO vao,
        bool pers);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        _shaped = false;
        if (!NotNull(harmony) || Seams() is not [{ } update]) return;
        _shaped = EngineShape.Matches([update], fingerprint, nameof(PoolUpdates), logger);
        if (_shaped) _ = NotNull(harmony.Patch(update, new HarmonyMethod(typeof(PoolUpdates), nameof(Updating))));
    }

    internal static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.UpdateMesh),
                [typeof(MeshRef), typeof(MeshData)])
        ];
        return Assert(seams.Length == 1) ? seams : [];
    }

    // false when the update was made here
    internal static bool Updating(ClientPlatformWindows __instance, MeshRef modelRef, MeshData data)
    {
        if (!_shaped || Through is null || modelRef is not VAO vao || data is null || !NotNull(__instance) ||
            (__instance.GlErrorChecking && __instance.GlDebugMode) || !Assert(data.VerticesCount >= 0)) return true;
        var platform = __instance;
        if (data.xyz is { } xyz)
            Attribute(platform, vao, xyz, Bytes(xyz, data.XyzCount), (data.XyzOffset, data.XyzCount),
                (vao.xyzVboId, vao.xyzPtr));
        if (data.Normals is { } normals && data.VerticesCount > 0)
            Attribute(platform, vao, normals, Bytes(normals, data.VerticesCount), (data.NormalsOffset, data.VerticesCount),
                (vao.normalsVboId, vao.normalsPtr));
        if (data.Uv is { } uv && data.UvCount > 0)
            Attribute(platform, vao, uv, Bytes(uv, data.UvCount), (data.UvOffset, data.UvCount), (vao.uvVboId, vao.uvPtr));
        if (data.Rgba is { } rgba && data.RgbaCount > 0)
            Attribute(platform, vao, rgba, Bytes(rgba, data.RgbaCount), (data.RgbaOffset, data.RgbaCount),
                (vao.rgbaVboId, vao.rgbaPtr));
        if (data.Flags is { } flags && data.FlagsCount > 0)
            Attribute(platform, vao, flags, Bytes(flags, data.FlagsCount), (data.FlagsOffset, data.FlagsCount),
                (vao.flagsVboId, vao.flagsPtr));
        if (data.CustomFloats is { Count: > 0 } floats)
            Attribute(platform, vao, floats.Values, Bytes(floats.Values, floats.Count), (floats.BaseOffset, floats.Count),
                (vao.customDataFloatVboId, vao.customDataFloatPtr));
        if (data.CustomShorts is { Count: > 0 } shorts)
            Attribute(platform, vao, shorts.Values, Bytes(shorts.Values, shorts.Count), (shorts.BaseOffset, shorts.Count),
                (vao.customDataShortVboId, vao.customDataShortPtr));
        if (data.CustomInts is { Count: > 0 } ints)
            Attribute(platform, vao, ints.Values, Bytes(ints.Values, ints.Count), (ints.BaseOffset, ints.Count),
                (vao.customDataIntVboId, vao.customDataIntPtr));
        if (data.CustomBytes is { Count: > 0 } bytes)
            Attribute(platform, vao, bytes.Values, Bytes(bytes.Values, bytes.Count), (bytes.BaseOffset, bytes.Count),
                (vao.customDataByteVboId, vao.customDataBytePtr));
        GL.BindBuffer((BufferTarget)ArrayBuffer, 0);
        Indices(platform, vao, data.Indices, (data.IndicesOffset, data.IndicesCount));
        return false;
    }

    private static void Attribute(ClientPlatformWindows platform, VAO vao, Array data, ReadOnlySpan<byte> bytes,
        (int Offset, int Count) at, (int Vbo, nint Ptr) to)
    {
        if (NotNull(data) && !Taken(vao, to.Vbo, at.Offset, bytes))
            Rendering.FacePacking.Upload(platform, data, at, to, vao.Persistent);
    }

    private static void Indices(ClientPlatformWindows platform, VAO vao, int[]? indices, (int Offset, int Count) at)
    {
        if (indices is null || !NotNull(vao)) return; // the engine's own early return
        var data = Bytes(indices, at.Count);
        if (vao.Persistent || data.IsEmpty || Through is not { } through || !through(vao, vao.vboIdIndex, at.Offset, data))
        {
            UpdateIndices(platform, indices, at.Offset, at.Count, vao, vao.Persistent);
            return;
        }

        GL.BindBuffer((BufferTarget)ElementArrayBuffer, vao.vboIdIndex);
        GL.BindBuffer((BufferTarget)ElementArrayBuffer, 0);
        vao.IndicesCount = at.Count;
        _ = Assert(vao.IndicesCount == at.Count);
    }

    // GL_ARRAY_BUFFER stays on the buffer, as the engine does
    private static bool Taken(VAO vao, int buffer, int offset, ReadOnlySpan<byte> data)
    {
        if (!NotNull(vao) || vao.Persistent || data.IsEmpty || Through is not { } through || !Assert(offset >= 0) ||
            !through(vao, buffer, offset, data)) return false;
        GL.BindBuffer((BufferTarget)ArrayBuffer, buffer);
        return Assert(buffer > 0);
    }

    // Empty when null or short: the engine's own call then fails
    private static ReadOnlySpan<byte> Bytes<T>(T[]? data, int count) where T : unmanaged
    {
        if (data is null || !Assert(count >= 0) || count > data.Length) return [];
        return MemoryMarshal.AsBytes(data.AsSpan(0, count));
    }
}
