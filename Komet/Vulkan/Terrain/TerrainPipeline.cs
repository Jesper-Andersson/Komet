namespace Komet.Vulkan;

// OpenGL's clip depth (-1..1) kept via VK_EXT_depth_clip_control; front face flipped, as Vulkan's y points down.
internal sealed unsafe partial class TerrainPipeline : IDisposable
{
    private const int MaxResources = 32, MaxAttributes = 16, MaxColors = 8;

    private readonly IntPtr _device;
    private ulong _cache; // the device's pipeline cache, 0 for none
    private uint _pushed; // bytes of push constants both stages read
    private bool _bresenham; // lines as OpenGL rasterizes them
    private int _disposed;
    private static int _live;

    private TerrainPipeline(IntPtr device, Resource[] resources)
    {
        _ = Assert(device != IntPtr.Zero) && Assert(resources.Length <= MaxResources);
        (_device, Resources) = (device, resources);
        _ = Interlocked.Increment(ref _live);
    }

    // Made, not yet disposed, any thread
    public static int Live => Volatile.Read(ref _live);

    public readonly record struct Resource(int Binding, uint Type, uint Stages);

    public readonly record struct Target(uint[] Colors, uint Depth);

    public ulong Pipeline { get; private set; }
    public ulong Layout { get; private set; }
    public ulong SetLayout { get; private set; }
    public Resource[] Resources { get; }

    public static TerrainPipeline? Create(VulkanDevice device, GlslPort.Ported ported,
        TerrainPools.Attribute[] attributes, (GlTap.Fixed State, Target Target, uint Topology) pass, out string why)
    {
        why = "";
        var colors = pass.Target.Colors.Length;
        if (!NotNull(device) || !NotNull(ported) || !Assert(colors is >= 0 and <= MaxColors)) return null;
        if (!device.DepthClipControl)
        {
            why = "the device lacks VK_EXT_depth_clip_control";
            return null;
        }

        var resources = Merged(Spirv.Resources(ported.Vertex), Spirv.Resources(ported.Fragment));
        var pipeline = new TerrainPipeline(device.Handle, resources)
        {
            _pushed = (uint)ported.HotUniforms.Size, _bresenham = device.BresenhamLines, _cache = device.PipelineCache
        };
        var inputs = pipeline.Inputs(ported, attributes, out var missing);
        // formatless inputs refused before the driver: RADV divides by the size (SIGFPE), e.g. old recipes without
        // matrix columns
        why = missing.Length > 0 ? missing : pipeline.Layouts() ?? pipeline.Build(ported, inputs, pass) ?? "";
        if (why.Length == 0) return pipeline;
        pipeline.Dispose();
        return null;
    }

    // Built on a builder from inputs copied here: ports are immutable, device handles change only at start/end (it
    // waits for all builds), and the pipeline cache is not EXTERNALLY_SYNCHRONIZED, so no lock. Dropped: its builder
    // destroys it.
    public static Build<TerrainPipeline> Started(VulkanDevice device, GlslPort.Ported ported,
        TerrainPools.Attribute[] attributes, (GlTap.Fixed State, Target Target, uint Topology) pass)
    {
        _ = NotNull(attributes) && Assert(pass.Target.Colors.Length <= MaxColors);
        TerrainPools.Attribute[] inputs = [.. attributes];
        var copied = (pass.State, new Target([.. pass.Target.Colors], pass.Target.Depth), pass.Topology);
        return new Build<TerrainPipeline>(Builds.Kind.Pipeline,
            () => (Create(device, ported, inputs, copied, out var why), why), static made => made.Dispose());
    }

    private static Resource[] Merged(Spirv.Resource[] vertex, Spirv.Resource[] fragment)
    {
        var merged = new SortedDictionary<int, Resource>();
        var both = vertex.Select(r => (r, Vk.StageVertex)).Concat(fragment.Select(r => (r, Vk.StageFragment)))
            .ToArray();
        foreach (var (r, stage) in both.Bounded(MaxResources * 2))
            merged[r.Binding] = merged.TryGetValue(r.Binding, out var known)
                ? known with { Stages = known.Stages | stage }
                : new Resource(r.Binding, (uint)r.Type, stage);
        _ = Assert(vertex.All(r => r.Set == 0) && fragment.All(r => r.Set == 0));
        return [.. merged.Values];
    }

    private string? Layouts()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Resources.Length <= MaxResources)) return "no device";
        var bindings = stackalloc Vk.LayoutBinding[MaxResources];
        for (var i = 0; i < Math.Min(Resources.Length, MaxResources); i++)
            bindings[i] = new Vk.LayoutBinding
            {
                Binding = (uint)Resources[i].Binding, Type = (int)Resources[i].Type, Count = 1,
                Stages = Resources[i].Stages
            };
        var setInfo = new Vk.SetLayoutInfo
        {
            SType = Vk.SetLayoutCreateInfo, Flags = Vk.PushDescriptorLayout, BindingCount = (uint)Resources.Length,
            Bindings = bindings
        };
        ulong set, layout;
        if (VkApi.CreateSetLayout(_device, &setInfo, null, &set) != Vk.Success) return "no descriptor set layout";
        SetLayout = set;
        var push = new Vk.PushRange { Stages = Vk.StageVertex | Vk.StageFragment, Size = _pushed };
        var layoutInfo = new Vk.PipelineLayoutInfo
        {
            SType = Vk.PipelineLayoutCreateInfo, SetLayoutCount = 1, SetLayouts = &set,
            PushRangeCount = _pushed > 0 ? 1u : 0, PushRanges = &push
        };
        if (VkApi.CreatePipelineLayout(_device, &layoutInfo, null, &layout) != Vk.Success) return "no pipeline layout";
        Layout = layout;
        return null;
    }

    public void Dispose()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Resources.Length <= MaxResources)) return;
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _ = Interlocked.Decrement(ref _live);
        if (Pipeline != 0) VkApi.DestroyPipeline(_device, Pipeline, null);
        if (Layout != 0) VkApi.DestroyPipelineLayout(_device, Layout, null);
        if (SetLayout != 0) VkApi.DestroySetLayout(_device, SetLayout, null);
        (Pipeline, Layout, SetLayout) = (0, 0, 0);
    }
}
