namespace Komet.Vulkan;

// Vulkan-only API for mods' raw GL: depth/stencil, points, 8-bit indices, divisors, indirect draws, image clears
internal static partial class Vk
{
    public const uint AspectStencil = 0x4, AspectDepthStencil = AspectDepth | AspectStencil;
    public const uint FormatD24S8 = 129, FormatD32S8 = 130, TopologyPoints = 0;
    public const uint IndexUint8 = 1000265000; // VK_INDEX_TYPE_UINT8_EXT
    public const uint DynamicStencilCompare = 6, DynamicStencilWrite = 7, DynamicStencilReference = 8;
    public const uint StencilFront = 0x1, StencilBack = 0x2;

    // VkFormatFeatureFlags of optimal tiling
    public const uint FeatureSampled = 0x1, FeatureDepthStencilAttachment = 0x200, FeatureBlitSource = 0x400,
        FeatureBlitTarget = 0x800;

    public const int IndexTypeUint8Features = 1000265000; // VkPhysicalDeviceIndexTypeUint8FeaturesEXT
    public const int DivisorFeatures = 1000190002, DivisorStateInfo = 1000190001; // VK_EXT_vertex_attribute_divisor
    public const string IndexUint8Extension = "VK_EXT_index_type_uint8", DivisorExtension = "VK_EXT_vertex_attribute_divisor";

    public struct FormatProperties { public uint Linear, Optimal, Buffer; }

    // VkPhysicalDeviceVertexAttributeDivisorFeaturesEXT
    public unsafe struct TwoFeatures
    {
        public int SType; public void* Next;
        public uint First, Second;
    }

    public struct BindingDivisor { public uint Binding, Divisor; }

    // VkPipelineVertexInputDivisorStateCreateInfoEXT
    public unsafe struct DivisorState
    {
        public int SType; public void* Next;
        public uint Count;
        public BindingDivisor* Divisors;
    }

    // VkClearDepthStencilValue
    public struct DepthStencilValue
    {
        public float Depth;
        public uint Stencil;
    }
}
