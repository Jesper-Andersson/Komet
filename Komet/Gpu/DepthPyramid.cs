namespace Komet.Gpu;

// Units lie far above the engine's: a build may run mid terrain pass with its program's textures bound.
internal sealed class DepthPyramid
{
    public const int Unit = 31, DepthUnit = 30;
    private const int ParamsBinding = 14, AboveImage = 6, LevelImage = 7, Tile = 8, MaxLevels = 16;

    private int _program, _params, _texture;
    private IGpuBackend? _gpu;

    // Backend the pyramid was made with; its programs and images belong to it, also after a switch
    private IGpuBackend gpu => _gpu ?? GpuBackends.Current;

    public int Texture => _texture;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Levels { get; private set; }

    public bool Ready(ILogger logger, IGpuBackend? backend = null)
    {
        if (_program != 0) return true;
        _gpu = backend ?? GpuBackends.Current;
        if (!NotNull(logger) || !NotNull(gpu)) return false;
        _program = gpu.Program(GpuShaders.HiZ, logger);
        if (_program != 0 && _params == 0) _params = gpu.Buffer();
        return _program != 0;
    }

    // False when the pyramid was made anew for another size: what it held is gone
    public bool Build(int depth, int depthWidth, int depthHeight)
    {
        if (!Assert(_program != 0) || !Assert(depth > 0 && depthWidth > 0 && depthHeight > 0)) return false;
        var (width, height, kept) = ((depthWidth + 1) / 2, (depthHeight + 1) / 2, true);
        if (width != Width || height != Height)
        {
            gpu.DeleteTexture(ref _texture);
            _texture = gpu.Pyramid(width, height, out var levels);
            (Width, Height, Levels, kept) = (width, height, levels, false);
        }

        gpu.Barrier(GpuBarrier.Attachments); // the draws into the depth buffer, then this build's fetches of it
        gpu.Texture(DepthUnit, depth);
        var (w, h, sw, sh) = (width, height, depthWidth, depthHeight);
        Span<float> p = stackalloc float[ShaderParams.PyramidFloats];
        for (var level = 0; level < Math.Min(Levels, MaxLevels); level++)
        {
            ShaderParams.Ints(p, sw, sh, w, h);
            ShaderParams.Ints(p[4..], level == 0 ? 1 : 0, 0, 0, 0);
            gpu.Upload<float>(_params, p);
            gpu.Uniforms(ParamsBinding, _params);
            if (level > 0) gpu.Image(AboveImage, _texture, level - 1, false);
            gpu.Image(LevelImage, _texture, level, true);
            gpu.Dispatch(_program, (w + Tile - 1) / Tile, (h + Tile - 1) / Tile);
            gpu.Barrier(GpuBarrier.Images | GpuBarrier.Textures);
            (sw, sh, w, h) = (w, h, Math.Max(1, w / 2), Math.Max(1, h / 2)); // the mip chain's sizes
        }

        gpu.Texture(DepthUnit, 0);
        return kept;
    }

    public void Release()
    {
        gpu.DeleteProgram(ref _program);
        gpu.DeleteBuffer(ref _params);
        gpu.DeleteTexture(ref _texture);
        (Width, Height, Levels, _gpu) = (0, 0, 0, null);
        _ = Assert(_texture == 0);
    }
}

// The std140 parameter blocks the shaders declare, written as floats: an ivec4 goes in as its ints' bits
internal static class ShaderParams
{
    public const int PyramidFloats = 8, TestFloats = 32;

    public static void Ints(Span<float> into, int a, int b, int c, int d)
    {
        if (!Assert(into.Length >= 4)) return;
        (into[0], into[1], into[2], into[3]) = (BitConverter.Int32BitsToSingle(a), BitConverter.Int32BitsToSingle(b),
            BitConverter.Int32BitsToSingle(c), BitConverter.Int32BitsToSingle(d));
    }

    public static void Test(Span<float> into, float[] viewProjection, (double X, double Y, double Z) shift,
        DepthPyramid pyramid, (int First, int Count) ranges, (int X, int Y, int Z, int W) flags,
        (int From, int Before) draws = default)
    {
        if (!Assert(into.Length >= TestFloats) || !Assert(viewProjection.Length == 16) || !NotNull(pyramid)) return;
        viewProjection.CopyTo(into);
        (into[16], into[17], into[18], into[19]) = ((float)shift.X, (float)shift.Y, (float)shift.Z, 0);
        Ints(into[20..], pyramid.Width, pyramid.Height, pyramid.Levels, ranges.Count);
        Ints(into[24..], flags.X, flags.Y, flags.Z, flags.W);
        Ints(into[28..], ranges.First, draws.From, draws.Before, 0);
    }
}
