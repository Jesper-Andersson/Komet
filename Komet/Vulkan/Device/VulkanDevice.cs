using System.Runtime.InteropServices;
using System.Text;

namespace Komet.Vulkan;

// The device whose UUID GL reports (GL_DEVICE_UUID_EXT where offered), else picked by GL's vendor and renderer names
// (Pick). Nothing is shared with OpenGL.
internal sealed unsafe partial class VulkanDevice : IDisposable
{
    public const int UuidBytes = 16;
    private const int MaxDevices = 16, MaxFamilies = 32;
    private const string ClipControl = "VK_EXT_depth_clip_control", LineRasterization = "VK_KHR_line_rasterization";
    private const int MaxExtensions = 1024, MaxWanted = 12;

    private static readonly string[] Extensions = ["VK_KHR_push_descriptor"];

    private IntPtr _instance, _physical, _device, _queue, _commands;
    private ulong _pool, _fence;
    private uint _family;
    private Vk.MemoryProperties _memory;

    public string Name { get; private set; } = "";

    // VK_EXT_depth_clip_control is on: a pipeline can take OpenGL's clip space depth, -1 to 1, as it is
    public bool DepthClipControl { get; private set; }

    // Lines wider than a pixel (block outline is 1.6), rasterized like GL (Bresenham, not parallelograms) where offered
    public bool WideLines { get; private set; }
    public bool BresenhamLines { get; private set; }

    public bool SmoothLines { get; private set; }
    public bool NonSolidFill { get; private set; }
    public bool LogicOps { get; private set; }

    public bool IndirectCount { get; private set; }

    // occlusionQueryPrecise: an occlusion query counts the samples that passed, as OpenGL's GL_SAMPLES_PASSED
    public bool PreciseQueries { get; private set; }

    // shaderFloat64, enabled when offered: doubles in compute shaders (rows.comp)
    public bool Float64 { get; private set; }

    // pipelineStatisticsQuery: what the opaque terrain cost in the pipeline (VulkanFrame.Statistics)
    public bool PipelineStatistics { get; private set; }

    // Nanoseconds per timestamp tick, 0 when the queue takes no timestamps (VulkanFrame.Times)
    public float TimestampPeriod { get; private set; }
    public IntPtr Handle => _device;
    public uint Family => _family;

    public IntPtr InstanceHandle => _instance;
    public IntPtr PhysicalHandle => _physical;

    public bool Surfaces { get; private set; }
    public bool XlibSurfaces { get; private set; }
    public bool WaylandSurfaces { get; private set; }
    public bool Swapchains { get; private set; }

    public static VulkanDevice? Create(ReadOnlySpan<byte> uuid, out string why) => Create(uuid, default, out why);

    // No UUID: picked by GL_VENDOR/GL_RENDERER (Pick)
    public static VulkanDevice? Create(ReadOnlySpan<byte> uuid, (string? Vendor, string? Renderer) gl, out string why)
    {
        why = "";
        if (!Assert(uuid.Length is UuidBytes or 0)) return null;
        if (!VkApi.Load())
        {
            why = "no Vulkan loader";
            return null;
        }

        var device = new VulkanDevice();
        why = device.Instance() ?? device.Pick(uuid, gl) ?? device.Logical() ?? device.Pool() ?? "";
        if (why.Length == 0)
        {
            device.OpenCache();
            return device;
        }

        device.Dispose();
        return null;
    }

    private string? Instance()
    {
        if (!Assert(_instance == IntPtr.Zero)) return "an instance already";
        var surfaces = VkApi.InstanceOffers(Vk.SurfaceExtension);
        XlibSurfaces = surfaces && VkApi.InstanceOffers(Vk.XlibSurfaceExtension);
        WaylandSurfaces = surfaces && VkApi.InstanceOffers(Vk.WaylandSurfaceExtension);
        Surfaces = XlibSurfaces || WaylandSurfaces;
        fixed (byte* name = "Komet\0"u8, surface = Encoding.UTF8.GetBytes(Vk.SurfaceExtension + "\0"),
               xlib = Encoding.UTF8.GetBytes(Vk.XlibSurfaceExtension + "\0"),
               wayland = Encoding.UTF8.GetBytes(Vk.WaylandSurfaceExtension + "\0"))
        {
            var app = new Vk.AppInfo
            {
                SType = Vk.ApplicationInfo, ApplicationName = name, EngineName = name, Api = Vk.ApiVersion13
            };
            var extensions = stackalloc byte*[3];
            var count = 0u;
            if (Surfaces) extensions[count++] = surface;
            if (XlibSurfaces) extensions[count++] = xlib;
            if (WaylandSurfaces) extensions[count++] = wayland;
            var info = new Vk.InstanceInfo
            {
                SType = Vk.InstanceCreateInfo, Application = &app, ExtensionCount = count,
                Extensions = count > 0 ? extensions : null
            };
            IntPtr instance;
            if (VkApi.CreateInstance(&info, null, &instance) != Vk.Success || !Assert(instance != IntPtr.Zero))
                return "vkCreateInstance failed";
            _instance = instance;
            return VkApi.LoadInstance(instance) ? null : "Vulkan instance functions missing";
        }
    }

    private bool Offers(string extension)
    {
        if (!Assert(_physical != IntPtr.Zero) || !Assert(extension.Length < Vk.ExtensionNameBytes)) return false;
        var count = 0u;
        _ = VkApi.EnumerateDeviceExtensions(_physical, null, &count, null);
        var properties = new Vk.ExtensionProperties[Math.Min(count, MaxExtensions)];
        count = (uint)properties.Length;
        fixed (Vk.ExtensionProperties* p = properties) _ = VkApi.EnumerateDeviceExtensions(_physical, null, &count, p);
        return VkApi.Lists(properties, count, extension);
    }

    // Each terrain feature enabled only where offered: the device is made, a pipeline needing a missing one refuses
    private void Wanted(Vk.Features2* features, Vk.Features12* v12, Vk.LineFeaturesInfo* lines)
    {
        if (!Assert(features != null && v12 != null) || !Assert(_physical != IntPtr.Zero)) return;
        var offeredLines = new Vk.LineFeaturesInfo { SType = Vk.LineFeatures };
        void* only = BresenhamLines ? &offeredLines : null;
        var offeredUint8 = new Vk.OneFeature { SType = Vk.IndexTypeUint8Features, Next = only };
        if (IndexUint8) only = &offeredUint8;
        var offeredDivisor = new Vk.TwoFeatures { SType = Vk.DivisorFeatures, Next = only };
        if (Divisors) only = &offeredDivisor;
        var offered12 = new Vk.Features12 { SType = Vk.Vulkan12Features, Next = only };
        var offered = new Vk.Features2 { SType = Vk.PhysicalDeviceFeatures2, Next = &offered12 };
        VkApi.GetPhysicalDeviceFeatures2(_physical, &offered);
        foreach (var feature in (ReadOnlySpan<int>)[Vk.IndependentBlend, Vk.MultiDrawIndirect, Vk.SamplerAnisotropy,
                     Vk.FeatureDepthClamp, Vk.DrawIndirectFirstInstance, Vk.OcclusionQueryPrecise, Vk.WideLines,
                     Vk.FillModeNonSolid, Vk.FeatureLogicOp, Vk.ShaderFloat64, Vk.PipelineStatisticsQuery])
            features->Features[feature] = offered.Features[feature];
        PreciseQueries = offered.Features[Vk.OcclusionQueryPrecise] != 0;
        MultiDraws = offered.Features[Vk.MultiDrawIndirect] != 0;
        (IndexUint8, Divisors) = (IndexUint8 && offeredUint8.Enabled != 0, Divisors && offeredDivisor.First != 0);
        Float64 = offered.Features[Vk.ShaderFloat64] != 0;
        PipelineStatistics = offered.Features[Vk.PipelineStatisticsQuery] != 0;
        WideLines = offered.Features[Vk.WideLines] != 0;
        (NonSolidFill, LogicOps) = (offered.Features[Vk.FillModeNonSolid] != 0, offered.Features[Vk.FeatureLogicOp] != 0);
        if (BresenhamLines && lines != null)
        {
            (lines->Bresenham, lines->Smooth) = (offeredLines.Bresenham, offeredLines.Smooth);
            (BresenhamLines, SmoothLines) = (offeredLines.Bresenham != 0, offeredLines.Smooth != 0);
        }
        v12->Features[Vk.DrawIndirectCount] = offered12.Features[Vk.DrawIndirectCount];
        IndirectCount = offered12.Features[Vk.DrawIndirectCount] != 0;
        _ = Assert(v12->SType == Vk.Vulkan12Features);
    }

    private string? Logical()
    {
        if (!Assert(_physical != IntPtr.Zero) || !Assert(_device == IntPtr.Zero)) return "no device";
        DepthClipControl = Offers(ClipControl);
        BresenhamLines = Offers(LineRasterization);
        (IndexUint8, Divisors) = (Offers(Vk.IndexUint8Extension), Offers(Vk.DivisorExtension)); // what mods' raw OpenGL needs
        Swapchains = Surfaces && Offers(Vk.SwapchainExtension);
        var wanted = Extras(Extensions);
        var names = Array.ConvertAll(wanted, Marshal.StringToCoTaskMemUTF8);
        try
        {
            var pointers = stackalloc byte*[MaxWanted];
            for (var i = 0; i < Math.Min(names.Length, MaxWanted); i++) pointers[i] = (byte*)names[i];
            var lines = new Vk.LineFeaturesInfo { SType = Vk.LineFeatures };
            var clip = new Vk.OneFeature
            {
                SType = Vk.DepthClipControlFeatures, Enabled = 1, Next = BresenhamLines ? &lines : null
            };
            void* chain = BresenhamLines ? &lines : null;
            if (DepthClipControl) chain = &clip;
            var uint8 = new Vk.OneFeature { SType = Vk.IndexTypeUint8Features, Enabled = 1, Next = chain };
            if (IndexUint8) chain = &uint8;
            var divisor = new Vk.TwoFeatures { SType = Vk.DivisorFeatures, First = 1, Next = chain };
            if (Divisors) chain = &divisor;
            var v13 = new Vk.Features13 { SType = Vk.Vulkan13Features, Next = chain };
            v13.Features[Vk.DynamicRendering] = 1;
            var v12 = new Vk.Features12 { SType = Vk.Vulkan12Features, Next = &v13 };
            var features = new Vk.Features2 { SType = Vk.PhysicalDeviceFeatures2, Next = &v12 };
            Wanted(&features, &v12, &lines);
            (uint8.Enabled, divisor.First) = (IndexUint8 ? 1u : 0, Divisors ? 1u : 0); // as offered
            var priority = 1f;
            var queue = new Vk.QueueInfo
            {
                SType = Vk.DeviceQueueCreateInfo, Family = _family, Count = 1, Priorities = &priority
            };
            var info = new Vk.DeviceInfo
            {
                SType = Vk.DeviceCreateInfo, Next = &features, QueueInfoCount = 1, QueueInfos = &queue,
                ExtensionCount = (uint)Math.Min(wanted.Length, MaxWanted), Extensions = pointers
            };
            IntPtr device;
            if (VkApi.CreateDevice(_physical, &info, null, &device) != Vk.Success) return "vkCreateDevice failed";
            _device = device;
            Formats();
            var loaded = VkApi.LoadDevice(device);
            return loaded ? null : "Vulkan device functions missing";
        }
        finally
        {
            foreach (var name in names.Bounded(MaxWanted)) Marshal.FreeCoTaskMem(name);
        }
    }

    private string? Pool()
    {
        if (!Assert(_device != IntPtr.Zero)) return "no device";
        IntPtr queue, commands;
        VkApi.GetDeviceQueue(_device, _family, 0, &queue);
        var pool = new Vk.PoolInfo
        {
            SType = Vk.CommandPoolCreateInfo, Flags = Vk.ResetCommandBuffer, Family = _family
        };
        ulong handle, fence;
        if (VkApi.CreateCommandPool(_device, &pool, null, &handle) != Vk.Success) return "vkCreateCommandPool failed";
        _pool = handle;
        var allocate = new Vk.CommandBufferInfo { SType = Vk.CommandBufferAllocateInfo, Pool = handle, Count = 1 };
        if (VkApi.AllocateCommandBuffers(_device, &allocate, &commands) != Vk.Success) return "no command buffer";
        var fenceInfo = new Vk.FlagsInfo { SType = Vk.FenceCreateInfo };
        if (VkApi.CreateFence(_device, &fenceInfo, null, &fence) != Vk.Success) return "vkCreateFence failed";
        (_queue, _commands, _fence) = (queue, commands, fence);
        return Assert(queue != IntPtr.Zero) ? null : "no queue";
    }

    public int MemoryType(uint typeBits, uint wanted, uint unwanted = 0)
    {
        if (!Assert(_memory.TypeCount <= 32) || !Assert(wanted != 0)) return -1;
        for (var i = 0; i < Math.Min((int)_memory.TypeCount, 32); i++)
            if ((typeBits & (1u << i)) != 0 && (_memory.Types[2 * i] & wanted) == wanted &&
                (_memory.Types[2 * i] & unwanted) == 0)
                return i;
        return -1;
    }

    // Run and Submit wait for the GPU to finish, unlike the frames' Submit
    public bool Run(Action<IntPtr> record, ReadOnlySpan<ulong> signal) =>
        Assert(signal.Length <= 4) && Run(record, [], signal);

    public bool Run(Action<IntPtr> record, ReadOnlySpan<ulong> wait, ReadOnlySpan<ulong> signal)
    {
        if (!NotNull(record) || !Assert(signal.Length <= 4)) return false;
        var commands = Begin();
        if (commands == IntPtr.Zero) return false;
        record(commands);
        return Submit(wait, signal);
    }

    public IntPtr Begin() =>
        Assert(_commands != IntPtr.Zero) && Assert(_device != IntPtr.Zero) && Begin(_commands) ? _commands : IntPtr.Zero;

    public bool Submit(ReadOnlySpan<ulong> signal) => Assert(signal.Length <= 4) && Submit([], signal);

    // Every stage waits for the wait semaphores
    public bool Submit(ReadOnlySpan<ulong> wait, ReadOnlySpan<ulong> signal)
    {
        var fence = _fence;
        if (!Submit([_commands], wait, signal, fence)) return false;
        var done = VkApi.WaitForFences(_device, 1, &fence, 1, ulong.MaxValue) == Vk.Success;
        return VkApi.ResetFences(_device, 1, &fence) == Vk.Success && Assert(done) && Assert(fence != 0);
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            _ = VkApi.DeviceWaitIdle(_device);
            CloseCache();
            if (_fence != 0) VkApi.DestroyFence(_device, _fence, null);
            if (_pool != 0) VkApi.DestroyCommandPool(_device, _pool, null);
            VkApi.DestroyDevice(_device, null);
        }

        if (_instance != IntPtr.Zero) VkApi.DestroyInstance(_instance, null);
        (_device, _instance, _queue, _commands) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        (_pool, _fence) = (0, 0);
        _ = Assert(_device == IntPtr.Zero) && Assert(_instance == IntPtr.Zero);
    }
}
