namespace Komet.Vulkan;

internal sealed unsafe class Samplers(IntPtr device) : IDisposable
{
    private const int MaxSamplers = 1024;
    private const uint Nearest = 0, Linear = 1, ClampToEdge = 2, Repeat = 0, LessOrEqual = 3;
    private const int GlLinear = 0x2601, GlNearestMipmapNearest = 0x2700, GlLinearMipmapNearest = 0x2701;
    private const int GlNearestMipmapLinear = 0x2702, GlLinearMipmapLinear = 0x2703, GlRepeat = 0x2901;
    private const int GlClampToBorder = 0x812D;
    private const uint ClampToBorder = 3, OpaqueBlack = 3, OpaqueWhite = 5;

    private readonly Dictionary<State, ulong> _made = [];

    // Mipmaps: 0 none, 1 nearest, 2 linear. Anisotropy 1 is none.
    public readonly record struct State(bool LinearMag, bool LinearMin, int Mipmaps, bool Clamp, bool Compare,
        float Anisotropy = 1)
    {
        public bool Border { get; init; }
        public bool WhiteBorder { get; init; }
    }

    public static State FromGl(int min, int mag, int wrap, bool compare)
    {
        _ = Assert(min >= 0) && Assert(mag >= 0);
        var (linearMin, mipmaps) = Minifying(min);
        return new State(mag == GlLinear, linearMin, mipmaps, wrap != GlRepeat, compare)
        {
            Border = wrap == GlClampToBorder
        };
    }

    // State after one glTexParameter: FromGl's parameters (and border red) change it, others keep it; null if unknown
    public static State? Tuned(State state, uint name, float value)
    {
        var v = (int)value;
        _ = Assert(Finite(value)) && Assert(state.Mipmaps is >= 0 and <= 2);
        return name switch
        {
            0x2801 => Minifying(v) is var (linear, mipmaps) ? state with { LinearMin = linear, Mipmaps = mipmaps } : null,
            0x2800 => state with { LinearMag = v == GlLinear },
            0x2802 => state with { Clamp = v != GlRepeat, Border = v == GlClampToBorder },
            0x884C => state with { Compare = v != 0 },
            0x1004 => state with { WhiteBorder = value > 0.5f },
            0x2803 or 0x8072 or 0x884D or 0x84FE => state,
            _ => null
        };
    }

    private static (bool Linear, int Mipmaps) Minifying(int min)
    {
        _ = Assert(min >= 0) && Assert(GlLinearMipmapLinear > GlLinear);
        var mipmaps = min switch
        {
            GlNearestMipmapNearest or GlLinearMipmapNearest => 1,
            GlNearestMipmapLinear or GlLinearMipmapLinear => 2,
            _ => 0
        };
        return (min is GlLinear or GlLinearMipmapNearest or GlLinearMipmapLinear, mipmaps);
    }

    public ulong Get(State state)
    {
        if (!Assert(device != IntPtr.Zero) || !Assert(state.Mipmaps is >= 0 and <= 2)) return 0;
        if (_made.TryGetValue(state, out var known)) return known;
        var address = state.Clamp ? ClampToEdge : Repeat;
        if (state.Border) address = ClampToBorder;
        var info = new Vk.SamplerInfo
        {
            SType = Vk.SamplerCreateInfo, MagFilter = state.LinearMag ? Linear : Nearest,
            MinFilter = state.LinearMin ? Linear : Nearest, MipmapMode = state.Mipmaps == 2 ? Linear : Nearest,
            AddressU = address, AddressV = address, AddressW = address, MaxLod = state.Mipmaps == 0 ? 0.25f : 1000,
            Anisotropy = state.Anisotropy > 1 ? 1u : 0, MaxAnisotropy = Math.Max(state.Anisotropy, 1),
            Compare = state.Compare ? 1u : 0, CompareOp = LessOrEqual,
            Border = state.WhiteBorder ? OpaqueWhite : OpaqueBlack
        };
        ulong sampler;
        if (_made.Count >= MaxSamplers || VkApi.CreateSampler(device, &info, null, &sampler) != Vk.Success) return 0;
        _made[state] = sampler;
        return sampler;
    }

    public void Dispose()
    {
        if (!Assert(device != IntPtr.Zero) || !Assert(_made.Count <= MaxSamplers)) return;
        foreach (var sampler in _made.Values.Bounded(MaxSamplers)) VkApi.DestroySampler(device, sampler, null);
        _made.Clear();
    }
}
