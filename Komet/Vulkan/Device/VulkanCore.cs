using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// The Vulkan device, started on the first frame while Vulkan is on (VulkanRenderer.Only). Nothing is shared with
// OpenGL: the device is OpenGL's GPU by GL_DEVICE_UUID_EXT where offered, else by GL's vendor and renderer names. Off:
// nothing loaded.
internal static class VulkanCore
{
    private static ILogger? _logger;
    private static Hook? _hook;
    private static bool _tried;

    public static bool Enabled => VulkanRenderer.Only;
    public static string Status { get; private set; } = "";
    public static VulkanDevice? Device { get; private set; }

    public static bool Failed => _tried && Device is null;

    public static void Install(ICoreClientAPI api, ILogger logger)
    {
        Stop();
        if (!NotNull(api) || !NotNull(logger) || !NotNull(api.Event)) return;
        _logger = logger;
        _hook = new Hook();
        api.Event.RegisterRenderer(_hook, EnumRenderStage.Before, "komet-vulkan");
    }

    // The world closes: the device goes, the switch stays
    public static void Stop()
    {
        _ = Assert(Device is null || _tried); // a device only comes from a start
        Device?.Dispose();
        (Device, _tried, Status) = (null, false, "");
    }

    // On the render thread, with the GL context current
    internal static void Start()
    {
        if (_tried || !Assert(Device is null) || !Assert(_logger is not null)) return;
        _tried = true;
        Span<byte> uuid = stackalloc byte[VulkanDevice.UuidBytes];
        ReadOnlySpan<byte> named = GlInterop.Uuid(uuid) ? uuid : default; // names its GPU exactly
        var device = VulkanDevice.Create(named, (GL.GetString(StringName.Vendor), GL.GetString(StringName.Renderer)),
            out var why);
        if (device is null)
        {
            Report(why);
            return;
        }

        Device = device;
        Report($"{device.Name} (picked by {device.Picked}): Vulkan-only, nothing shared with OpenGL");
    }

    private static void Report(string status)
    {
        Status = NotNull(status) && Assert(status.Length > 0) ? status : "?";
        _logger?.Notification("Komet: Vulkan {0}", Status);
    }

    private sealed class Hook : IRenderer
    {
        public double RenderOrder => 0;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (Enabled && !_tried && Assert(stage == EnumRenderStage.Before) && NotNull(_hook)) Start();
        }

        public void Dispose() =>
            _ = Assert(ReferenceEquals(_hook, this) || _hook is null) && Assert(Device is null || _tried);
    }
}
