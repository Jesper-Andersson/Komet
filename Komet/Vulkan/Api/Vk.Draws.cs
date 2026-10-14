
namespace Komet.Vulkan;

internal static partial class Vk
{
    public const int LayoutTransferSrc = 6, ViewType2DArray = 5, ViewTypeCube = 3;
    public const uint CubeCompatible = 0x10;
    public const uint FilterLinear = 1, FilterNearest = 0;
    public const uint TopologyLines = 1, TopologyLineStrip = 2, TopologyTriangleStrip = 4;
    public const uint TopologyTriangleFan = 5, IndexUint16 = 0, DynamicLineWidth = 2;

    public const int LineFeatures = 1000259000, LineStateInfo = 1000259001, QueryPoolCreateInfo = 11;
    public const uint QueryOcclusion = 0, QueryTimestamp = 2, QueryPrecise = 1, Result64 = 1, ResultWait = 2;
    public const uint ResultAvailability = 4;
    public const int OcclusionQueryPrecise = 23, PipelineStatisticsQuery = 24, ShaderFloat64 = 39;

    // Pipeline statistics, written in order: VS invocations, clipping invocations, clipping primitives, FS invocations
    public const uint QueryPipelineStatistics = 1, CountedStatistics = 0x4 | 0x20 | 0x40 | 0x80;

    public struct PushRange { public uint Stages, Offset, Size; }

    public unsafe struct QueryPoolInfo
    {
        public int SType; public void* Next;
        public uint Flags, QueryType, QueryCount, PipelineStatistics;
    }
    public const uint LineBresenham = 2;

    // VkPhysicalDeviceLineRasterizationFeaturesKHR
    public unsafe struct LineFeaturesInfo
    {
        public int SType; public void* Next;
        public uint Rectangular, Bresenham, Smooth, StippledRectangular, StippledBresenham, StippledSmooth;
    }

    // VkPipelineRasterizationLineStateCreateInfoKHR
    public unsafe struct LineState
    {
        public int SType; public void* Next;
        public uint Mode, Stippled, Factor;
        public ushort Pattern;
    }

    public struct Layers { public uint Aspect, Level, BaseLayer, Count; }

    public struct ImageBlit
    {
        public Layers Source;
        public int SourceX0, SourceY0, SourceZ0, SourceX1, SourceY1, SourceZ1;
        public Layers Target;
        public int TargetX0, TargetY0, TargetZ0, TargetX1, TargetY1, TargetZ1;
    }

    // VkImageCopy: same-sized regions between two images of compatible formats, texel for texel
    public struct ImageCopy
    {
        public Layers Source;
        public int SourceX, SourceY, SourceZ;
        public Layers Target;
        public int TargetX, TargetY, TargetZ;
        public uint Width, Height, Depth;
    }

    public const uint StageHost = 0x4000, AccessHostRead = 0x2000;

    // VkClearAttachment: the value is a VkClearValue - four floats for a color, depth and stencil for a depth
    public struct ClearAttachment
    {
        public uint Aspect, Attachment;
        public float Red, Green, Blue, Alpha;
    }

    public struct ClearRect
    {
        public Rect Rect;
        public uint BaseLayer, Layers;
    }
}
