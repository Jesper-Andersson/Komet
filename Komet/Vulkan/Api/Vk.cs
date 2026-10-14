namespace Komet.Vulkan;

// Hand-declared per the C headers on 64-bit: dispatchable handles are pointers, the others 64-bit numbers.
internal static partial class Vk
{
    public const uint ApiVersion13 = (1u << 22) | (3u << 12), QueueFamilyIgnored = ~0u;
    public const int Success = 0;

    // VkStructureType
    public const int ApplicationInfo = 0, InstanceCreateInfo = 1, DeviceQueueCreateInfo = 2, DeviceCreateInfo = 3;
    public const int SubmitInfo = 4, MemoryAllocateInfo = 5, FenceCreateInfo = 8, SemaphoreCreateInfo = 9;
    public const int ImageCreateInfo = 14, CommandPoolCreateInfo = 39, CommandBufferAllocateInfo = 40;
    public const int CommandBufferBeginInfo = 42, ImageMemoryBarrier = 45;
    public const int PhysicalDeviceProperties2 = 1000059001, PhysicalDeviceIdProperties = 1000071004;

    public const uint FormatRgba8 = 37, TransferSrc = 0x1, TransferDst = 0x2, Sampled = 0x4;
    public const uint Storage = 0x8, ColorAttachment = 0x10, DepthAttachment = 0x20, AspectDepth = 0x2;
    public const uint FormatRgba16F = 97, FormatD32F = 126, FormatR16F = 76;
    public const uint FormatRgba32F = 109, FormatR8 = 9, FormatRg16F = 83, FormatRgba16 = 91;
    public const uint FormatRg8 = 16, FormatSrgba8 = 43, FormatR16 = 70, FormatA2B10G10R10 = 64, FormatB10G11R11F = 122;
    public const int LayoutUndefined = 0, LayoutGeneral = 1, LayoutTransferDst = 7;
    public const uint StageTop = 0x1, StageTransfer = 0x1000, StageBottom = 0x2000, StageAll = 0x10000;
    public const uint AccessTransferWrite = 0x1000, AccessMemoryRead = 0x8000, AccessMemoryWrite = 0x10000;
    public const uint QueueGraphics = 0x1, QueueCompute = 0x2, DeviceLocal = 0x1, AspectColor = 0x1;
    public const uint OneTimeSubmit = 0x1, ResetCommandBuffer = 0x2;

    // VkPhysicalDeviceProperties2: vendorID at 16+8, deviceID 16+12, deviceName 16+20, pipelineCacheUUID 16+276
    public const int PropertiesBytes = 1024, DeviceNameOffset = 36;
    public const int VendorOffset = 24, ModelOffset = 28, TypeOffset = 32, CacheUuidOffset = 292;

    // VkPhysicalDeviceLimits at 312: timestampComputeAndGraphics at 420, timestampPeriod (float ns/tick) at 424
    public const int TimestampsOffset = 312 + 420, TimestampPeriodOffset = 312 + 424;

    public unsafe struct AppInfo
    {
        public int SType; public void* Next;
        public byte* ApplicationName;
        public uint ApplicationVersion;
        public byte* EngineName;
        public uint EngineVersion, Api;
    }

    public unsafe struct InstanceInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public AppInfo* Application;
        public uint LayerCount;
        public byte** Layers;
        public uint ExtensionCount;
        public byte** Extensions;
    }

    public unsafe struct IdProperties
    {
        public int SType; public void* Next;
        public fixed byte DeviceUuid[16];
        public fixed byte DriverUuid[16];
        public fixed byte DeviceLuid[8];
        public uint DeviceNodeMask, DeviceLuidValid;
    }

    public struct QueueFamily { public uint Flags, Count, TimestampBits, GranularityWidth, GranularityHeight, GranularityDepth; }

    public unsafe struct MemoryProperties
    {
        public uint TypeCount;
        public fixed uint Types[64]; // 32 of { propertyFlags, heapIndex }
        public uint HeapCount;
        public fixed ulong Heaps[32]; // 16 of { size, flags }
    }

    public unsafe struct QueueInfo
    {
        public int SType; public void* Next;
        public uint Flags, Family, Count;
        public float* Priorities;
    }

    public unsafe struct DeviceInfo
    {
        public int SType; public void* Next;
        public uint Flags, QueueInfoCount;
        public QueueInfo* QueueInfos;
        public uint LayerCount;
        public byte** Layers;
        public uint ExtensionCount;
        public byte** Extensions;
        public void* Features;
    }

    public unsafe struct PoolInfo
    {
        public int SType; public void* Next;
        public uint Flags, Family;
    }

    public unsafe struct CommandBufferInfo
    {
        public int SType; public void* Next;
        public ulong Pool;
        public int Level;
        public uint Count;
    }

    public unsafe struct BeginInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public void* Inheritance;
    }

    public unsafe struct ImageInfo
    {
        public int SType; public void* Next;
        public uint Flags, ImageType, Format, Width, Height, Depth, MipLevels, ArrayLayers, Samples, Tiling, Usage;
        public uint SharingMode, FamilyCount;
        public uint* Families;
        public int InitialLayout;
    }

    public struct MemoryRequirements
    {
        public ulong Size, Alignment;
        public uint TypeBits;
    }

    public unsafe struct AllocateInfo
    {
        public int SType; public void* Next;
        public ulong Size;
        public uint TypeIndex;
    }

    public unsafe struct FlagsInfo
    {
        public int SType; public void* Next;
        public uint Flags; // VkSemaphoreCreateInfo, VkFenceCreateInfo
    }

    public unsafe struct ImageBarrier
    {
        public int SType; public void* Next;
        public uint SrcAccess, DstAccess;
        public int OldLayout, NewLayout;
        public uint SrcFamily, DstFamily;
        public ulong Image;
        public ColorRange Range;
    }

    public struct ColorRange { public uint Aspect, BaseLevel, Levels, BaseLayer, Layers; }

    public unsafe struct Submit
    {
        public int SType; public void* Next;
        public uint WaitCount;
        public ulong* Waits;
        public uint* WaitStages;
        public uint CommandBufferCount;
        public IntPtr* CommandBuffers;
        public uint SignalCount;
        public ulong* Signals;
    }
}
