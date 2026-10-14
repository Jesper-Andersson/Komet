using Vintagestory.API.MathTools;
using Komet.Gpu;

namespace Komet.Rendering;

// Ranges, entries, commands numbered across the frame (pass 1 commands from MaxRanges): the surfaces' second pass
// covers all calls in one dispatch. Commands land in GPU thread order; engine order cost more (bench culling1440).
internal static partial class OcclusionCulling
{
    private const int Ring = 4, StatUints = 8, CommandBytes = 20, RangeBytes = 32, Group = 64;
    private const int ParamsBinding = 14, RangesBinding = 4, CountersBinding = 5, VerdictsBinding = 6;
    private const int CommandsBinding = 7, StartsBinding = 8;
    // stats, then counts of pass 0, pass 1, shadow draws (from MaxDraws on)
    private const int CounterUints = StatUints + 4 * MaxDraws, FarCounts = StatUints + 3 * MaxDraws;
    private const int ShadowBase = 2 * MaxRanges, FarBase = 3 * MaxRanges; // shadow, then far cache commands, after the camera's
    private const int Test = 0, Again = 1, Keep = 2, KeepAgain = 3; // cull.comp's passes

    private static readonly DepthPyramid Depth = new();
    private static readonly int[] CounterBuffers = new int[Ring], ReadBuffers = new int[Ring];
    private static readonly uint[] Stats = new uint[StatUints];
    private static readonly float[] ViewProjection = new float[16], Previous = new float[16];
    private static int _program, _ranges, _verdicts, _starts, _commands, _params, _frame, _frameRanges, _frameDraws;
    private static int _surfaceRanges, _surfaceDraws;
    private static bool _detected, _supported, _captured, _builtThisFrame, _previousReady, _counting, _late, _compared;
    private static double _px, _py, _pz;

    private static IGpuBackend? _gpu;

    private static IGpuBackend Gpu => _gpu ?? GpuBackends.Current;

    public static bool Detected => _detected;
    public static bool Supported => _supported;

    // Oldest frame: 0 ranges, 1 hidden, 2 triangles, 3 hidden; 4..7 pass 1 (surfaces); NaN before the first read
    public static double Stat(int at) =>
        _counting && Index(at, StatUints) && Assert(Stats.Length == StatUints) ? Stats[at] : double.NaN;

    // Triangles finally not drawn, as share of the frustum's; and ranges pass 1 drew late
    public static double CulledPercent => Stat(2) > 0 ? 100 * CulledTriangles / Stat(2) : double.NaN;

    public static double CulledTriangles => Stat(3) - LateTriangles;

    public static double LateRanges => Stat(4) - Stat(5);

    public static double LateTriangles => Stat(6) - Stat(7);

    private static bool Ready()
    {
        if (_detected && !ReferenceEquals(_gpu, GpuBackends.Current)) Release(); // backend switched
        if (_detected) return _supported;
        (_detected, _gpu) = (true, GpuBackends.Current);
        if (_logger is null || !Gpu.Supported() || !Depth.Ready(_logger)) return false;
        _program = Gpu.Program(GpuShaders.Cull, _logger);
        (_ranges, _verdicts, _starts, _commands, _params) =
            (Gpu.Buffer(), Gpu.Buffer(), Gpu.Buffer(), Gpu.Buffer(), Gpu.Buffer());
        Gpu.Upload<uint>(_ranges, [], MaxRanges * RangeBytes);
        Gpu.Upload<uint>(_verdicts, [], Math.Max(MaxRanges, MaxRows) * 4); // per range, or per row (rows.comp)
        Gpu.Upload<uint>(_starts, [], 2 * MaxDraws * 4);
        Gpu.Upload<uint>(_commands, [], 4 * MaxRanges * CommandBytes);
        for (var slot = 0; slot < Ring; slot++)
        {
            CounterBuffers[slot] = Gpu.Buffer();
            Gpu.Upload<uint>(CounterBuffers[slot], [], 4 * CounterUints);
            Gpu.Zero(CounterBuffers[slot], 0, CounterUints);
            ReadBuffers[slot] = Gpu.Buffer();
            Gpu.Readable(ReadBuffers[slot], 4 * StatUints);
        }

        _supported = _program != 0;
        if (_supported) ReadyRows();
        return Assert(CounterBuffers[Ring - 1] != 0) && _supported;
    }

    private static bool Room(int ranges, int draws) =>
        Assert(ranges >= 0 && draws >= 0) && _frameRanges + ranges <= MaxRanges && _frameDraws + draws <= MaxDraws;

    // Frame matrices, once, from the first held call
    private static bool Capture()
    {
        if (_captured) return Finite(ViewProjection[15]);
        _captured = _api?.Render is { } render && Occlusion.Capture(render, ViewProjection);
        return _captured;
    }

    private static void BeginFrame(bool engineless = false)
    {
        (_frameRanges, _frameDraws, _surfaceRanges, _surfaceDraws, _builtThisFrame, _captured) = (0, 0, 0, 0, false, false);
        (_camera, _view, _late, _compared) = (null, default, false, false);
        ClearBindings();
        BeginRows(engineless);
        Gpu.Zero(CounterBuffers[_frame % Ring], 0, StatUints);
        _ = Assert(_frame >= 0);
    }

    // FinishSurfaces' pyramid is next frame's previous. One build a frame: each stalls; a second cost more than it hid.
    private static void EndFrame()
    {
        _previousReady = _builtThisFrame;
        // for Sample; every pass ended in a barrier covering the copy
        Gpu.Copy(CounterBuffers[_frame % Ring], ReadBuffers[_frame % Ring], 0, StatUints);
        _frame++;
        _ = Assert(_frameRanges <= MaxRanges) && Assert(!_late);
    }

    private static FrameBufferRef? DepthBuffer()
    {
        if (!NotNull(_api)) return null;
        return _api.Render.FrameBuffers is { Count: > 0 } buffers && buffers[0] is { Width: > 0, Height: > 0 } primary
            ? primary
            : null;
    }

    private static void Cull((int First, int Count) ranges, (int First, int Count) draws,
        (FrustumCulling Culler, Vec3d Camera)? rows = null, bool staged = false)
    {
        if ((rows is null && ranges.Count == 0) || !Assert(draws.Count is > 0 and <= MaxDraws) ||
            !Assert(ranges.First + ranges.Count <= MaxRanges)) return;
        var counters = CounterBuffers[_frame % Ring];
        if (rows is { } row) UploadRows(draws, row.Culler, row.Camera, (0, _backFaces));
        else if (!staged)
            Gpu.UploadAt<Occlusion.Range>(_ranges, ranges.First * RangeBytes, Ranges.AsSpan(ranges.First, ranges.Count));
        Prime(draws, counters, both: true);
        if (rows is null) Bind(counters);
        else BindRows(counters);
        var shift = _surface && _camera is { } camera ? (camera.X - _px, camera.Y - _py, camera.Z - _pz) : (0, 0, 0);
        var test = _previousReady ? Test : Keep;
        var (matrix, flags) = _surface ? (Previous, (0, test, StatUints, 0)) : (ViewProjection, (1, Test, StatUints, 0));
        if (rows is null) Pass(matrix, shift, flags, (ranges, draws));
        else RowsPass(matrix, shift, flags, draws, _callMaxRows);
        var sorted = rows is not null && !_surface && Orderless() && Sorted(draws); // the surfaces blend
        if (_surface)
        {
            (_late, _compared) = (true, _previousReady);
            (_surfaceRanges, _surfaceDraws) = (ranges.First + ranges.Count, draws.First + draws.Count);
            if (rows is not null)
                (_surfaceMaxRows, _surfaceRows, _surfaceBackFaces) =
                    (Math.Max(_surfaceMaxRows, _callMaxRows), true, _surfaceBackFaces && _backFaces);
        }

        Issue(draws, (0, StatUints), counters, sorted);
        if (rows is { } far && FarCache is { Refresh: true } cut) FarPass(cut, draws, far, counters);
        if (_surface) Remember(draws);
        (_frameRanges, _frameDraws) = (ranges.First + ranges.Count, draws.First + draws.Count);
        Gpu.Texture(DepthPyramid.Unit, 0);
    }

    private static void FinishSurfaces()
    {
        if (!Assert(_late) || !Assert(_surfaceRanges <= MaxRanges)) return;
        _late = false;
        var depth = DepthBuffer();
        var texture = depth is null ? 0 : Gpu.Engine(depth.DepthTextureId); // Vulkan: the frame's image
        if (depth is not null && texture > 0) _ = Depth.Build(texture, depth.Width, depth.Height);
        _builtThisFrame = texture > 0; // without it the later passes stay the engine's
        if (_builtThisFrame && _camera is { } camera && Assert(_captured))
        {
            ViewProjection.CopyTo(Previous, 0); // the next frame's surfaces test against it
            (_px, _py, _pz) = (camera.X, camera.Y, camera.Z);
        }

        if (!_compared || _surfaceRanges == 0) return;
        var counters = CounterBuffers[_frame % Ring];
        if (_surfaceRows) LateRows(counters, texture != 0);
        else
        {
            Bind(counters);
            Pass(ViewProjection, (0, 0, 0), (1, texture == 0 ? KeepAgain : Again, StatUints + MaxDraws, MaxRanges),
                ((0, _surfaceRanges), (0, _surfaceDraws)));
        }

        IssueLate(counters);
        Gpu.Texture(DepthPyramid.Unit, 0);
    }

    // Range starts uploaded, counts zeroed (pass 0's too when both; shadow counts after pass 1's)
    private static void Prime((int First, int Count) draws, int counters, bool both)
    {
        if (!Assert(counters != 0) || !Assert(draws.Count > 0)) return;
        Gpu.UploadAt<uint>(_starts, draws.First * 4, Starts.AsSpan(draws.First, draws.Count));
        if (both) Gpu.Zero(counters, StatUints + draws.First, draws.Count);
        Gpu.Zero(counters, StatUints + MaxDraws + draws.First, draws.Count);
    }

    private static void Bind(int counters)
    {
        if (!Assert(counters != 0) || !Assert(_ranges != 0)) return;
        Gpu.Storage(RangesBinding, _ranges);
        Gpu.Storage(CountersBinding, counters);
        Gpu.Storage(VerdictsBinding, _verdicts);
        Gpu.Storage(StartsBinding, _starts);
        Gpu.Storage(CommandsBinding, _commands);
    }

    private static void Pass(float[] matrix, (double X, double Y, double Z) shift, (int, int, int, int) flags,
        ((int First, int Count) Ranges, (int First, int Count) Draws) over)
    {
        var (ranges, draws) = over;
        if (!Assert(matrix.Length == 16) || !Assert(ranges.Count > 0) || !Assert(draws.Count > 0)) return;
        Span<float> p = stackalloc float[ShaderParams.TestFloats];
        ShaderParams.Test(p, matrix, shift, Depth, ranges, flags, (draws.First, draws.First + draws.Count));
        Gpu.Upload<float>(_params, p);
        Gpu.Uniforms(ParamsBinding, _params);
        Gpu.Texture(DepthPyramid.Unit, Depth.Texture);
        Gpu.Dispatch(_program, (ranges.Count + Group - 1) / Group);
        Gpu.Barrier(GpuBarrier.Indirect | GpuBarrier.Storage | GpuBarrier.Copy);
    }

    // Once per HUD interval while the occlusion panel shows: oldest ring frame
    public static void Sample()
    {
        if (!Active || !_supported || _frame < Ring) return;
        Gpu.Read(ReadBuffers[(_frame + 1) % Ring], 0, Stats);
        _counting = Assert(Stats.Length == StatUints);
    }

    private static void Release()
    {
        ReleaseRows();
        Depth.Release();
        Gpu.DeleteProgram(ref _program);
        Gpu.DeleteBuffer(ref _ranges);
        Gpu.DeleteBuffer(ref _verdicts);
        Gpu.DeleteBuffer(ref _starts);
        Gpu.DeleteBuffer(ref _commands);
        Gpu.DeleteBuffer(ref _params);
        for (var slot = 0; slot < Ring; slot++)
        {
            Gpu.DeleteBuffer(ref CounterBuffers[slot]);
            Gpu.DeleteBuffer(ref ReadBuffers[slot]);
        }

        Array.Clear(Stats);
        (_detected, _supported, _captured, _builtThisFrame, _previousReady, _counting) =
            (false, false, false, false, false, false);
        _gpu = null;
        (_frame, _frameRanges, _frameDraws, _surfaceRanges, _surfaceDraws, _late) = (0, 0, 0, 0, 0, false);
        ClearBindings();
        _ = Assert(_program == 0) && Assert(Depth.Texture == 0);
    }

    // One surface call plus surfaces' end, engine-free (GPU tests); ranges of one draw or split over several
    internal static void RunFrame(ReadOnlySpan<Occlusion.Range> ranges, float[] viewProjection,
        (double X, double Y, double Z) camera, ReadOnlySpan<int> split = default)
    {
        if (!Assert(ranges.Length is > 0 and <= MaxRanges) || !Assert(viewProjection.Length == 16) || !Ready()) return;
        BeginFrame();
        viewProjection.CopyTo(ViewProjection, 0);
        (_captured, _camera, _surface) = (true, new Vec3d(camera.X, camera.Y, camera.Z), true);
        ranges.CopyTo(Ranges);
        int[] counts = split.IsEmpty ? [ranges.Length] : [.. split];
        var start = 0;
        for (var d = 0; d < Math.Min(counts.Length, MaxDraws); d++)
        {
            (Starts[d], Draws[d]) = ((uint)start, new Draw(null!, new Vec3f(), start, counts[d]));
            start += counts[d];
        }

        _ = Assert(start == ranges.Length);
        Cull((0, ranges.Length), (0, counts.Length), staged: Stage(ranges, 0));
        FinishSurfaces();
        EndFrame();
    }
}
