
namespace Komet.Vulkan;

internal static partial class Vk
{
    public const int BufferCreateInfo = 12, ImageViewCreateInfo = 15, ShaderModuleCreateInfo = 16, StageCreateInfo = 18;
    public const int ComputePipelineCreateInfo = 29, PipelineLayoutCreateInfo = 30, SamplerCreateInfo = 31;
    public const int SetLayoutCreateInfo = 32, WriteDescriptorSet = 35, MemoryBarrier = 46;
    public const uint HostVisible = 0x2, HostCoherent = 0x4, HostCached = 0x8, UsageUniform = 0x10, UsageStorage = 0x20;
    public const uint UsageIndex = 0x40, UsageVertex = 0x80, UsageIndirect = 0x100;
    public const uint StageCompute = 0x20, PushDescriptorLayout = 0x1;
    public const uint PipelineCompute = 0x800, PipelineIndirect = 0x2, AccessIndirect = 0x1, AccessShaderRead = 0x20;
    public const uint AccessShaderWrite = 0x40, AccessTransferRead = 0x800;
    public const int BindCompute = 1, ViewType2D = 1, FormatR32F = 100;

    public struct BufferCopy { public ulong SourceOffset, TargetOffset, Size; }

    public unsafe struct BufferInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public ulong Size;
        public uint Usage, SharingMode, FamilyCount;
        public uint* Families;
    }

    public unsafe struct ViewInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public ulong Image;
        public int ViewType;
        public uint Format, R, G, B, A;
        public ColorRange Range;
    }

    public unsafe struct SamplerInfo
    {
        public int SType; public void* Next;
        public uint Flags, MagFilter, MinFilter, MipmapMode, AddressU, AddressV, AddressW;
        public float LodBias;
        public uint Anisotropy;
        public float MaxAnisotropy;
        public uint Compare, CompareOp;
        public float MinLod, MaxLod;
        public uint Border, Unnormalized;
    }

    public unsafe struct ModuleInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public nuint CodeSize;
        public uint* Code;
    }

    public unsafe struct LayoutBinding
    {
        public uint Binding;
        public int Type;
        public uint Count, Stages;
        public ulong* Samplers;
    }

    public unsafe struct SetLayoutInfo
    {
        public int SType; public void* Next;
        public uint Flags, BindingCount;
        public LayoutBinding* Bindings;
    }

    public unsafe struct PipelineLayoutInfo
    {
        public int SType; public void* Next;
        public uint Flags, SetLayoutCount;
        public ulong* SetLayouts;
        public uint PushRangeCount;
        public void* PushRanges;
    }

    public unsafe struct StageInfo
    {
        public int SType; public void* Next;
        public uint Flags, Stage;
        public ulong Module;
        public byte* Name;
        public void* Specialization;
    }

    public unsafe struct ComputeInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public StageInfo Stage;
        public ulong Layout, Base;
        public int BaseIndex;
    }

    public unsafe struct WriteInfo
    {
        public int SType; public void* Next;
        public ulong Set;
        public uint Binding, ArrayElement, Count;
        public int Type;
        public ImageDescriptor* Images;
        public BufferDescriptor* Buffers;
        public void* TexelViews;
    }

    public struct BufferDescriptor { public ulong Buffer, Offset, Range; }

    public struct ImageDescriptor
    {
        public ulong Sampler, View;
        public int Layout;
    }

    public unsafe struct GlobalBarrier
    {
        public int SType; public void* Next;
        public uint SrcAccess, DstAccess;
    }

    public struct BufferImageCopy
    {
        public ulong Offset;
        public uint RowLength, ImageHeight, Aspect, Level, BaseLayer, Layers;
        public int X, Y, Z;
        public uint Width, Height, Depth;
    }
}
