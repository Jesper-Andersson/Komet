using System.Runtime.InteropServices;
using unsafe BindBufferFn = delegate* unmanaged<uint, uint, void>;
using unsafe BindBufferBaseFn = delegate* unmanaged<uint, uint, uint, void>;
using unsafe BindBufferRangeFn = delegate* unmanaged<uint, uint, uint, nint, nint, void>;
using unsafe DeleteBuffersFn = delegate* unmanaged<int, uint*, void>;
using unsafe NamedBufferStorageFn = delegate* unmanaged<uint, nint, nint, uint, void>;
using unsafe BindVertexArrayFn = delegate* unmanaged<uint, void>;
using unsafe AttribPointerFn = delegate* unmanaged<uint, int, uint, byte, int, nint, void>;
using unsafe AttribIPointerFn = delegate* unmanaged<uint, int, uint, int, nint, void>;
using unsafe AttribIndexFn = delegate* unmanaged<uint, void>;
using unsafe AttribDivisorFn = delegate* unmanaged<uint, uint, void>;
using unsafe DeleteVertexArraysFn = delegate* unmanaged<int, uint*, void>;
using unsafe ArrayVertexBufferFn = delegate* unmanaged<uint, uint, uint, nint, int, void>;
using unsafe ArrayElementBufferFn = delegate* unmanaged<uint, uint, void>;
using unsafe TexParameteriFn = delegate* unmanaged<uint, uint, int, void>;
using unsafe TexParameterfFn = delegate* unmanaged<uint, uint, float, void>;
using unsafe TexParameterivFn = delegate* unmanaged<uint, uint, int*, void>;
using unsafe TexParameterfvFn = delegate* unmanaged<uint, uint, float*, void>;
using unsafe SetScissorFn = delegate* unmanaged<int, int, int, int, void>;
using unsafe SetPolygonModeFn = delegate* unmanaged<uint, uint, void>;
using unsafe SetClearColorFn = delegate* unmanaged<float, float, float, float, void>;
using unsafe SetClearDepthFn = delegate* unmanaged<double, void>;
using unsafe SetClearDepthfFn = delegate* unmanaged<float, void>;
using unsafe MakeArraysFn = delegate* unmanaged<int, uint*, void>;
using unsafe BeginQueryFn = delegate* unmanaged<uint, uint, void>;
using unsafe EndQueryFn = delegate* unmanaged<uint, void>;
using unsafe QueryObjectivFn = delegate* unmanaged<uint, uint, int*, void>;
using unsafe QueryObjectuivFn = delegate* unmanaged<uint, uint, uint*, void>;
using unsafe SetLineWidthFn = delegate* unmanaged<float, void>;
using unsafe PixelStoreiFn = delegate* unmanaged<uint, int, void>;
using unsafe ReadBufferFn = delegate* unmanaged<uint, void>;
using unsafe NamedReadBufferFn = delegate* unmanaged<uint, uint, void>;
using unsafe LogicOpFn = delegate* unmanaged<uint, void>;

namespace Komet.Vulkan;

// Name-making entries note names after the driver's call. Texture parameters change copy sampling: Writes hears them.
internal static unsafe partial class GlTap
{
    private const int ObjectsAt = TouchAt + TouchCount, ObjectCount = 36, MaxNames = 4096, MaxDeleted = 1 << 20;

    private static Entry[] ObjectEntries() =>
    [
        new("glBindBuffer", (nint)(BindBufferFn)(&BindBuffer), Group.Objects),
        new("glBindBufferBase", (nint)(BindBufferBaseFn)(&BindBufferBase), Group.Objects),
        new("glBindBufferRange", (nint)(BindBufferRangeFn)(&BindBufferRange), Group.Objects),
        new("glDeleteBuffers", (nint)(DeleteBuffersFn)(&DeleteBuffers), Group.Objects),
        new("glNamedBufferStorage", (nint)(NamedBufferStorageFn)(&NamedBufferStorage), Group.Objects),
        new("glBindVertexArray", (nint)(BindVertexArrayFn)(&BindVertexArray), Group.Objects),
        new("glVertexAttribPointer", (nint)(AttribPointerFn)(&AttribPointer), Group.Objects),
        new("glVertexAttribIPointer", (nint)(AttribIPointerFn)(&AttribIPointer), Group.Objects),
        new("glEnableVertexAttribArray", (nint)(AttribIndexFn)(&EnableAttrib), Group.Objects),
        new("glDisableVertexAttribArray", (nint)(AttribIndexFn)(&DisableAttrib), Group.Objects),
        new("glVertexAttribDivisor", (nint)(AttribDivisorFn)(&AttribDivisor), Group.Objects),
        new("glDeleteVertexArrays", (nint)(DeleteVertexArraysFn)(&DeleteVertexArrays), Group.Objects),
        new("glVertexArrayVertexBuffer", (nint)(ArrayVertexBufferFn)(&ArrayVertexBuffer), Group.Objects),
        new("glVertexArrayElementBuffer", (nint)(ArrayElementBufferFn)(&ArrayElementBuffer), Group.Objects),
        new("glTexParameteri", (nint)(TexParameteriFn)(&TexParameteri), Group.Objects),
        new("glTexParameterf", (nint)(TexParameterfFn)(&TexParameterf), Group.Objects),
        new("glTexParameteriv", (nint)(TexParameterivFn)(&TexParameteriv), Group.Objects),
        new("glTexParameterfv", (nint)(TexParameterfvFn)(&TexParameterfv), Group.Objects),
        new("glTextureParameteri", (nint)(TexParameteriFn)(&TextureParameteri), Group.Objects),
        new("glTextureParameterfv", (nint)(TexParameterfvFn)(&TextureParameterfv), Group.Objects),
        new("glScissor", (nint)(SetScissorFn)(&SetScissor), Group.Objects),
        new("glPolygonMode", (nint)(SetPolygonModeFn)(&SetPolygonMode), Group.Objects),
        new("glClearColor", (nint)(SetClearColorFn)(&SetClearColor), Group.Objects),
        new("glClearDepth", (nint)(SetClearDepthFn)(&SetClearDepth), Group.Objects),
        new("glGenVertexArrays", (nint)(MakeArraysFn)(&GenVertexArrays), Group.Objects),
        new("glCreateVertexArrays", (nint)(MakeArraysFn)(&CreateVertexArrays), Group.Objects),
        new("glBeginQuery", (nint)(BeginQueryFn)(&BeginQuery), Group.Objects),
        new("glEndQuery", (nint)(EndQueryFn)(&EndQuery), Group.Objects),
        new("glGetQueryObjectiv", (nint)(QueryObjectivFn)(&QueryObjectiv), Group.Objects),
        new("glGetQueryObjectuiv", (nint)(QueryObjectuivFn)(&QueryObjectuiv), Group.Objects),
        new("glLineWidth", (nint)(SetLineWidthFn)(&SetLineWidth), Group.Objects),
        new("glPixelStorei", (nint)(PixelStoreiFn)(&PixelStorei), Group.Objects),
        new("glReadBuffer", (nint)(ReadBufferFn)(&ReadBuffer), Group.Objects),
        new("glNamedFramebufferReadBuffer", (nint)(NamedReadBufferFn)(&NamedReadBuffer), Group.Objects),
        new("glLogicOp", (nint)(LogicOpFn)(&LogicOp), Group.Objects),
        new("glClearDepthf", (nint)(SetClearDepthfFn)(&SetClearDepthf), Group.Objects) // OpenTK's ClearDepth(float)
    ];

    private static bool Live(int n) =>
        Assert(n is >= 0 and < 64) && Assert(Original[ObjectsAt + n] != IntPtr.Zero) &&
        Index(ObjectsAt + n, _entries.Length);

    [UnmanagedCallersOnly]
    private static void BindBuffer(uint target, uint buffer)
    {
        var was = Bound(target);
        BindTo(target, buffer);
        if (target != PackBuffer && Bound(target) != was) Version++; // where reads go changes no draw, nor the same buffer
        if (Live(0) && Assert(Scene) && Assert(Version > 0)) ((BindBufferFn)Original[ObjectsAt])(target, buffer);
    }

    [UnmanagedCallersOnly]
    private static void BindBufferBase(uint target, uint index, uint buffer)
    {
        Version++;
        Indexed(target, index, (buffer, 0, 0));
        if (Live(1) && Assert(Scene) && Assert(Version > 0))
            ((BindBufferBaseFn)Original[ObjectsAt + 1])(target, index, buffer);
    }

    [UnmanagedCallersOnly]
    private static void BindBufferRange(uint target, uint index, uint buffer, nint offset, nint size)
    {
        Version++;
        Indexed(target, index, (buffer, offset, size));
        if (Live(2) && Assert(Scene) && Assert(Version > 0))
            ((BindBufferRangeFn)Original[ObjectsAt + 2])(target, index, buffer, offset, size);
    }

    [UnmanagedCallersOnly]
    private static void DeleteBuffers(int count, uint* buffers)
    {
        Version++;
        _ = Assert(count >= 0);
        // in slices: a mirror whose buffer went unheard of would stay until the name came back
        for (var at = 0; buffers != null && at < Math.Min(count, MaxDeleted); at += MaxNames)
            BuffersDeleted(new ReadOnlySpan<uint>(buffers + at, Math.Min(count - at, MaxNames)));
        if (Live(3) && Assert(Scene) && Assert(count >= 0)) ((DeleteBuffersFn)Original[ObjectsAt + 3])(count, buffers);
    }

    private static void BuffersDeleted(ReadOnlySpan<uint> gone)
    {
        if (!Assert(gone.Length <= MaxNames)) return;
        if (Writes is { } writes)
            try
            {
                writes.BuffersGone(gone); // a span: no state to hand Heard
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _ = Faulted(e);
            }
        foreach (var buffer in gone.Bounded(MaxNames))
        {
            if (buffer == 0) continue;
            if (Recording) _ = BufferSizes.Remove(buffer);
            LeanGone(buffer, false);
            for (var i = 0; i < MaxBindings; i++)
            {
                if (Uniforms[i].Buffer == buffer) Uniforms[i] = default;
                if (Storages[i].Buffer == buffer) Storages[i] = default;
            }

            if (_arrayBuffer == buffer) _arrayBuffer = 0;
            if (_looseElements == buffer) _looseElements = 0;
        }

        _ = Assert(Storages.Length == MaxBindings);
    }

    [UnmanagedCallersOnly]
    private static void NamedBufferStorage(uint buffer, nint size, nint data, uint flags)
    {
        Version++;
        var small = BufferLean(buffer, size, flags, UsageOf(flags)) == Leaning.Small;
        DataWritten(buffer, size, data, (flags & PersistentStorage) != 0);
        if (small && Assert(Original[TouchAt + 33] != IntPtr.Zero) && Index(TouchAt + 33, _entries.Length))
            ((delegate* unmanaged<uint, nint, nint, uint, void>)Original[TouchAt + 33])(buffer, LeanBufferKept, 0,
                UsageOf(flags)); // glNamedBufferData: mutable, its size can come back
        else if (!small && Live(4) && Assert(Scene) && Assert(size >= 0))
            ((NamedBufferStorageFn)Original[ObjectsAt + 4])(buffer, size, data, flags);
    }

    [UnmanagedCallersOnly]
    private static void BindVertexArray(uint array)
    {
        if (array != _vao) (_vao, _array, Version) = (array, null, Version + 1);
        if (Live(5) && Assert(Scene) && Assert(Version > 0)) ((BindVertexArrayFn)Original[ObjectsAt + 5])(array);
    }

    [UnmanagedCallersOnly]
    private static void AttribPointer(uint index, int size, uint type, byte normalized, int stride, nint pointer)
    {
        Pointer(index, (size, type, normalized != 0, false), stride, pointer);
        if (Live(6) && Assert(Scene) && Assert(stride >= 0))
            ((AttribPointerFn)Original[ObjectsAt + 6])(index, size, type, normalized, stride, pointer);
    }

    [UnmanagedCallersOnly]
    private static void AttribIPointer(uint index, int size, uint type, int stride, nint pointer)
    {
        Pointer(index, (size, type, false, true), stride, pointer);
        if (Live(7) && Assert(Scene) && Assert(stride >= 0))
            ((AttribIPointerFn)Original[ObjectsAt + 7])(index, size, type, stride, pointer);
    }

    [UnmanagedCallersOnly]
    private static void EnableAttrib(uint index)
    {
        Switched(BoundArray, index, true);
        if (Live(8) && Assert(Scene) && Assert(Version > 0)) ((AttribIndexFn)Original[ObjectsAt + 8])(index);
    }

    [UnmanagedCallersOnly]
    private static void DisableAttrib(uint index)
    {
        Switched(BoundArray, index, false);
        if (Live(9) && Assert(Scene) && Assert(Version > 0)) ((AttribIndexFn)Original[ObjectsAt + 9])(index);
    }

    [UnmanagedCallersOnly]
    private static void AttribDivisor(uint index, uint divisor)
    {
        if (BoundArray is { } array && index < MaxAttributes && Assert(array.Attributes.Length == MaxAttributes) &&
            array.Attributes[index].Divisor != divisor)
        {
            array.Attributes[index] = array.Attributes[index] with { Divisor = divisor };
            (array.Changes, Version) = (array.Changes + 1, Version + 1);
            _ = Assert(array.Attributes[index].Divisor == divisor);
        }

        if (Live(10) && Assert(Scene) && Assert(Version > 0))
            ((AttribDivisorFn)Original[ObjectsAt + 10])(index, divisor);
    }

    [UnmanagedCallersOnly]
    private static void DeleteVertexArrays(int count, uint* arrays)
    {
        Version++;
        if (arrays != null && Assert(count >= 0))
        {
            for (var i = 0; i < Math.Min(count, MaxDeleted); i++)
            {
                _ = Arrays.Remove(arrays[i]);
                if (arrays[i] == _vao) (_vao, _array) = (0, null); // deleting the bound array binds 0
            }

            _ = Assert(Arrays.Count <= MaxArrays);
        }

        if (Live(11) && Assert(Scene) && Assert(count >= 0))
            ((DeleteVertexArraysFn)Original[ObjectsAt + 11])(count, arrays);
    }

    [UnmanagedCallersOnly]
    private static void ArrayVertexBuffer(uint array, uint binding, uint buffer, nint offset, int stride)
    {
        if (ArrayOf(array) is { } vertices && binding < MaxAttributes && Assert(stride >= 0))
        {
            // binding i for attribute i, as Komet's own pool rebinding uses it
            var was = vertices.Attributes[binding];
            vertices.Attributes[binding] = was with
            {
                Buffer = buffer, Offset = offset, Stride = Effective(stride, was.Size, was.Type)
            };
            if (vertices.Attributes[binding] != was) (vertices.Changes, Version) = (vertices.Changes + 1, Version + 1);
            _ = Assert(vertices.Changes >= 0);
        }

        if (Live(12) && Assert(Scene) && Assert(stride >= 0))
            ((ArrayVertexBufferFn)Original[ObjectsAt + 12])(array, binding, buffer, offset, stride);
    }

    [UnmanagedCallersOnly]
    private static void ArrayElementBuffer(uint array, uint buffer)
    {
        Version++;
        if (ArrayOf(array) is { } vertices && Assert(vertices.Attributes.Length == MaxAttributes))
        {
            vertices.Elements = buffer;
            _ = Assert(vertices.Elements == buffer);
        }

        if (Live(13) && Assert(Scene) && Assert(Version > 0))
            ((ArrayElementBufferFn)Original[ObjectsAt + 13])(array, buffer);
    }

    [UnmanagedCallersOnly]
    private static void TexParameteri(uint target, uint name, int value)
    {
        Version++;
        Tuned(BoundName(target), name, value, Tracked(target));
        if (Live(14) && Assert(Scene) && Assert(name > 0))
            ((TexParameteriFn)Original[ObjectsAt + 14])(target, name, value);
    }

    [UnmanagedCallersOnly]
    private static void TexParameterf(uint target, uint name, float value)
    {
        Version++;
        Tuned(BoundName(target), name, value, Tracked(target));
        if (Live(15) && Assert(Scene) && Assert(name > 0))
            ((TexParameterfFn)Original[ObjectsAt + 15])(target, name, value);
    }

    [UnmanagedCallersOnly]
    private static void TexParameteriv(uint target, uint name, int* values)
    {
        Version++;
        // an integer border colour is normalized: int.MaxValue is 1
        if (values != null) Tuned(BoundName(target), name, name == 0x1004 ? values[0] / (float)int.MaxValue : values[0], Tracked(target));
        if (Recording && values != null && Tracked(target)) BorderInts(BoundName(target), name, values);
        if (Live(16) && Assert(Scene) && Assert(name > 0))
            ((TexParameterivFn)Original[ObjectsAt + 16])(target, name, values);
    }

    [UnmanagedCallersOnly]
    private static void TexParameterfv(uint target, uint name, float* values)
    {
        Version++;
        if (values != null) Tuned(BoundName(target), name, values[0], Tracked(target));
        if (Recording && values != null && Tracked(target)) BorderFloats(BoundName(target), name, values);
        if (Live(17) && Assert(Scene) && Assert(name > 0))
            ((TexParameterfvFn)Original[ObjectsAt + 17])(target, name, values);
    }

    [UnmanagedCallersOnly]
    private static void TextureParameteri(uint texture, uint name, int value)
    {
        Version++;
        Tuned(texture, name, value);
        if (Live(18) && Assert(Scene) && Assert(name > 0))
            ((TexParameteriFn)Original[ObjectsAt + 18])(texture, name, value);
    }

    [UnmanagedCallersOnly]
    private static void TextureParameterfv(uint texture, uint name, float* values)
    {
        Version++;
        if (values != null) Tuned(texture, name, values[0]);
        if (Recording && values != null) BorderFloats(texture, name, values);
        if (Live(19) && Assert(Scene) && Assert(name > 0))
            ((TexParameterfvFn)Original[ObjectsAt + 19])(texture, name, values);
    }

    [UnmanagedCallersOnly]
    private static void SetScissor(int x, int y, int width, int height)
    {
        Version++;
        _scissor = (x, y, width, height);
        if (Live(20) && Assert(Scene) && Assert(width >= 0 && height >= 0))
            ((SetScissorFn)Original[ObjectsAt + 20])(x, y, width, height);
    }

    [UnmanagedCallersOnly]
    private static void SetPolygonMode(uint face, uint mode)
    {
        Version++;
        _polygonMode = mode;
        if (Live(21) && Assert(Scene) && Assert(mode > 0)) ((SetPolygonModeFn)Original[ObjectsAt + 21])(face, mode);
    }

    [UnmanagedCallersOnly]
    private static void SetLineWidth(float width)
    {
        Version++;
        if (width > 0 && width <= _widest) _lineWidth = width; // else OpenGL raises GL_INVALID_VALUE and keeps it
        if (Live(30) && Assert(Scene) && Finite(width)) ((SetLineWidthFn)Original[ObjectsAt + 30])(width);
    }

    [UnmanagedCallersOnly]
    private static void PixelStorei(uint name, int value)
    {
        _unpack = name switch
        {
            0x0CF2 => _unpack with { RowLength = value }, 0x0CF3 => _unpack with { SkipRows = value },
            0x0CF4 => _unpack with { SkipPixels = value }, 0x0CF5 => _unpack with { Alignment = value }, _ => _unpack
        };
        _pack = name switch
        {
            0x0D02 => _pack with { RowLength = value }, 0x0D03 => _pack with { SkipRows = value },
            0x0D04 => _pack with { SkipPixels = value }, 0x0D05 => _pack with { Alignment = value }, _ => _pack
        };
        if (Live(31) && Assert(Scene) && Assert(name > 0)) ((PixelStoreiFn)Original[ObjectsAt + 31])(name, value);
    }

    [UnmanagedCallersOnly]
    private static void LogicOp(uint operation)
    {
        Version++;
        if (operation is >= 0x1500 and <= 0x150F) _logicOp = operation;
        if (Live(34) && Assert(Scene) && Assert(operation > 0)) ((LogicOpFn)Original[ObjectsAt + 34])(operation);
    }

    // The window's colour buffers read from the stand-in's attachment
    [UnmanagedCallersOnly]
    private static void ReadBuffer(uint mode)
    {
        Version++;
        if (Window != 0 && _readFramebuffer == Window) mode = Windowed(mode);
        ReadSet(_readFramebuffer, mode);
        if (Live(32) && Assert(Scene) && Assert(mode is 0 or >= GlFrontLeft)) ((ReadBufferFn)Original[ObjectsAt + 32])(mode);
    }

    [UnmanagedCallersOnly]
    private static void NamedReadBuffer(uint framebuffer, uint mode)
    {
        Version++;
        if (framebuffer == 0 && Window != 0) (framebuffer, mode) = (Window, Windowed(mode));
        ReadSet(framebuffer, mode);
        if (Live(33) && Assert(Scene) && Assert(mode is 0 or >= GlFrontLeft))
            ((NamedReadBufferFn)Original[ObjectsAt + 33])(framebuffer, mode);
    }

    [UnmanagedCallersOnly]
    private static void SetClearColor(float r, float g, float b, float a)
    {
        Version++;
        _clearColor = (r, g, b, a);
        if (Live(22) && Assert(Scene) && Finite(r)) ((SetClearColorFn)Original[ObjectsAt + 22])(r, g, b, a);
    }

    [UnmanagedCallersOnly]
    private static void SetClearDepthf(float depth)
    {
        Version++;
        _clearDepth = depth;
        if (Live(35) && Assert(Scene) && Finite(depth)) ((SetClearDepthfFn)Original[ObjectsAt + 35])(depth);
    }

    [UnmanagedCallersOnly]
    private static void SetClearDepth(double depth)
    {
        Version++;
        _clearDepth = depth;
        if (Live(23) && Assert(Scene) && Assert(!double.IsNaN(depth)))
            ((SetClearDepthFn)Original[ObjectsAt + 23])(depth);
    }

    [UnmanagedCallersOnly]
    private static void GenVertexArrays(int count, uint* arrays)
    {
        if (Live(24) && Assert(Scene) && Assert(count >= 0)) ((MakeArraysFn)Original[ObjectsAt + 24])(count, arrays);
        Version++;
        ArraysMade(count, arrays);
    }

    [UnmanagedCallersOnly]
    private static void CreateVertexArrays(int count, uint* arrays)
    {
        if (Live(25) && Assert(Scene) && Assert(count >= 0)) ((MakeArraysFn)Original[ObjectsAt + 25])(count, arrays);
        Version++;
        ArraysMade(count, arrays);
    }

    [UnmanagedCallersOnly]
    private static void BeginQuery(uint target, uint id)
    {
        Version++;
        if (_quiet == 0) Queried(target, id);
        if (Live(26) && Assert(Scene) && Assert(id > 0)) ((BeginQueryFn)Original[ObjectsAt + 26])(target, id);
    }

    [UnmanagedCallersOnly]
    private static void EndQuery(uint target)
    {
        Version++;
        if (_quiet == 0) Queried(target, 0);
        if (Live(27) && Assert(Scene) && Assert(target > 0)) ((EndQueryFn)Original[ObjectsAt + 27])(target);
    }

    [UnmanagedCallersOnly]
    private static void QueryObjectiv(uint id, uint name, int* value)
    {
        if (value != null && Answered(id, name) is { } answer && Assert(answer >= 0))
        {
            *value = (int)Math.Min(answer, int.MaxValue);
            return;
        }

        if (Live(28) && Assert(Scene) && Assert(name > 0)) ((QueryObjectivFn)Original[ObjectsAt + 28])(id, name, value);
    }

    [UnmanagedCallersOnly]
    private static void QueryObjectuiv(uint id, uint name, uint* value)
    {
        if (value != null && Answered(id, name) is { } answer && Assert(answer >= 0))
        {
            *value = (uint)Math.Min(answer, uint.MaxValue);
            return;
        }

        if (Live(29) && Assert(Scene) && Assert(name > 0))
            ((QueryObjectuivFn)Original[ObjectsAt + 29])(id, name, value);
    }

    private static void Indexed(uint target, uint index, (uint Buffer, long Offset, long Size) binding)
    {
        if (index >= MaxBindings || !Assert(Uniforms.Length == MaxBindings) || !Assert(binding.Offset >= 0)) return;
        if (target == UniformBuffer) Uniforms[index] = binding;
        else if (target == StorageBuffer) Storages[index] = binding;
        BindTo(target, binding.Buffer); // the indexed binds also bind the generic point
    }

    // The attribute reads the array buffer bound now, from the pointer as an offset
    private static void Pointer(uint index, (int Size, uint Type, bool Normalized, bool Integer) format, int stride,
        nint pointer)
    {
        if (BoundArray is not { } array || index >= MaxAttributes || !Assert(stride >= 0)) return;
        var was = array.Attributes[index];
        array.Attributes[index] = new Attribute(was.Enabled, _arrayBuffer, format.Size, format.Type, format.Normalized,
            format.Integer, Effective(stride, format.Size, format.Type), pointer, was.Divisor);
        if (array.Attributes[index] != was) (array.Changes, Version) = (array.Changes + 1, Version + 1);
        _ = Assert(array.Changes >= 0);
    }

    private static void Switched(VertexArray? array, uint index, bool on)
    {
        if (array is null || index >= MaxAttributes || !Assert(array.Attributes.Length == MaxAttributes)) return;
        if (array.Attributes[index].Enabled == on) return; // the same again: no layout change
        array.Attributes[index] = array.Attributes[index] with { Enabled = on };
        (array.Changes, Version) = (array.Changes + 1, Version + 1);
        _ = Assert(array.Attributes[index].Enabled == on);
    }

    private static void ArraysMade(int count, uint* arrays)
    {
        if (arrays == null || !Assert(count >= 0) || count > MaxNames) return;
        for (var i = 0; i < Math.Min(count, MaxNames); i++)
            if (arrays[i] != 0 && Arrays.Count < MaxArrays)
                Arrays[arrays[i]] = new VertexArray();
        _ = Assert(Arrays.Count <= MaxArrays);
    }
}
