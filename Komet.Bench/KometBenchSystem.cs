namespace Komet.Bench;

// The developer benchmark's mod ("kometbench", scripts/bench.sh puts it beside the build under test): inert unless KOMET_BENCH
// names a bench.json. After Komet's StartClientSide, so every Komet patch and the HUD are in place when the driver registers.
public sealed class KometBenchSystem : ModSystem
{
    private const double AfterKomet = 1;

    public override double ExecuteOrder() => Assert(AfterKomet > base.ExecuteOrder()) ? AfterKomet : base.ExecuteOrder();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        if (!NotNull(api)) return;
        Benchmark.Install(api);
    }
}
