namespace Komet.Vulkan;

internal static partial class Vk
{
    public const int PhysicalDeviceFeatures2 = 1000059000, Vulkan12Features = 51, Vulkan13Features = 53;
    public const int DepthClipControlFeatures = 1000355000;
    public const int DepthClipControlCreateInfo = 1000355001, GraphicsPipelineCreateInfo = 28;
    public const int VertexInputStateInfo = 19, InputAssemblyStateInfo = 20, ViewportStateInfo = 22;
    public const int RasterizationStateInfo = 23, MultisampleStateInfo = 24, DepthStencilStateInfo = 25;
    public const int ColorBlendStateInfo = 26, DynamicStateInfo = 27, RenderingCreateInfo = 1000044002;
    public const int RenderingInfoType = 1000044000, RenderingAttachmentInfo = 1000044001;
    public const uint StageVertex = 0x1, StageFragment = 0x10, BindGraphics = 0;
    public const uint TopologyTriangles = 3;
    public const uint FrontClockwise = 1, FrontCounterClockwise = 0;
    public const uint DynamicViewport = 0, DynamicScissor = 1, IndexUint32 = 1;
    public const uint LoadOpLoad = 0, StoreOpStore = 0;
    public const int LayoutColorAttachment = 2, LayoutDepthAttachment = 3, LayoutShaderRead = 5;
    public const uint CombinedSampler = 1;
    public const int FeatureCount = 55, IndependentBlend = 3, MultiDrawIndirect = 9, SamplerAnisotropy = 19;
    public const int FeatureDepthClamp = 11, LayoutDepthReadOnly = 4, FillModeNonSolid = 13, WideLines = 15, FeatureLogicOp = 8;
    public const int DrawIndirectFirstInstance = 10, PipelineCacheCreateInfo = 17;
    public const int DynamicRendering = 12, Vulkan13Count = 15, ExtensionNameBytes = 256;
    public const int DrawIndirectCount = 1, Vulkan12Count = 47;

    public unsafe struct Features2
    {
        public int SType; public void* Next;
        public fixed uint Features[FeatureCount];
    }

    public unsafe struct Features12
    {
        public int SType; public void* Next;
        public fixed uint Features[Vulkan12Count];
    }

    public unsafe struct Features13
    {
        public int SType; public void* Next;
        public fixed uint Features[Vulkan13Count];
    }

    public unsafe struct OneFeature
    {
        public int SType; public void* Next;
        // VkPhysicalDeviceDepthClipControlFeaturesEXT, VkPipelineViewportDepthClipControlCreateInfoEXT
        public uint Enabled;
    }

    public unsafe struct ExtensionProperties
    {
        public fixed byte Name[ExtensionNameBytes];
        public uint SpecVersion;
    }

    public struct VertexBinding { public uint Binding, Stride, InputRate; }

    public struct VertexAttribute { public uint Location, Binding, Format, Offset; }

    public unsafe struct VertexInputState
    {
        public int SType; public void* Next;
        public uint Flags, BindingCount;
        public VertexBinding* Bindings;
        public uint AttributeCount;
        public VertexAttribute* Attributes;
    }

    public unsafe struct InputAssemblyState
    {
        public int SType; public void* Next;
        public uint Flags, Topology, PrimitiveRestart;
    }

    public unsafe struct ViewportState
    {
        public int SType; public void* Next;
        public uint Flags, ViewportCount;
        public void* Viewports;
        public uint ScissorCount;
        public void* Scissors;
    }

    public unsafe struct RasterizationState
    {
        public int SType; public void* Next;
        public uint Flags, DepthClamp, Discard, PolygonMode, CullMode, FrontFace, DepthBias;
        public float BiasConstant, BiasClamp, BiasSlope, LineWidth;
    }

    public unsafe struct MultisampleState
    {
        public int SType; public void* Next;
        public uint Flags, Samples, SampleShading;
        public float MinSampleShading;
        public void* SampleMask;
        public uint AlphaToCoverage, AlphaToOne;
    }

    public struct StencilState { public uint Fail, Pass, DepthFail, Compare, CompareMask, WriteMask, Reference; }

    public unsafe struct DepthStencilState
    {
        public int SType; public void* Next;
        public uint Flags, DepthTest, DepthWrite, DepthCompare, DepthBounds, Stencil;
        public StencilState Front, Back;
        public float MinDepthBounds, MaxDepthBounds;
    }

    public struct BlendAttachment { public uint Enable, SourceColor, TargetColor, ColorOp, SourceAlpha, TargetAlpha, AlphaOp, WriteMask; }

    public unsafe struct ColorBlendState
    {
        public int SType; public void* Next;
        public uint Flags, LogicOpEnable, LogicOp, AttachmentCount;
        public BlendAttachment* Attachments;
        public fixed float Constants[4];
    }

    public unsafe struct DynamicState
    {
        public int SType; public void* Next;
        public uint Flags, Count;
        public uint* States;
    }

    public unsafe struct PipelineRendering
    {
        public int SType; public void* Next;
        public uint ViewMask, ColorCount;
        public uint* ColorFormats;
        public uint DepthFormat, StencilFormat;
    }

    public unsafe struct GraphicsInfo
    {
        public int SType; public void* Next;
        public uint Flags, StageCount;
        public StageInfo* Stages;
        public VertexInputState* VertexInput;
        public InputAssemblyState* InputAssembly;
        public void* Tessellation;
        public ViewportState* Viewport;
        public RasterizationState* Rasterization;
        public MultisampleState* Multisample;
        public DepthStencilState* DepthStencil;
        public ColorBlendState* ColorBlend;
        public DynamicState* Dynamic;
        public ulong Layout, RenderPass;
        public uint Subpass;
        public ulong BasePipeline;
        public int BaseIndex;
    }

    public struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }

    public struct Rect
    {
        public int X, Y;
        public uint Width, Height;
    }

    public unsafe struct Attachment
    {
        public int SType; public void* Next;
        public ulong View;
        public int Layout;
        public uint ResolveMode;
        public ulong ResolveView;
        public int ResolveLayout;
        public uint Load, Store;
        public fixed float Clear[4];
    }

    public unsafe struct RenderingInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public Rect Area;
        public uint Layers, ViewMask, ColorCount;
        public Attachment* Colors;
        public Attachment* Depth;
        public Attachment* Stencil;
    }

    public struct DrawIndexed
    {
        public uint IndexCount, InstanceCount, FirstIndex;
        public int VertexOffset;
        public uint FirstInstance;
    }

    public unsafe struct PipelineCacheInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public nuint InitialDataSize;
        public void* InitialData;
    }
}
