using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// A vertex array or texture not seen being made is asked from OpenGL once, on first sight
internal static unsafe partial class GlTap
{
    public const int MaxAttributes = 16, MaxBindings = 16;
    public const uint Texture2DArray = 0x8C1A, TextureCube = 0x8513, Fill = 0x1B02;
    private const int MaxArrays = 1 << 16, MaxKnownTextures = 1 << 16;
    private const uint ArrayBuffer = 0x8892, ElementBuffer = 0x8893, UniformBuffer = 0x8A11, StorageBuffer = 0x90D2;
    private const uint CopyRead = 0x8F36, CopyWrite = 0x8F37, UnpackBuffer = 0x88EC, IndirectBuffer = 0x8F3F;
    private const uint PackBuffer = 0x88EB, ParameterBuffer = 0x80EE;
    private const uint CubeFirst = 0x8515, CubeLast = 0x851A;

    // One vertex attribute as glVertexAttrib(I)Pointer left it; Stride is what OpenGL steps by (never 0)
    public readonly record struct Attribute(bool Enabled, uint Buffer, int Size, uint Type, bool Normalized,
        bool Integer, int Stride, long Offset, uint Divisor);

    // A VAO's attributes by location and element buffer; Changes: attribute changes (element buffer rebinds per draw)
    public sealed class VertexArray
    {
        public Attribute[] Attributes { get; } = new Attribute[MaxAttributes];
        public uint Elements { get; set; }
        public long Changes { get; set; }
    }

    private static readonly Dictionary<uint, VertexArray> Arrays = [];
    private static readonly Dictionary<uint, uint> TextureTargets = []; // each texture's target, asked once
    private static readonly (uint Buffer, long Offset, long Size)[] Uniforms = new (uint, long, long)[MaxBindings];
    private static readonly (uint Buffer, long Offset, long Size)[] Storages = new (uint, long, long)[MaxBindings];
    private static readonly uint[] UnitArrays = new uint[MaxUnits], UnitCubes = new uint[MaxUnits];
    private static uint _vao, _arrayBuffer, _uniformBuffer, _storageBuffer, _copyRead, _copyWrite, _unpackBuffer;
    private static uint _indirect, _packBuffer, _parameters, _polygonMode = Fill;
    private static float _lineWidth = 1, _widest = 1;
    private static uint _logicOp = 0x1503; // GL_COPY
    private static (int RowLength, int SkipRows, int SkipPixels, int Alignment) _unpack = (0, 0, 0, 4), _pack = (0, 0, 0, 4);
    private const int ProfileMask = 0x9126;
    private static VertexArray? _array;
    private static (int X, int Y, int Width, int Height) _scissor;
    private static (float R, float G, float B, float A) _clearColor;
    private static double _clearDepth = 1;

    public static IGlWrites? Writes { get; set; }

    // What the tap keeps by GL name, for the report; Bytes: approx., of vertex arrays (16 attributes each) and targets
    public static (int Programs, int Arrays, int Textures, int Framebuffers, long Bytes) Held =>
        Assert(Arrays.Count <= MaxArrays) && Assert(TextureTargets.Count <= MaxKnownTextures)
            ? (Programs.Count, Arrays.Count, TextureTargets.Count, Buffers.Count,
                (730L * Arrays.Count) + (24L * TextureTargets.Count))
            : default;

    // An occlusion query began (target, id) or ended (target, 0)
    public static Action<uint, uint>? Querying { get; set; }

    // A query's result or availability (id, GL_QUERY_RESULT or _AVAILABLE) from Vulkan; null: OpenGL answers
    public static System.Func<uint, uint, long?>? Answering { get; set; }

    public static (int X, int Y, int Width, int Height) Scissor => _scissor;
    public static uint PolygonMode => _polygonMode;
    public static float LineWidth => _lineWidth;

    // glLogicOp's operation (GL_CLEAR 0x1500 to GL_SET 0x150F), used while GL_COLOR_LOGIC_OP is on
    public static uint LogicOperation => _logicOp;

    // glPixelStorei's GL_UNPACK_*, and whether GL_PIXEL_UNPACK_BUFFER is bound (the pointer is then an offset into it)
    public static (int RowLength, int SkipRows, int SkipPixels, int Alignment) Unpack => _unpack;
    public static bool Unpacking => _unpackBuffer != 0;

    // glPixelStorei's GL_PACK_* and GL_PIXEL_PACK_BUFFER, while the Objects group hears of them (Scene)
    public static ((int RowLength, int SkipRows, int SkipPixels, int Alignment) Store, uint Buffer)? Pack =>
        Scene && Assert(_pack.Alignment >= 0) ? (_pack, _packBuffer) : null;

    public static VertexArray? BoundArray => _vao == 0 ? null : _array ??= ArrayOf(_vao);

    public static (uint Buffer, long Offset, long Size) UniformBinding(int index) =>
        Index(index, MaxBindings) && Assert(Uniforms.Length == MaxBindings) ? Uniforms[index] : default;

    public static (uint Buffer, long Offset, long Size) StorageBinding(int index) =>
        Index(index, MaxBindings) && Assert(Storages.Length == MaxBindings) ? Storages[index] : default;

    public static uint UnitTexture(int unit, uint target)
    {
        if (!Index(unit, MaxUnits) || !Assert(UnitArrays.Length == MaxUnits)) return 0;
        return target switch { Texture2DArray => UnitArrays[unit], TextureCube => UnitCubes[unit], _ => UnitTextures[unit] };
    }

    // The target a texture was made for, asked once (DSA binds by it)
    public static uint TargetOf(uint texture)
    {
        if (texture == 0 || !Assert(TextureTargets.Count <= MaxKnownTextures)) return Texture2D;
        if (TextureTargets.TryGetValue(texture, out var known)) return known;
        if (Recording && TextureRecords.TryGetValue(texture, out var recorded) && recorded.Target != 0)
            return recorded.Target;
        GL.GetTextureParameter((int)texture, (GetTextureParameter)0x1006, out int target); // GL_TEXTURE_TARGET
        var made = target == 0 ? Texture2D : (uint)target;
        if (TextureTargets.Count >= MaxKnownTextures) TextureTargets.Clear();
        TextureTargets[texture] = made;
        return made;
    }

    public static void Sampled(List<uint> found)
    {
        if (!NotNull(found) || !Assert(UnitTextures.Length == MaxUnits)) return;
        found.Clear();
        if (_samplers is not { } samplers) return;
        if (!ReferenceEquals(_unitsOf, samplers) || _unitsAt != SamplerVersion)
            (_units, _unitsOf, _unitsAt) = ([.. samplers.Values], samplers, SamplerVersion); // made again only on a change
        foreach (var unit in _units.Bounded(MaxArray * 4))
        {
            if (!Index(unit, MaxUnits)) continue;
            foreach (var texture in (ReadOnlySpan<uint>)[UnitTextures[unit], UnitArrays[unit], UnitCubes[unit]])
                if (texture != 0 && !found.Contains(texture))
                    found.Add(texture);
        }
    }

    private static int[] _units = [];
    private static Dictionary<int, int>? _unitsOf;
    private static long _unitsAt = -1;

    // A vertex array as the tap follows it, while the scene is tapped
    public static VertexArray? ArrayAt(uint vao) =>
        Scene && Assert(vao > 0) && Assert(Arrays.Count <= MaxArrays) ? ArrayOf(vao) : null;

    private static VertexArray? ArrayOf(uint vao)
    {
        if (vao == 0 || !Assert(Arrays.Count <= MaxArrays)) return null;
        if (Arrays.TryGetValue(vao, out var known)) return known;
        var made = new VertexArray();
        _ = Assert(vao > 0) && Assert(made.Attributes.Length == MaxAttributes);
        for (var i = 0; i < MaxAttributes; i++)
        {
            int Get(int name)
            {
                GL.GetVertexArrayIndexed((int)vao, i, (VertexArrayIndexedParameter)name, out int value);
                return Assert(name > 0) && Index(i, MaxAttributes) ? value : 0;
            }

            var offset = new long[1];
            GL.GetVertexArrayIndexed64((int)vao, i, (VertexArrayIndexed64Parameter)0x82D7, offset);
            var (size, type) = (Get(0x8623), (uint)Get(0x8625));
            made.Attributes[i] = new Attribute(Get(0x8622) != 0, (uint)Get(0x889F), size, type, Get(0x886A) != 0,
                Get(0x88FD) != 0, Effective(Get(0x82D8), size, type), offset[0], (uint)Get(0x88FE));
        }

        GL.GetVertexArray((int)vao, VertexArrayParameter.ElementArrayBufferBinding, out int elements);
        made.Elements = (uint)elements;
        if (Arrays.Count < MaxArrays) Arrays[vao] = made;
        _ = Assert(elements >= 0);
        return made;
    }

    // 0 means tightly packed
    private static int Effective(int stride, int size, uint type)
    {
        if (!Assert(stride >= 0) || !Assert(size is >= 0 and <= 4 or 0x80E1)) return 0;
        if (stride > 0) return stride;
        return type switch
        {
            0x1400 or 0x1401 => size, // bytes
            0x1402 or 0x1403 or 0x140B => 2 * size, // shorts, half floats
            0x8D9F or 0x8368 or 0x8C3B => 4, // the packed 2_10_10_10 and 10F_11F_11F formats
            0x140A => 8 * size, // doubles
            _ => 4 * size
        };
    }

    // The element buffer is the bound vertex array's. Vulkan-only also follows VAO 0's (GL keeps one): the engine binds
    // a pool's index buffer with none bound to fill it (updateIndices), which must reach Vulkan's copy
    private static uint _looseElements;

    private static uint Bound(uint target)
    {
        _ = Assert(Uniforms.Length == MaxBindings) && Assert(Storages.Length == MaxBindings);
        return target switch
        {
            ArrayBuffer => _arrayBuffer, ElementBuffer => BoundArray?.Elements ?? (Recording ? _looseElements : 0),
            UniformBuffer => _uniformBuffer,
            StorageBuffer => _storageBuffer, CopyRead => _copyRead, CopyWrite => _copyWrite,
            UnpackBuffer => _unpackBuffer, IndirectBuffer => _indirect, PackBuffer => _packBuffer,
            ParameterBuffer => _parameters, _ => 0
        };
    }

    private static void BindTo(uint target, uint buffer)
    {
        switch (target)
        {
            case ArrayBuffer: _arrayBuffer = buffer; break;
            case ElementBuffer when BoundArray is { } array: array.Elements = buffer; break;
            case ElementBuffer when _vao == 0: _looseElements = buffer; break;
            case UniformBuffer: _uniformBuffer = buffer; break;
            case StorageBuffer: _storageBuffer = buffer; break;
            case CopyRead: _copyRead = buffer; break;
            case CopyWrite: _copyWrite = buffer; break;
            case UnpackBuffer: _unpackBuffer = buffer; break;
            case IndirectBuffer: _indirect = buffer; break;
            case PackBuffer: _packBuffer = buffer; break;
            case ParameterBuffer: _parameters = buffer; break;
        }

        _ = Assert(Bound(target) == buffer || target is not (ArrayBuffer or UniformBuffer)) && Assert(Version >= 0);
    }

    private static uint BoundName(uint target)
    {
        var unit = _unit % MaxUnits;
        _ = Assert(unit < MaxUnits) && Assert(UnitArrays.Length == MaxUnits);
        return target switch
        {
            Texture2DArray => UnitArrays[unit], TextureCube or >= CubeFirst and <= CubeLast => UnitCubes[unit],
            _ => UnitTextures[unit]
        };
    }

    // GL_PACK_* as OpenGL holds them: a wait for Mesa's glthread
    public static (int RowLength, int SkipRows, int SkipPixels, int Alignment) Packing()
    {
        var store = (GL.GetInteger(GetPName.PackRowLength), GL.GetInteger(GetPName.PackSkipRows),
            GL.GetInteger(GetPName.PackSkipPixels), GL.GetInteger(GetPName.PackAlignment));
        _ = Assert(store.Item4 is 1 or 2 or 4 or 8) && Assert(store.Item1 >= 0);
        return store;
    }

    private static void SeedObjects()
    {
        (_vao, _array) = ((uint)GL.GetInteger(GetPName.VertexArrayBinding), null);
        _arrayBuffer = (uint)GL.GetInteger(GetPName.ArrayBufferBinding);
        _looseElements = _vao == 0 ? (uint)GL.GetInteger(GetPName.ElementArrayBufferBinding) : 0;
        _uniformBuffer = (uint)GL.GetInteger(GetPName.UniformBufferBinding);
        _storageBuffer = (uint)GL.GetInteger((GetPName)0x90D3);
        (_copyRead, _copyWrite) = ((uint)GL.GetInteger((GetPName)0x8F36), (uint)GL.GetInteger((GetPName)0x8F37));
        (_unpackBuffer, _indirect) = ((uint)GL.GetInteger((GetPName)0x88EF), (uint)GL.GetInteger((GetPName)0x8F43));
        _unpack = (GL.GetInteger(GetPName.UnpackRowLength), GL.GetInteger(GetPName.UnpackSkipRows),
            GL.GetInteger(GetPName.UnpackSkipPixels), GL.GetInteger(GetPName.UnpackAlignment));
        (_pack, _packBuffer) = (Packing(), (uint)GL.GetInteger((GetPName)0x88ED));
        for (var i = 0; i < MaxBindings; i++)
        {
            GL.GetInteger(GetIndexedPName.UniformBufferBinding, i, out int uniform);
            GL.GetInteger((GetIndexedPName)0x90D3, i, out int storage);
            GL.GetInteger64((GetIndexedPName)0x8A29, i, out long start);
            GL.GetInteger64((GetIndexedPName)0x8A2A, i, out long size);
            Uniforms[i] = ((uint)uniform, start, size);
            Storages[i] = ((uint)storage, 0, 0);
        }

        var box = new int[4];
        GL.GetInteger(GetPName.ScissorBox, box);
        _scissor = (box[0], box[1], box[2], box[3]);
        var clear = new float[4];
        GL.GetFloat(GetPName.ColorClearValue, clear);
        (_clearColor, _clearDepth) = ((clear[0], clear[1], clear[2], clear[3]), GL.GetDouble(GetPName.DepthClearValue));
        var modes = new int[2];
        GL.GetInteger(GetPName.PolygonMode, modes);
        _polygonMode = (uint)modes[0];
        _lineWidth = GL.GetFloat(GetPName.LineWidth);
        _logicOp = (uint)GL.GetInteger(GetPName.LogicOpMode);
        var range = new float[2];
        GL.GetFloat(GetPName.AliasedLineWidthRange, range);
        // a forward-compatible core context refuses lines wider than one (GL_INVALID_VALUE): the width stays
        var refused = (GL.GetInteger(GetPName.ContextFlags) & 1) != 0 && (GL.GetInteger((GetPName)ProfileMask) & 1) != 0;
        _widest = refused ? 1 : Math.Max(range[1], 1);
        var active = GL.GetInteger(GetPName.ActiveTexture);
        for (var unit = 0; unit < MaxUnits; unit++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            UnitArrays[unit] = (uint)GL.GetInteger(GetPName.TextureBinding2DArray);
            UnitCubes[unit] = (uint)GL.GetInteger(GetPName.TextureBindingCubeMap);
        }

        GL.ActiveTexture((TextureUnit)active);
        _ = Assert(_scissor.Width >= 0) && Assert(_polygonMode != 0);
        _ = Assert(UnitArrays.Length == MaxUnits) && Assert(active >= (int)Texture0) &&
            Assert(UnitCubes.Length == MaxUnits);
    }
}

// Told of every write into a buffer or a texture, before the driver runs it; data points at client memory (or is null)
internal interface IGlWrites
{
    // glBufferData/glBufferStorage (and named): respecified with first contents; persistent: mapped for good, unseen
    void BufferData(uint buffer, long size, IntPtr data, bool persistent);

    void BufferSubData(uint buffer, long offset, long size, IntPtr data);

    void BufferCopy(uint from, uint to, long fromOffset, long toOffset, long size);

    // A write the mirror cannot see: a mapping, a clear, Komet's own; why names which, for the report
    void BufferUnseen(uint buffer, string why);

    void BuffersGone(ReadOnlySpan<uint> buffers);

    // A texture's contents changed (an upload, a copy into it, its mipmaps made)
    void TextureChanged(uint texture);

    // One of a texture's parameters set (the first value of a vector one)
    void TextureTuned(uint texture, uint name, float value);

    // glGenerateMipmap on 2D: true when its copy makes the levels too (Vulkan-only: any followed texture, recorded)
    bool TextureMipmapped(uint texture);

    // Vulkan-only (GlTap.Recording), any followed target, after the record took it: a level specified or storage made
    // (glTexImage*, glTex(ture)Storage*: Levels at once). target: bind target or cube face; data: format and type, 0
    // for none; pixels: client memory, 0 for none or a pixel unpack buffer's (then also changed)
    void TextureImaged(uint texture, uint target, (int Level, int Levels) levels,
        (int Format, int Width, int Height, int Depth) spec, (uint Format, uint Type) data, IntPtr pixels);

    // Vulkan-only: client-memory upload into a box of a level (Z: layer or cube face); true when taken, else changed
    bool TextureSubImaged(uint texture, uint target, int level, (int X, int Y, int Z, int Width, int Height, int Depth) box,
        (uint Format, uint Type) data, IntPtr pixels);

    // Vulkan-only: glCopyImageSubData from one texture's level (and layer Z) into another's; true when taken
    bool TextureCopied((uint Texture, int Level, int X, int Y, int Z) to, (uint Texture, int Level, int X, int Y, int Z) from,
        (int Width, int Height, int Depth) size);

    // Vulkan-only: glCopyTexSubImage2D from an FBO's read buffer (Read: GlTap.ReadBufferOf) into a level (Z: layer)
    bool TextureRead(uint texture, int level, (int X, int Y, int Z) at, (int Fbo, int Read) from,
        (int X, int Y, int Width, int Height) rect);
}
