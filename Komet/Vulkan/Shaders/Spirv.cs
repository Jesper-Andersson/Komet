namespace Komet.Vulkan;

// Enough for Komet's compute and the engine's shaders: storage/uniform buffers, sampled and storage images.
internal static partial class Spirv
{
    public const uint Magic = 0x07230203;
    // VkDescriptorType
    public const int SampledImage = 1, StorageImage = 3, UniformBuffer = 6, StorageBuffer = 7;
    private const int MaxWords = 1 << 22, MaxIds = 1 << 20, MaxResources = 64, Header = 5;

    private const int OpTypeImage = 25, OpTypeSampledImage = 27, OpTypeStruct = 30, OpTypePointer = 32, OpVariable = 59;
    private const int OpDecorate = 71, OpTypeArray = 28, OpTypeRuntimeArray = 29;
    private const int DecorationBufferBlock = 3, DecorationBinding = 33, DecorationSet = 34;
    private const int UniformConstant = 0, Uniform = 2, StorageBufferClass = 12;

    public readonly record struct Resource(int Set, int Binding, int Type);

    public static Resource[] Resources(ReadOnlySpan<uint> words)
    {
        if (words.Length < Header || words[0] != Magic || !Assert(words.Length <= MaxWords)) return [];
        var bound = (int)Math.Min(words[3], MaxIds);
        var ids = new Id[bound];
        var variables = new List<int>();
        var at = Header;
        for (var step = 0; step < MaxWords && at < words.Length; step++)
        {
            var (count, op) = ((int)(words[at] >> 16), (int)(words[at] & 0xFFFF));
            if (count == 0 || at + count > words.Length) break;
            Read(words.Slice(at, count), op, ids, variables);
            at += count;
        }

        var resources = new List<Resource>();
        foreach (var variable in variables.Bounded(MaxResources))
        {
            if (!Index(variable, ids.Length)) continue;
            // Through the pointer and any arrays to an image, sampled image or block
            var (storage, type) = (ids[variable].Storage, ids[variable].Of);
            for (var hop = 0; hop < 8 && Index(type, ids.Length) && ids[type].Kind == 0; hop++) type = ids[type].Of;
            if (!Index(type, ids.Length)) continue;
            var descriptor = (storage, ids[type].Kind) switch
            {
                (UniformConstant, SampledImage) => SampledImage,
                (UniformConstant, SampledImage + 1) => 2, // a separate image: VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE
                (UniformConstant, StorageImage) => StorageImage,
                (StorageBufferClass, -1) => StorageBuffer,
                (Uniform, -1) => ids[type].BufferBlock ? StorageBuffer : UniformBuffer,
                _ => 0
            };
            if (descriptor > 0) resources.Add(new Resource(ids[variable].Set, ids[variable].Binding, descriptor));
        }

        resources.Sort((a, b) => a.Set != b.Set ? a.Set.CompareTo(b.Set) : a.Binding.CompareTo(b.Binding));
        return Assert(resources.Count <= MaxResources) ? [.. resources] : [];
    }

    private static void Read(ReadOnlySpan<uint> op, int code, Id[] ids, List<int> variables)
    {
        if (!Assert(op.Length > 0) || op.Length < 2) return;
        switch (code)
        {
            case OpDecorate when op.Length >= 3 && op[1] < ids.Length:
                ref var target = ref ids[op[1]];
                if (op[2] == DecorationBinding && op.Length >= 4) target.Binding = (int)op[3];
                else if (op[2] == DecorationSet && op.Length >= 4) target.Set = (int)op[3];
                else if (op[2] == DecorationBufferBlock) target.BufferBlock = true;
                break;
            case OpVariable when op.Length >= 4 && op[2] < ids.Length:
                (ids[op[2]].Of, ids[op[2]].Storage) = ((int)op[1], (int)op[3]);
                if (op[3] is UniformConstant or Uniform or StorageBufferClass) variables.Add((int)op[2]);
                break;
            case OpTypePointer or OpTypeArray or OpTypeRuntimeArray when op.Length >= 3 && op[1] < ids.Length:
                ids[op[1]].Of = (int)op[code == OpTypePointer ? 3 : 2];
                break;
            case OpTypeImage when op.Length >= 8 && op[1] < ids.Length:
                ids[op[1]].Kind = op[7] == 2 ? StorageImage : SampledImage + 1; // Sampled 2: a storage image
                break;
            case OpTypeSampledImage or OpTypeStruct when op[1] < ids.Length:
                ids[op[1]].Kind = code == OpTypeSampledImage ? SampledImage : -1;
                break;
        }

        _ = Assert(ids.Length > 0);
    }

    private struct Id
    {
        public int Of, Storage, Kind, Set, Binding;
        public bool BufferBlock;
    }
}
