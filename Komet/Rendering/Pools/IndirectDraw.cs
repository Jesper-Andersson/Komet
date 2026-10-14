using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Core;
using Komet.Diagnostics;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// RenderMesh's glMultiDrawElements passes a client-side range list most drivers walk per draw on the CPU; as GPU
// indirect commands it is one command. Needs ARB_multi_draw_indirect (else engine path, e.g. macOS). Commands go into a
// ring in one persistently mapped buffer (ARB_buffer_storage), else each draw orphans a fresh store.
internal static class IndirectDraw
{
    private const int MaxRanges = 65536, CommandBytes = 20, MaxErrors = 64;
    private const long SyncTimeoutNs = 1_000_000_000; // the GPU is wedged long before this

    private const int Segments = 4, SegmentBytes = MaxRanges * CommandBytes;

    private static readonly IntPtr[] Fences = new IntPtr[Segments];
    private static int _buffer;
    private static IntPtr _mapped; // zero while the ring is unavailable
    private static int _segment, _used;
    private static Command[] _commands = [];
    public static bool Enabled { get; set; } = true;
    public static bool Detected { get; private set; } // the first draw asks the driver
    public static bool Supported { get; private set; }
    public static bool Persistent { get; private set; }
    public static long Draws { get; private set; } // totals while Counting.Hud, main thread
    public static long Ranges { get; private set; }

    // Sees every draw first, feature on or off: true holds it (OcclusionCulling issues it later)
    internal static System.Func<MeshRef, bool, bool>? Intercept { get; set; }

    // Renderer drawing the pool itself (Vulkan); true when it did. Starts are int pairs, as glMultiDrawElements takes.
    internal delegate bool Drawing(MeshRef modelRef, int[] indices, int[] sizes, int count, bool useSSBOs);

    internal static Drawing? Takeover { get; set; }

    // Vulkan-only: untaken pools draw the engine way (glMultiDrawElements), not via Komet's GL buffer Vulkan can't see
    internal static bool Plain { get; set; }

    public static void Install(Harmony harmony)
    {
        _commands = []; // the GL buffer outlives the world, the list need not
        var render = AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.RenderMesh),
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)]);
        var prefix = new HarmonyMethod(RenderMesh);
        if (!NotNull(harmony) || !NotNull(render) || !Assert(Unsafe.SizeOf<Command>() == CommandBytes) ||
            !Assert(Il.Binds(render, prefix.method))) return;
        _ = NotNull(harmony.Patch(render, prefix));
    }

    private static bool RenderMesh(MeshRef modelRef, int[] indices, int[] indicesSizes, int groupCount, bool useSSBOs)
    {
        if (Intercept?.Invoke(modelRef, useSSBOs) == true) return false;
        if (Takeover?.Invoke(modelRef, indices, indicesSizes, groupCount, useSSBOs) == true) return false;
        if (!Enabled || Plain || modelRef is not VAO vao) return true;
        if (!Detected) (Supported, Detected) = (Detect(), true);
        if (!Supported) return true;
        if (!Assert(groupCount >= 0) || !Assert(indices.Length >= 2 * groupCount) ||
            !Assert(indicesSizes.Length >= groupCount) || groupCount > MaxRanges) return true;
        if (groupCount == 0)
        {
            RuntimeStats.drawCallsCount++;
            return false;
        }

        var at = Upload(indices, indicesSizes, groupCount);
        if (at < 0) return true;
        RuntimeStats.drawCallsCount++;
        if (Counting.Hud) (Draws, Ranges) = (Draws + 1, Ranges + groupCount);
        GL.BindVertexArray(vao.VaoId);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer,
            useSSBOs ? ClientPlatformAbstract.singleIndexBufferId : vao.vboIdIndex);
        if (useSSBOs) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, vao.xyzVboId);
        GL.MultiDrawElementsIndirect(vao.drawMode, DrawElementsType.UnsignedInt, at, groupCount, 0);
        if (useSSBOs) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
        GL.BindVertexArray(0);
        return false;
    }

    // Ranges as commands at the returned byte offset in the indirect buffer, -1 for the engine path. Engine starts are
    // byte offsets of uint indices, every other int.
    private static unsafe int Upload(int[] indices, int[] indicesSizes, int groupCount)
    {
        var bytes = groupCount * CommandBytes;
        if (!Assert(bytes <= SegmentBytes)) return -1;
        if (_commands.Length < groupCount)
            _commands = new Command[Math.Min(MaxRanges, Math.Max(groupCount, 2 * _commands.Length))];
        var staged = _commands.AsSpan(0, groupCount);
        // staged in cached memory: the mapping is write combined, partial line stores are slow
        for (var i = 0; i < Math.Min(staged.Length, MaxRanges); i++)
            staged[i] = new Command { Count = indicesSizes[i], InstanceCount = 1, FirstIndex = indices[2 * i] / 4 };
        if (_mapped == IntPtr.Zero)
        {
            GL.BindBuffer(BufferTarget.DrawIndirectBuffer, _buffer);
            GL.BufferData(BufferTarget.DrawIndirectBuffer, bytes, _commands, BufferUsageHint.StreamDraw);
            return 0;
        }

        if (_used + bytes > SegmentBytes) Lap();
        var at = _segment * SegmentBytes + _used;
        staged.CopyTo(new Span<Command>((void*)(_mapped + at), groupCount));
        _used += bytes;
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, _buffer);
        return at;
    }

    // Fences the segment left, waits on the one entered; a wait means the GPU lags frames
    private static void Lap()
    {
        if (Fences[_segment] != IntPtr.Zero) GL.DeleteSync(Fences[_segment]);
        Fences[_segment] = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        (_segment, _used) = ((_segment + 1) % Segments, 0);
        var fence = Fences[_segment];
        if (fence == IntPtr.Zero) return;
        _ = GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, SyncTimeoutNs);
        GL.DeleteSync(fence);
        Fences[_segment] = IntPtr.Zero;
    }

    private static bool Detect()
    {
        if (!GpuStats.Offered("GL_ARB_multi_draw_indirect")) return false;
        _buffer = GL.GenBuffer();
        if (!Assert(_buffer != 0)) return false;
        if (!GpuStats.Offered("GL_ARB_buffer_storage")) return true;
        Array.Clear(Fences);
        (_segment, _used) = (0, 0);
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, _buffer);
        const MapBufferAccessMask access = MapBufferAccessMask.MapWriteBit | MapBufferAccessMask.MapPersistentBit |
                                           MapBufferAccessMask.MapCoherentBit;
        var storage = (BufferStorageFlags)access; // same bits; OpenTK's BufferStorageFlags lacks [Flags]
        for (var drain = 0; drain < MaxErrors && GL.GetError() != ErrorCode.NoError; drain++)
            _ = Assert(false); // a pending error would be read as ours; report it once
        GL.BufferStorage(BufferTarget.DrawIndirectBuffer, Segments * SegmentBytes, IntPtr.Zero, storage);
        if (GL.GetError() == ErrorCode.NoError)
            _mapped = GL.MapBufferRange(BufferTarget.DrawIndirectBuffer, IntPtr.Zero, Segments * SegmentBytes, access);
        Persistent = _mapped != IntPtr.Zero;
        if (Persistent) return true;
        // BufferStorage may have made the store immutable before failing, which would refuse BufferData
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
        GL.DeleteBuffer(_buffer);
        _buffer = GL.GenBuffer();
        _ = Assert(_buffer != 0);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Command // DrawElementsIndirectCommand
    {
        public int Count, InstanceCount, FirstIndex, BaseVertex, BaseInstance;
    }
}
