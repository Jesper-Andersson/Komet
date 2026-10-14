using Vintagestory.Client;

namespace Komet.Bench;

// The default framebuffer after the GUI, read in the next frame's Done stage
internal sealed class BenchShots(string folder) : IRenderer
{
    private string? _pending;

    public double RenderOrder => 10;
    public int RenderRange => 0;

    public void Take(string name)
    {
        if (!NotNull(name) || !Assert(name.EndsWith(".png", StringComparison.Ordinal))) return;
        _pending = Path.Combine(folder, name);
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (_pending is not { } path || !Assert(stage == EnumRenderStage.Done)) return;
        _pending = null;
        try
        {
            ScreenManager.Platform.LoadFrameBuffer(EnumFrameBuffer.Default);
            _ = ScreenManager.Platform.SaveScreenshot(folder, path, flip: true); // glReadPixels reads bottom up
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ScreenManager.Platform.Logger.Warning("Komet bench: no screenshot {0}: {1}", path, e.Message);
        }
    }

    public void Dispose() => _ = Assert(folder.Length > 0) && Assert(_pending is null || _pending.Length > 0);
}
