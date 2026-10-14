using Vintagestory.Client;

namespace Komet.Diagnostics;

internal sealed class FrameStats
{
    private const int History = 2000; // the 0.1 % low needs a thousand frames
    internal const int MaxFrames = 1_000_000; // the longest sorted run LowFps takes: the bench recorder's capacity
    private const float MaxFrameSeconds = 10; // a stall (world load, a debugger), not a frame time
    private readonly float[] _history = new float[History], _sorted = new float[History], _gcMs = new float[History];
    private float _elapsed;
    private int _next, _drawCallsAtLastSample;

    public int Frames { get; private set; }

    public int HistoryLength => _history.Length;

    // NaN for an empty window: "FPS: 0" would be a claim
    public float Fps => Frames == 0 || _elapsed <= 0 ? float.NaN : Frames / _elapsed;
    public float AverageMs => Frames == 0 ? float.NaN : _elapsed / Frames * 1000f;
    public float WorstMs { get; private set; } = float.NaN;
    public float WorstGcMs { get; private set; } = float.NaN; // how much of it the collector held every thread
    public float Low1Fps { get; private set; } = float.NaN;
    public float Low01Fps { get; private set; } = float.NaN;
    public int Recorded { get; private set; }
    public int DrawCallsPerFrame { get; private set; }

    // The engine zeroes these per frame and sets them once a second: only nonzero readings are kept
    public int RenderedTriangles { get; private set; }
    public int AvailableTriangles { get; private set; }

    // Steady frames only: a load stall or pause gap would own the lows for a thousand frames.
    public void Record(float deltaTime, float gcMs, bool steady)
    {
        if (!Assert(deltaTime >= 0) || deltaTime >= MaxFrameSeconds || !Index(_next, History)) return;
        if (!Assert(float.IsNaN(gcMs) || gcMs >= 0)) gcMs = float.NaN;
        Frames++;
        _elapsed += deltaTime;
        if (steady)
        {
            (_history[_next], _gcMs[_next]) = (deltaTime * 1000f, gcMs);
            _next = (_next + 1) % History;
            if (Recorded < History) Recorded++;
        }

        if (RuntimeStats.renderedTriangles != 0) RenderedTriangles = RuntimeStats.renderedTriangles;
        if (RuntimeStats.availableTriangles != 0) AvailableTriangles = RuntimeStats.availableTriangles;
    }

    public float HistoryMs(int index) => Index(index, History) ? _history[(_next + index) % History] : 0;

    // drawCallsCount resets only at start and Alt+F3 and wraps after hours: unchecked delta; a drop means a reset.
    public void SampleWindow()
    {
        var calls = RuntimeStats.drawCallsCount;
        var drawn = unchecked(calls - _drawCallsAtLastSample);
        if (drawn < 0) drawn = Math.Max(calls, 0);
        if (Frames > 0) DrawCallsPerFrame = drawn / Frames;
        _drawCallsAtLastSample = calls;
        if (Recorded == 0 || !Assert(Recorded <= History)) return;
        var history = _history.AsSpan(0, Recorded);
        var worst = 0;
        for (var i = 1; i < Math.Min(Recorded, History); i++)
            if (history[i] > history[worst]) worst = i;
        (WorstMs, WorstGcMs) = (history[worst], _gcMs[worst]);
        var window = _sorted.AsSpan(0, Recorded);
        history.CopyTo(window);
        window.Sort();
        (Low1Fps, Low01Fps) = (LowFps(window, 100), LowFps(window, 1000));
    }

    internal static float Percentile(ReadOnlySpan<float> sorted, int perMille)
    {
        if (sorted.IsEmpty || !Assert(perMille is > 0 and <= 1000)) return float.NaN;
        var rank = ((long)perMille * sorted.Length + 999) / 1000; // ceil(p · n), 1-based
        return Assert(rank >= 1) && Index((int)rank - 1, sorted.Length) ? sorted[(int)rank - 1] : sorted[^1];
    }

    // 1000·k / Σ(worst k ms), k = n / share (100: 1 % low, 1000: 0.1 % low); NaN until share frames. The bench uses it
    // too.
    internal static float LowFps(ReadOnlySpan<float> sorted, int share)
    {
        if (!Assert(share is 100 or 1000) || sorted.Length < share) return float.NaN;
        var worst = sorted[^(sorted.Length / share)..];
        if (!Assert(worst.Length <= MaxFrames) || !Assert(worst[0] <= worst[^1])) return float.NaN;
        float sum = 0;
        for (var i = 0; i < Math.Min(worst.Length, MaxFrames); i++) sum += worst[i];
        return Assert(sum >= 0) && Finite(sum) && sum > 0 ? 1000f * worst.Length / sum : 0;
    }

    public void ResetHistory()
    {
        Array.Clear(_history);
        Array.Clear(_gcMs);
        (_next, Recorded) = (0, 0);
        (WorstMs, WorstGcMs, Low1Fps, Low01Fps) = (float.NaN, float.NaN, float.NaN, float.NaN);
    }

    public void Reset() => (Frames, _elapsed) = (0, 0f);
}
