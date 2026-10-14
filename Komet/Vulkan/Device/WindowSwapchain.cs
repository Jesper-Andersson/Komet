namespace Komet.Vulkan;

// Wayland surface only when GLFW runs on Wayland (OPENTK_4_USE_WAYLAND=1), else X11 (XWayland). Acquire uses a fence
// the engine thread waits on as in SwapBuffers, so the GPU never waits mid-segment. If Vulkan cannot present, GL does.
internal sealed unsafe class WindowSwapchain : IDisposable
{
    private const int MaxImages = 8, MaxFormats = 64, MaxModes = 16;
    private const ulong AcquireNanoseconds = 1_000_000_000;
    private const uint UsageTransferDst = 0x2;

    private readonly VulkanDevice _device;
    private readonly ulong _surface, _fence;
    private ulong _swapchain;
    private ulong[] _images = [], _rendered = [];
    private (int Width, int Height, uint Mode) _made;
    private readonly uint?[] _modes = new uint?[3]; // per VSync mode, asked of the surface once: a query is a compositor round trip
    private volatile bool _stale; // set by a present on the recording thread too
    private volatile bool _unpresented; // an image acquired that no present has shown yet
    private int _held = -1; // that image: an acquired image must be presented, so the next acquire gives it again

    // Acquires, presents and the swapchain's making against FrameBridge's thread; never held while waiting for another thread
    private readonly Lock _raw = new();

    // The engine's presents so far: a bridged frame is shown only while none came after the frame it bridges from
    public long Presents { get; private set; }

    private WindowSwapchain(VulkanDevice device, ulong surface, ulong fence)
    {
        (_device, _surface, _fence) = (device, surface, fence);
        _ = Assert(surface != 0) && Assert(fence != 0);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint Format { get; private set; }

    // The window: on Wayland its wl_display and wl_surface, on X11 its Display and Window (an XID)
    public readonly record struct Native(bool Wayland, IntPtr Display, nuint Window);

    public static WindowSwapchain? Create(VulkanDevice device, Native window, out string why)
    {
        if (!NotNull(device) || !device.Swapchains || !VkApi.Presents)
            return Refused(out why, "the Vulkan loader or device has no swapchains for a window");
        if (window.Display == IntPtr.Zero || window.Window == 0) return Refused(out why, "the window has no native handle");
        if (Surface(device, window, out why) is not { } surface) return null;
        uint supported;
        var asked = VkApi.GetSurfaceSupport(device.PhysicalHandle, device.Family, surface, &supported);
        var presents = asked == Vk.Success && supported != 0;
        var fence = presents ? device.Fence(false) : 0;
        if (fence != 0) return new WindowSwapchain(device, surface, fence);
        VkApi.DestroySurface(device.InstanceHandle, surface, null);
        return Refused(out why, presents ? "no fence" : "the queue cannot present to the window");
    }

    private static ulong? Surface(VulkanDevice device, Native window, out string why)
    {
        why = "";
        ulong surface;
        _ = Assert(window.Display != IntPtr.Zero) && Assert(device.InstanceHandle != IntPtr.Zero);
        if (window.Wayland)
        {
            if (!device.WaylandSurfaces || VkApi.CreateWaylandSurface == null)
                return Unsurfaced(out why, "the Vulkan instance has no Wayland surfaces");
            var wayland = new Vk.WaylandSurfaceInfo
            {
                SType = Vk.WaylandSurfaceCreateInfo, Display = window.Display, Surface = (IntPtr)window.Window
            };
            return VkApi.CreateWaylandSurface(device.InstanceHandle, &wayland, null, &surface) == Vk.Success
                ? surface
                : Unsurfaced(out why, "vkCreateWaylandSurfaceKHR failed");
        }

        if (!device.XlibSurfaces || VkApi.CreateXlibSurface == null)
            return Unsurfaced(out why, "the Vulkan instance has no X11 surfaces");
        var xlib = new Vk.XlibSurfaceInfo { SType = Vk.XlibSurfaceCreateInfo, Display = window.Display, Window = window.Window };
        return VkApi.CreateXlibSurface(device.InstanceHandle, &xlib, null, &surface) == Vk.Success
            ? surface
            : Unsurfaced(out why, "vkCreateXlibSurfaceKHR failed");
    }

    private static ulong? Unsurfaced(out string why, string reason)
    {
        why = reason;
        _ = Assert(why.Length > 0) && Assert(reason.Length > 0);
        return null;
    }

    private static WindowSwapchain? Refused(out string why, string reason)
    {
        why = reason;
        _ = Assert(why.Length > 0);
        return null;
    }

    // OpenTK's VSync mode: 0 off, 1 on, 2 adaptive
    private uint Mode(int vsync)
    {
        var at = Math.Clamp(vsync, 0, _modes.Length - 1);
        if (!Index(at, _modes.Length)) return Vk.PresentFifo;
        if (_modes[at] is { } known) return known;
        var count = (uint)MaxModes;
        var modes = stackalloc uint[MaxModes];
        _ = VkApi.GetSurfacePresentModes(_device.PhysicalHandle, _surface, &count, modes);
        var offered = new ReadOnlySpan<uint>(modes, (int)Math.Min(count, MaxModes));
        _ = Assert(offered.Length <= MaxModes);
        ReadOnlySpan<uint> wanted = vsync switch
        {
            0 => [Vk.PresentImmediate, Vk.PresentMailbox],
            2 => [Vk.PresentFifoRelaxed],
            _ => []
        };
        var mode = Vk.PresentFifo; // every surface offers it
        foreach (var candidate in wanted.Bounded(2))
            if (offered.Contains(candidate))
            {
                mode = candidate;
                break;
            }

        _modes[at] = mode;
        return Assert(_modes[at] == mode) ? mode : Vk.PresentFifo;
    }

    // An acquired but unpresented image (frame went to GL, submit failed) is reused; a new swapchain lets go of it
    public int Acquire(int width, int height, int vsync, out string why)
    {
        why = "";
        if (!Assert(width > 0 && height > 0)) return -1;
        if (_unpresented) _device.CatchUp?.Invoke(); // one thread at a time: the recording thread's last present first
        var mode = Mode(vsync);
        var anew = _swapchain == 0 || _stale || _made != (width, height, mode);
        if (_unpresented && !anew && Index(_held, _images.Length)) return _held;
        _unpresented = false;
        if (anew && !Made((width, height, mode), out why)) return -1;
        var index = Next(out var result);
        if (result == Vk.OutOfDate && Made((width, height, mode), out why)) index = Next(out result);
        if (result == Vk.Suboptimal) _stale = true; // shown still, made anew for the next frame
        (_held, _unpresented) = (index, index >= 0);
        if (index >= 0) return index;
        why = $"vkAcquireNextImageKHR returned {result}";
        return -1;
    }

    private int Next(out int result)
    {
        _ = Assert(_swapchain != 0) && Assert(_fence != 0);
        uint index;
        var fence = _fence;
        lock (_raw) result = VkApi.AcquireNextImage(_device.Handle, _swapchain, AcquireNanoseconds, 0, fence, &index);
        if (result is not (Vk.Success or Vk.Suboptimal)) return -1;
        var waited = VkApi.WaitForFences(_device.Handle, 1, &fence, 1, AcquireNanoseconds) == Vk.Success;
        _ = VkApi.ResetFences(_device.Handle, 1, &fence);
        return Assert(waited) && index < _images.Length ? (int)index : -1;
    }

    public (ulong Image, ulong Rendered) Image(int index) =>
        Index(index, _images.Length) && Assert(_rendered.Length == _images.Length)
            ? (_images[index], _rendered[index])
            : default;

    // Shown once its semaphore is signalled; an out-of-date swapchain is made anew next frame
    public bool Present(int index)
    {
        if (!Index(index, _images.Length) || !Assert(_swapchain != 0)) return false;
        var (swapchain, rendered, at) = (_swapchain, _rendered[index], (uint)index);
        var info = new Vk.PresentInfo
        {
            SType = Vk.PresentInfoType, WaitCount = 1, Waits = &rendered, SwapchainCount = 1, Swapchains = &swapchain,
            Indices = &at
        };
        int result;
        lock (_raw)
        {
            result = _device.Present(&info);
            Presents++;
        }

        if (result is Vk.OutOfDate or Vk.Suboptimal) _stale = true;
        _unpresented = false; // last: what it set is seen by the next acquire
        return result is Vk.Success or Vk.Suboptimal;
    }

    // The swapchain for the size and mode; the old one handed over and destroyed once the GPU is done with it
    private bool Made((int Width, int Height, uint Mode) wanted, out string why)
    {
        if (!Assert(wanted.Width > 0 && wanted.Height > 0)) return Failed(out why, "no size");
        Vk.SurfaceCapabilities caps;
        _ = _device.WaitIdle(); // rare: the old images may be in use still
        if (VkApi.GetSurfaceCapabilities(_device.PhysicalHandle, _surface, &caps) != Vk.Success)
            return Failed(out why, "no surface capabilities");
        var (width, height) = caps.CurrentWidth == uint.MaxValue
            ? ((uint)wanted.Width, (uint)wanted.Height)
            : (caps.CurrentWidth, caps.CurrentHeight);
        if (width == 0 || height == 0) return Failed(out why, "the window has no area (minimized)");
        if ((caps.SupportedUsage & UsageTransferDst) == 0) return Failed(out why, "its images take no transfers");
        var format = Chosen();
        if (format is null) return Failed(out why, "no 8-bit RGBA or BGRA format for the window");
        var count = Math.Max(caps.MinImageCount + 1, 3u);
        if (caps.MaxImageCount > 0) count = Math.Min(count, caps.MaxImageCount);
        var info = new Vk.SwapchainInfo
        {
            SType = Vk.SwapchainCreateInfo, Surface = _surface, MinImageCount = count, Format = format.Value.Format,
            ColorSpace = format.Value.ColorSpace, Width = width, Height = height, ArrayLayers = 1,
            Usage = UsageTransferDst, Transform = caps.CurrentTransform, PresentMode = wanted.Mode, Clipped = 1,
            CompositeAlpha = (caps.SupportedCompositeAlpha & Vk.CompositeOpaque) != 0
                ? Vk.CompositeOpaque
                : caps.SupportedCompositeAlpha & (uint)-(int)caps.SupportedCompositeAlpha, // the lowest bit offered
            OldSwapchain = _swapchain
        };
        ulong made;
        lock (_raw)
        {
            var result = VkApi.CreateSwapchain(_device.Handle, &info, null, &made);
            if (_swapchain != 0) VkApi.DestroySwapchain(_device.Handle, _swapchain, null);
            _swapchain = 0;
            if (result != Vk.Success) return Failed(out why, $"vkCreateSwapchainKHR returned {result}");
            (_swapchain, _made, _stale, Width, Height, Format) =
                (made, wanted, false, (int)width, (int)height, format.Value.Format);
            return Images(out why);
        }
    }

    private bool Failed(out string why, string reason)
    {
        why = reason;
        _stale = true;
        _ = Assert(why.Length > 0) && Assert(_surface != 0);
        return false;
    }

    private Vk.SurfaceFormat? Chosen()
    {
        _ = Assert(_surface != 0) && Assert(_device.PhysicalHandle != IntPtr.Zero);
        var count = (uint)MaxFormats;
        var formats = stackalloc Vk.SurfaceFormat[MaxFormats];
        _ = VkApi.GetSurfaceFormats(_device.PhysicalHandle, _surface, &count, formats);
        Vk.SurfaceFormat? found = null;
        for (var i = 0; i < Math.Min((int)count, MaxFormats); i++)
        {
            var f = formats[i];
            if (f.ColorSpace != Vk.ColorSpaceSrgbNonlinear || f.Format is not (Vk.FormatBgra8 or Vk.FormatRgba8)) continue;
            if (f.Format == Vk.FormatBgra8) return f;
            found = f;
        }

        _ = Assert(count <= MaxFormats);
        return found;
    }

    private bool Images(out string why)
    {
        why = "";
        uint count;
        if (VkApi.GetSwapchainImages(_device.Handle, _swapchain, &count, null) < 0 || count == 0)
            return Failed(out why, "no swapchain images");
        count = Math.Min(count, MaxImages);
        var images = stackalloc ulong[MaxImages];
        if (VkApi.GetSwapchainImages(_device.Handle, _swapchain, &count, images) < 0)
            return Failed(out why, "no swapchain images");
        _images = new ReadOnlySpan<ulong>(images, (int)Math.Min(count, MaxImages)).ToArray();
        foreach (var semaphore in _rendered.Bounded(MaxImages)) _device.DestroySemaphore(semaphore);
        _rendered = new ulong[_images.Length];
        for (var i = 0; i < Math.Min(_rendered.Length, MaxImages); i++)
            if ((_rendered[i] = _device.Semaphore()) == 0)
                return Failed(out why, "no semaphore for a swapchain image");
        return Assert(_rendered.Length == _images.Length);
    }

    // FrameBridge's thread: an image acquired at once (none free: false), filled by submit (which signals the image's
    // semaphore) and presented, all while no engine frame is between its acquire and present and none came after the
    // frame bridged from (presents, the engine's count when it was published)
    public bool Bridge(ulong fence, long presents, System.Func<(ulong Image, int Width, int Height, ulong Rendered), bool> submit)
    {
        if (!NotNull(submit) || !Assert(fence != 0)) return false;
        lock (_raw)
        {
            if (_swapchain == 0 || _stale || _unpresented || Presents != presents) return false;
            uint index;
            var result = VkApi.AcquireNextImage(_device.Handle, _swapchain, 0, 0, fence, &index);
            if (result is not (Vk.Success or Vk.Suboptimal)) return false; // none free before the next refresh
            if (!_device.Wait(fence, AcquireNanoseconds) || index >= _images.Length || !submit((_images[index], Width, Height,
                    _rendered[index])))
            {
                _stale = true; // an acquired image nothing will present: the engine makes the swapchain anew
                return false;
            }

            var (swapchain, rendered, at) = (_swapchain, _rendered[index], index);
            var info = new Vk.PresentInfo
            {
                SType = Vk.PresentInfoType, WaitCount = 1, Waits = &rendered, SwapchainCount = 1, Swapchains = &swapchain,
                Indices = &at
            };
            result = _device.PresentAlone(&info);
            if (result is Vk.OutOfDate or Vk.Suboptimal) _stale = true;
            return Assert(Presents == presents) && result is Vk.Success or Vk.Suboptimal;
        }
    }

    public void Dispose()
    {
        _ = _device.WaitIdle();
        foreach (var semaphore in _rendered.Bounded(MaxImages)) _device.DestroySemaphore(semaphore);
        if (_swapchain != 0) VkApi.DestroySwapchain(_device.Handle, _swapchain, null);
        VkApi.DestroySurface(_device.InstanceHandle, _surface, null);
        _device.Destroy(_fence);
        (_swapchain, _images, _rendered) = (0, [], []);
        _ = Assert(_surface != 0) && Assert(_fence != 0);
    }
}
