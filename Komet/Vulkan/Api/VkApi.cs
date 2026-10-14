using System.Runtime.InteropServices;
using System.Text;

namespace Komet.Vulkan;

// Device functions via vkGetDeviceProcAddr (no loader dispatch). Nothing linked: without Vulkan Load returns false.
internal static unsafe partial class VkApi
{
    private const int MaxName = 128;
    private static readonly string[] Libraries =
        ["libvulkan.so.1", "libvulkan.so", "vulkan-1.dll", "libvulkan.1.dylib"];

    private static IntPtr _library;
    private static delegate* unmanaged<IntPtr, byte*, IntPtr> _instanceProc;
    private static delegate* unmanaged<IntPtr, byte*, IntPtr> _deviceProc;

    public static delegate* unmanaged<Vk.InstanceInfo*, void*, IntPtr*, int> CreateInstance { get; private set; }
    public static delegate* unmanaged<IntPtr, void*, void> DestroyInstance { get; private set; }
    public static delegate* unmanaged<IntPtr, uint*, IntPtr*, int> EnumeratePhysicalDevices { get; private set; }
    public static delegate* unmanaged<IntPtr, byte*, void> GetPhysicalDeviceProperties2 { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.Features2*, void> GetPhysicalDeviceFeatures2 { get; private set; }
    public static delegate* unmanaged<IntPtr, uint*, Vk.QueueFamily*, void> GetQueueFamilies { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.MemoryProperties*, void> GetMemoryProperties { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.DeviceInfo*, void*, IntPtr*, int> CreateDevice { get; private set; }

    public static delegate* unmanaged<IntPtr, void*, void> DestroyDevice { get; private set; }
    public static delegate* unmanaged<IntPtr, int> DeviceWaitIdle { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void> GetDeviceQueue { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.PoolInfo*, void*, ulong*, int> CreateCommandPool { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyCommandPool { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.CommandBufferInfo*, IntPtr*, int> AllocateCommandBuffers { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.BeginInfo*, int> BeginCommandBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, int> EndCommandBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, uint, uint, void*, uint, void*, uint, Vk.ImageBarrier*, void>
        CmdPipelineBarrier { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, int, float*, uint, Vk.ColorRange*, void> CmdClearColorImage { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, Vk.Submit*, ulong, int> QueueSubmit { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.FlagsInfo*, void*, ulong*, int> CreateFence { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyFence { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, ulong*, uint, ulong, int> WaitForFences { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, ulong*, int> ResetFences { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.ImageInfo*, void*, ulong*, int> CreateImage { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyImage { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, Vk.MemoryRequirements*, void> GetImageMemoryRequirements { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, int> BindImageMemory { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.FlagsInfo*, void*, ulong*, int> CreateSemaphore { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroySemaphore { get; private set; }
    private static delegate* unmanaged<IntPtr, Vk.AllocateInfo*, void*, ulong*, int> _allocateMemory;
    private static delegate* unmanaged<IntPtr, ulong, void*, void> _freeMemory;

    public static int AllocateMemory(IntPtr device, Vk.AllocateInfo* info, void* allocator, ulong* memory,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        if (!Assert(_allocateMemory != null) || !Assert(info != null && memory != null)) return -1;
        VulkanWatch.Mark($"vkAllocateMemory {info->Size >> 20} MB for {Path.GetFileNameWithoutExtension(file)}");
        var result = _allocateMemory(device, info, allocator, memory);
        if (result == Vk.Success) VkMemory.Allocated(*memory, info->Size, file);
        return result;
    }

    public static void FreeMemory(IntPtr device, ulong memory, void* allocator)
    {
        if (!Assert(_freeMemory != null) || memory == 0) return;
        VkMemory.Freed(memory);
        _freeMemory(device, memory, allocator);
    }

    public static bool Load()
    {
        if (_library != IntPtr.Zero) return true;
        foreach (var name in Libraries.Bounded(Libraries.Length))
            if (NativeLibrary.TryLoad(name, out _library)) break;
        if (_library == IntPtr.Zero || !NativeLibrary.TryGetExport(_library, "vkGetInstanceProcAddr", out var proc))
            return false;
        _instanceProc = (delegate* unmanaged<IntPtr, byte*, IntPtr>)proc;
        CreateInstance =
            (delegate* unmanaged<Vk.InstanceInfo*, void*, IntPtr*, int>)Instance(IntPtr.Zero, "vkCreateInstance");
        return Assert(_instanceProc != null) && CreateInstance != null;
    }

    public static bool LoadInstance(IntPtr instance)
    {
        if (!Assert(instance != IntPtr.Zero) || !Assert(_instanceProc != null)) return false;
        DestroyInstance = (delegate* unmanaged<IntPtr, void*, void>)Instance(instance, "vkDestroyInstance");
        EnumeratePhysicalDevices =
            (delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)Instance(instance, "vkEnumeratePhysicalDevices");
        GetPhysicalDeviceProperties2 =
            (delegate* unmanaged<IntPtr, byte*, void>)Instance(instance, "vkGetPhysicalDeviceProperties2");
        GetPhysicalDeviceFeatures2 =
            (delegate* unmanaged<IntPtr, Vk.Features2*, void>)Instance(instance, "vkGetPhysicalDeviceFeatures2");
        GetQueueFamilies = (delegate* unmanaged<IntPtr, uint*, Vk.QueueFamily*, void>)Instance(instance,
            "vkGetPhysicalDeviceQueueFamilyProperties");
        GetMemoryProperties =
            (delegate* unmanaged<IntPtr, Vk.MemoryProperties*, void>)Instance(instance,
                "vkGetPhysicalDeviceMemoryProperties");
        CreateDevice =
            (delegate* unmanaged<IntPtr, Vk.DeviceInfo*, void*, IntPtr*, int>)Instance(instance, "vkCreateDevice");
        _deviceProc = (delegate* unmanaged<IntPtr, byte*, IntPtr>)Instance(instance, "vkGetDeviceProcAddr");
        EnumerateDeviceExtensions = (delegate* unmanaged<IntPtr, byte*, uint*, Vk.ExtensionProperties*, int>)Instance(
            instance, "vkEnumerateDeviceExtensionProperties");
        LoadSurface(instance);
        LoadFormats(instance);
        return DestroyInstance != null && EnumeratePhysicalDevices != null && GetPhysicalDeviceProperties2 != null &&
               GetPhysicalDeviceFeatures2 != null &&
               GetQueueFamilies != null && GetMemoryProperties != null && CreateDevice != null && _deviceProc != null &&
               EnumerateDeviceExtensions != null;
    }

    public static bool LoadDevice(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero) || !Assert(_deviceProc != null)) return false;
        DestroyDevice = (delegate* unmanaged<IntPtr, void*, void>)Device(device, "vkDestroyDevice");
        DeviceWaitIdle = (delegate* unmanaged<IntPtr, int>)Device(device, "vkDeviceWaitIdle");
        GetDeviceQueue = (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Device(device, "vkGetDeviceQueue");
        CreateCommandPool =
            (delegate* unmanaged<IntPtr, Vk.PoolInfo*, void*, ulong*, int>)Device(device, "vkCreateCommandPool");
        DestroyCommandPool = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyCommandPool");
        AllocateCommandBuffers = (delegate* unmanaged<IntPtr, Vk.CommandBufferInfo*, IntPtr*, int>)Device(device,
            "vkAllocateCommandBuffers");
        BeginCommandBuffer = (delegate* unmanaged<IntPtr, Vk.BeginInfo*, int>)Device(device, "vkBeginCommandBuffer");
        EndCommandBuffer = (delegate* unmanaged<IntPtr, int>)Device(device, "vkEndCommandBuffer");
        CmdPipelineBarrier =
            (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void*, uint, void*, uint, Vk.ImageBarrier*, void>)
            Device(device, "vkCmdPipelineBarrier");
        CmdClearColorImage = (delegate* unmanaged<IntPtr, ulong, int, float*, uint, Vk.ColorRange*, void>)Device(device,
            "vkCmdClearColorImage");
        QueueSubmit = (delegate* unmanaged<IntPtr, uint, Vk.Submit*, ulong, int>)Device(device, "vkQueueSubmit");
        LoadSwapchain(device);
        return LoadSync(device) && LoadMemory(device) && LoadCompute(device) && LoadGraphics(device) && LoadDraws(device) &&
               LoadQueries(device) && LoadOnly(device) &&
               DestroyDevice != null &&
               DeviceWaitIdle != null &&
               GetDeviceQueue != null && CreateCommandPool != null && DestroyCommandPool != null &&
               AllocateCommandBuffers != null && BeginCommandBuffer != null && EndCommandBuffer != null &&
               CmdPipelineBarrier != null && CmdClearColorImage != null && QueueSubmit != null;
    }

    private static bool LoadSync(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return false;
        CreateFence = (delegate* unmanaged<IntPtr, Vk.FlagsInfo*, void*, ulong*, int>)Device(device, "vkCreateFence");
        DestroyFence = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyFence");
        WaitForFences = (delegate* unmanaged<IntPtr, uint, ulong*, uint, ulong, int>)Device(device, "vkWaitForFences");
        ResetFences = (delegate* unmanaged<IntPtr, uint, ulong*, int>)Device(device, "vkResetFences");
        CreateSemaphore =
            (delegate* unmanaged<IntPtr, Vk.FlagsInfo*, void*, ulong*, int>)Device(device, "vkCreateSemaphore");
        DestroySemaphore = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroySemaphore");
        return Assert(CreateFence != null) && DestroyFence != null && WaitForFences != null && ResetFences != null &&
               CreateSemaphore != null && DestroySemaphore != null;
    }

    private static bool LoadMemory(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return false;
        CreateImage = (delegate* unmanaged<IntPtr, Vk.ImageInfo*, void*, ulong*, int>)Device(device, "vkCreateImage");
        DestroyImage = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyImage");
        GetImageMemoryRequirements = (delegate* unmanaged<IntPtr, ulong, Vk.MemoryRequirements*, void>)Device(device,
            "vkGetImageMemoryRequirements");
        _allocateMemory =
            (delegate* unmanaged<IntPtr, Vk.AllocateInfo*, void*, ulong*, int>)Device(device, "vkAllocateMemory");
        _freeMemory = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkFreeMemory");
        BindImageMemory = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, int>)Device(device, "vkBindImageMemory");
        return Assert(CreateImage != null) && DestroyImage != null && GetImageMemoryRequirements != null &&
               _allocateMemory != null && _freeMemory != null && BindImageMemory != null;
    }

    private static IntPtr Instance(IntPtr instance, string name) =>
        Assert(name.StartsWith("vk", StringComparison.Ordinal)) &&
        Assert(instance != IntPtr.Zero || name is "vkCreateInstance" or "vkEnumerateInstanceExtensionProperties")
            ? Proc(_instanceProc, instance, name)
            : IntPtr.Zero;

    private static IntPtr Device(IntPtr device, string name) =>
        Assert(name.StartsWith("vk", StringComparison.Ordinal)) && Assert(device != IntPtr.Zero)
            ? Proc(_deviceProc, device, name)
            : IntPtr.Zero;

    private static IntPtr Proc(delegate* unmanaged<IntPtr, byte*, IntPtr> proc, IntPtr owner, string name)
    {
        if (proc == null || !Assert(name.Length < MaxName)) return IntPtr.Zero;
        Span<byte> text = stackalloc byte[MaxName];
        var length = Encoding.ASCII.GetBytes(name, text);
        if (!Assert(length < MaxName)) return IntPtr.Zero;
        text[length] = 0;
        fixed (byte* p = text) return proc(owner, p);
    }
}
