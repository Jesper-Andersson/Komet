using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics.OpenGL;
using unsafe CreateShaderFn = delegate* unmanaged<uint, uint>;
using unsafe ShaderSourceFn = delegate* unmanaged<uint, int, byte**, int*, void>;
using unsafe AttachShaderFn = delegate* unmanaged<uint, uint, void>;
using unsafe BindAttribFn = delegate* unmanaged<uint, uint, byte*, void>;
using unsafe LinkProgramFn = delegate* unmanaged<uint, void>;
using unsafe UniformLocationFn = delegate* unmanaged<uint, byte*, int>;
using unsafe BlockIndexFn = delegate* unmanaged<uint, byte*, uint>;
using unsafe BlockBindingFn = delegate* unmanaged<uint, uint, uint, void>;
using unsafe CreateProgramFn = delegate* unmanaged<uint>;
using unsafe MakeNamesFn = delegate* unmanaged<int, uint*, void>;
using unsafe CreateTexturesFn = delegate* unmanaged<uint, int, uint*, void>;
using unsafe FramebufferTexture2DFn = delegate* unmanaged<uint, uint, uint, uint, int, void>;
using unsafe FramebufferTextureLayerFn = delegate* unmanaged<uint, uint, uint, int, int, void>;
using unsafe FramebufferTextureFn = delegate* unmanaged<uint, uint, uint, int, void>;
using unsafe FramebufferRenderbufferFn = delegate* unmanaged<uint, uint, uint, uint, void>;
using unsafe CheckStatusFn = delegate* unmanaged<uint, uint>;
using unsafe SamplerParameteriFn = delegate* unmanaged<uint, uint, int, void>;
using unsafe SamplerParameterfFn = delegate* unmanaged<uint, uint, float, void>;
using unsafe TextureStorage2DFn = delegate* unmanaged<uint, int, uint, int, int, void>;
using unsafe TextureStorage3DFn = delegate* unmanaged<uint, int, uint, int, int, int, void>;
using unsafe TextureParameterfFn = delegate* unmanaged<uint, uint, float, void>;
using unsafe TextureParameterivFn = delegate* unmanaged<uint, uint, int*, void>;
using Attachments = Komet.Vulkan.GlFramebuffer;

namespace Komet.Vulkan;

// The record's own entries (VulkanRenderer.OnlyOn): program, framebuffer, sampler and texture creation, heard after the
// driver's call (unknown objects are asked from GL then). Entries OpenTK's table lacks are skipped, not a failed tap.
// glCheckFramebufferStatus is answered from the record for framebuffers seen made.
internal static unsafe partial class GlTap
{
    private const int RecordsAt = ObjectsAt + ObjectCount, RecordCount = 53, MaxShaders = 8, MaxName = 256;
    private const uint GlComplete = 0x8CD5, GlDepthAttachment = 0x8D00, GlDepthStencilAttachment = 0x821A;
    private const uint VertexStage = 0x8B31, FragmentStage = 0x8B30;

    private static Entry[] RecordEntries() =>
    [
        new("glCreateShader", (nint)(CreateShaderFn)(&CreateShader), Group.Records),
        new("glShaderSource", (nint)(ShaderSourceFn)(&ShaderSource), Group.Records),
        new("glAttachShader", (nint)(AttachShaderFn)(&AttachShader), Group.Records),
        new("glDetachShader", (nint)(AttachShaderFn)(&DetachShader), Group.Records),
        new("glBindAttribLocation", (nint)(BindAttribFn)(&BindAttribLocation), Group.Records),
        new("glLinkProgram", (nint)(LinkProgramFn)(&LinkProgram), Group.Records),
        new("glGetUniformLocation", (nint)(UniformLocationFn)(&GetUniformLocation), Group.Records),
        new("glGetUniformBlockIndex", (nint)(BlockIndexFn)(&GetUniformBlockIndex), Group.Records),
        new("glUniformBlockBinding", (nint)(BlockBindingFn)(&UniformBlockBinding), Group.Records),
        new("glCreateProgram", (nint)(CreateProgramFn)(&CreateProgram), Group.Records),
        new("glGenFramebuffers", (nint)(MakeNamesFn)(&GenFramebuffers), Group.Records),
        new("glCreateFramebuffers", (nint)(MakeNamesFn)(&CreateFramebuffers), Group.Records),
        new("glFramebufferTexture2D", (nint)(FramebufferTexture2DFn)(&FramebufferTexture2D), Group.Records),
        new("glFramebufferTextureLayer", (nint)(FramebufferTextureLayerFn)(&FramebufferTextureLayer), Group.Records),
        new("glFramebufferTexture", (nint)(FramebufferTextureFn)(&FramebufferTexture), Group.Records),
        new("glNamedFramebufferTexture", (nint)(FramebufferTextureFn)(&NamedFramebufferTexture), Group.Records),
        new("glNamedFramebufferTextureLayer", (nint)(FramebufferTextureLayerFn)(&NamedFramebufferTextureLayer),
            Group.Records),
        new("glFramebufferRenderbuffer", (nint)(FramebufferRenderbufferFn)(&FramebufferRenderbuffer), Group.Records),
        new("glNamedFramebufferRenderbuffer", (nint)(FramebufferRenderbufferFn)(&NamedFramebufferRenderbuffer),
            Group.Records),
        new("glCheckFramebufferStatus", (nint)(CheckStatusFn)(&CheckFramebufferStatus), Group.Records),
        new("glCheckFramebufferStatusEXT", (nint)(CheckStatusFn)(&CheckFramebufferStatusExt), Group.Records),
        new("glGenSamplers", (nint)(MakeNamesFn)(&GenSamplers), Group.Records),
        new("glCreateSamplers", (nint)(MakeNamesFn)(&CreateSamplers), Group.Records),
        new("glSamplerParameteri", (nint)(SamplerParameteriFn)(&SamplerParameteri), Group.Records),
        new("glSamplerParameterf", (nint)(SamplerParameterfFn)(&SamplerParameterf), Group.Records),
        new("glGenTextures", (nint)(MakeNamesFn)(&GenTextures), Group.Records),
        new("glCreateTextures", (nint)(CreateTexturesFn)(&CreateTextures), Group.Records),
        new("glTextureStorage2D", (nint)(TextureStorage2DFn)(&TextureStorage2D), Group.Records),
        new("glTextureStorage3D", (nint)(TextureStorage3DFn)(&TextureStorage3D), Group.Records),
        new("glTextureParameterf", (nint)(TextureParameterfFn)(&TextureParameterf), Group.Records),
        new("glTextureParameteriv", (nint)(TextureParameterivFn)(&TextureParameteriv), Group.Records),
        .. SpecEntries(),
        .. StencilEntries()
    ];

    private static bool Kept(int n) =>
        Assert(n is >= 0 and < RecordCount) && Original[RecordsAt + n] != IntPtr.Zero &&
        Index(RecordsAt + n, _entries.Length);

    [UnmanagedCallersOnly]
    private static uint CreateShader(uint type)
    {
        var shader = Kept(0) && Assert(Recording) && Assert(type > 0) ? ((CreateShaderFn)Original[RecordsAt])(type) : 0;
        if (shader != 0) Heard(static s => ShaderMade(s.shader, s.type), (shader, type));
        return shader;
    }

    [UnmanagedCallersOnly]
    private static void ShaderSource(uint shader, int count, byte** strings, int* lengths)
    {
        if (Kept(1) && Assert(Recording) && Assert(count >= 0))
            ((ShaderSourceFn)Original[RecordsAt + 1])(shader, count, strings, lengths);
        if (strings != null && count > 0)
            Heard(static s => SourceSet(s.shader, s.count, (byte**)s.Item3, (int*)s.Item4),
                (shader, count, (nint)strings, (nint)lengths));
    }

    [UnmanagedCallersOnly]
    private static void AttachShader(uint program, uint shader)
    {
        if (Kept(2) && Assert(Recording) && Assert(program > 0))
            ((AttachShaderFn)Original[RecordsAt + 2])(program, shader);
        Heard(static s => ShaderAttached(s.program, s.shader, true), (program, shader));
    }

    [UnmanagedCallersOnly]
    private static void DetachShader(uint program, uint shader)
    {
        if (Kept(3) && Assert(Recording) && Assert(program > 0))
            ((AttachShaderFn)Original[RecordsAt + 3])(program, shader);
        Heard(static s => ShaderAttached(s.program, s.shader, false), (program, shader));
    }

    [UnmanagedCallersOnly]
    private static void BindAttribLocation(uint program, uint index, byte* name)
    {
        if (Kept(4) && Assert(Recording) && Assert(index < 4096))
            ((BindAttribFn)Original[RecordsAt + 4])(program, index, name);
        if (name != null)
            Heard(static s => AttributeBound(s.program, s.index, (byte*)s.Item3),
                (program, index, (nint)name));
    }

    [UnmanagedCallersOnly]
    private static void LinkProgram(uint program)
    {
        if (Kept(5) && Assert(Recording) && Assert(program > 0)) ((LinkProgramFn)Original[RecordsAt + 5])(program);
        Heard(ProgramLinked, program);
    }

    [UnmanagedCallersOnly]
    private static int GetUniformLocation(uint program, byte* name)
    {
        var location = Kept(6) && Assert(Recording) && Assert(program > 0)
            ? ((UniformLocationFn)Original[RecordsAt + 6])(program, name)
            : -1;
        if (name != null)
            Heard(static s => LocationAsked(s.program, (byte*)s.Item2, s.location),
                (program, (nint)name, location));
        return location;
    }

    [UnmanagedCallersOnly]
    private static uint GetUniformBlockIndex(uint program, byte* name)
    {
        var index = Kept(7) && Assert(Recording) && Assert(program > 0)
            ? ((BlockIndexFn)Original[RecordsAt + 7])(program, name)
            : InvalidIndex;
        if (name != null)
            Heard(static s => BlockAsked(s.program, (byte*)s.Item2, s.index),
                (program, (nint)name, index));
        return index;
    }

    [UnmanagedCallersOnly]
    private static void UniformBlockBinding(uint program, uint index, uint binding)
    {
        if (Kept(8) && Assert(Recording) && Assert(program > 0))
            ((BlockBindingFn)Original[RecordsAt + 8])(program, index, binding);
        Heard(static s => BlockBound(s.program, s.index, s.binding), (program, index, binding));
    }

    [UnmanagedCallersOnly]
    private static uint CreateProgram()
    {
        var program = Kept(9) && Assert(Recording) && Assert(_entries.Length > RecordsAt)
            ? ((CreateProgramFn)Original[RecordsAt + 9])()
            : 0;
        if (program != 0) Heard(ProgramMade, program);
        return program;
    }

    [UnmanagedCallersOnly]
    private static void GenFramebuffers(int count, uint* names)
    {
        if (Kept(10) && Assert(Recording) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 10])(count, names);
        Heard(static s => FramebuffersMade(s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void CreateFramebuffers(int count, uint* names)
    {
        if (Kept(11) && Assert(Recording) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 11])(count, names);
        Heard(static s => FramebuffersMade(s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void FramebufferTexture2D(uint target, uint attachment, uint textureTarget, uint texture, int level)
    {
        if (Kept(12) && Assert(Recording) && Assert(level >= 0))
            ((FramebufferTexture2DFn)Original[RecordsAt + 12])(target, attachment, textureTarget, texture, level);
        Heard(static s => Attach(FramebufferAt(s.target), s.attachment,
            new Attachments.Attached((int)s.texture, s.level, 0, false)), (target, attachment, texture, level));
    }

    [UnmanagedCallersOnly]
    private static void FramebufferTextureLayer(uint target, uint attachment, uint texture, int level, int layer)
    {
        if (Kept(13) && Assert(Recording) && Assert(level >= 0))
            ((FramebufferTextureLayerFn)Original[RecordsAt + 13])(target, attachment, texture, level, layer);
        Heard(static s => Attach(FramebufferAt(s.target), s.attachment,
            new Attachments.Attached((int)s.texture, s.level, s.layer, false)),
            (target, attachment, texture, level, layer));
    }

    [UnmanagedCallersOnly]
    private static void FramebufferTexture(uint target, uint attachment, uint texture, int level)
    {
        if (Kept(14) && Assert(Recording) && Assert(level >= 0))
            ((FramebufferTextureFn)Original[RecordsAt + 14])(target, attachment, texture, level);
        Heard(static s => Attach(FramebufferAt(s.target), s.attachment, WholeLayers(s.texture, s.level)),
            (target, attachment, texture, level));
    }

    [UnmanagedCallersOnly]
    private static void NamedFramebufferTexture(uint framebuffer, uint attachment, uint texture, int level)
    {
        if (Kept(15) && Assert(Recording) && Assert(level >= 0))
            ((FramebufferTextureFn)Original[RecordsAt + 15])(framebuffer, attachment, texture, level);
        Heard(static s => Attach(s.framebuffer, s.attachment, WholeLayers(s.texture, s.level)),
            (framebuffer, attachment, texture, level));
    }

    [UnmanagedCallersOnly]
    private static void NamedFramebufferTextureLayer(uint framebuffer, uint attachment, uint texture, int level,
        int layer)
    {
        if (Kept(16) && Assert(Recording) && Assert(level >= 0))
            ((FramebufferTextureLayerFn)Original[RecordsAt + 16])(framebuffer, attachment, texture, level, layer);
        Heard(static s => Attach(s.framebuffer, s.attachment, new Attachments.Attached((int)s.texture, s.level, s.layer,
            false)), (framebuffer, attachment, texture, level, layer));
    }

    [UnmanagedCallersOnly]
    private static void FramebufferRenderbuffer(uint target, uint attachment, uint renderbufferTarget,
        uint renderbuffer)
    {
        if (Kept(17) && Assert(Recording) && Assert(attachment > 0))
            ((FramebufferRenderbufferFn)Original[RecordsAt + 17])(target, attachment, renderbufferTarget, renderbuffer);
        Heard(static s => Attach(FramebufferAt(s.target), s.attachment, default, s.renderbuffer),
            (target, attachment, renderbuffer));
    }

    [UnmanagedCallersOnly]
    private static void NamedFramebufferRenderbuffer(uint framebuffer, uint attachment, uint renderbufferTarget,
        uint renderbuffer)
    {
        if (Kept(18) && Assert(Recording) && Assert(attachment > 0))
        {
            var original = (FramebufferRenderbufferFn)Original[RecordsAt + 18];
            original(framebuffer, attachment, renderbufferTarget, renderbuffer);
        }

        Heard(static s => Attach(s.framebuffer, s.attachment, default, s.renderbuffer),
            (framebuffer, attachment, renderbuffer));
    }

    [UnmanagedCallersOnly]
    private static uint CheckFramebufferStatus(uint target)
    {
        if (Asked(static t => Status(t), target, null) is { } status && Assert(status == GlComplete)) return status;
        _ = Assert(Recording);
        return Kept(19) ? ((CheckStatusFn)Original[RecordsAt + 19])(target) : 0;
    }

    // The engine checks its framebuffers through GL.Ext
    [UnmanagedCallersOnly]
    private static uint CheckFramebufferStatusExt(uint target)
    {
        if (Asked(static t => Status(t), target, null) is { } status && Assert(status == GlComplete)) return status;
        _ = Assert(Recording);
        return Kept(20) ? ((CheckStatusFn)Original[RecordsAt + 20])(target) : 0;
    }

    [UnmanagedCallersOnly]
    private static void GenSamplers(int count, uint* names)
    {
        if (Kept(21) && Assert(Recording) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 21])(count, names);
        Heard(static s => SamplersMade(s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void CreateSamplers(int count, uint* names)
    {
        if (Kept(22) && Assert(Recording) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 22])(count, names);
        Heard(static s => SamplersMade(s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void SamplerParameteri(uint sampler, uint name, int value)
    {
        if (Kept(23) && Assert(Recording) && Assert(name > 0))
            ((SamplerParameteriFn)Original[RecordsAt + 23])(sampler, name, value);
        Heard(static s => SamplerTuned(s.sampler, s.name, s.value), (sampler, name, value));
    }

    [UnmanagedCallersOnly]
    private static void SamplerParameterf(uint sampler, uint name, float value)
    {
        if (Kept(24) && Assert(Recording) && Assert(name > 0))
            ((SamplerParameterfFn)Original[RecordsAt + 24])(sampler, name, value);
        if (Finite(value)) Heard(static s => SamplerTuned(s.sampler, s.name, (int)s.value), (sampler, name, value));
    }

    [UnmanagedCallersOnly]
    private static void GenTextures(int count, uint* names)
    {
        if (Kept(25) && Assert(Recording) && Assert(count >= 0)) ((MakeNamesFn)Original[RecordsAt + 25])(count, names);
        Heard(static s => TexturesMade(0, s.count, (uint*)s.Item2), (count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void CreateTextures(uint target, int count, uint* names)
    {
        if (Kept(26) && Assert(Recording) && Assert(count >= 0))
            ((CreateTexturesFn)Original[RecordsAt + 26])(target, count, names);
        Heard(static s => TexturesMade(s.target, s.count, (uint*)s.Item3), (target, count, (nint)names));
    }

    [UnmanagedCallersOnly]
    private static void TextureStorage2D(uint texture, int levels, uint format, int width, int height)
    {
        LeanGone(texture, true); // immutable storage is never lean: OpenGL could not be given its size back
        if (Kept(27) && Assert(Recording) && Assert(levels >= 0))
            ((TextureStorage2DFn)Original[RecordsAt + 27])(texture, levels, format, width, height);
        Heard(static s => StorageMade(s.texture, (s.levels, ((int)s.format, s.width, s.height, 1))),
            (texture, levels, format, width, height));
    }

    [UnmanagedCallersOnly]
    private static void TextureStorage3D(uint texture, int levels, uint format, int width, int height, int depth)
    {
        LeanGone(texture, true);
        if (Kept(28) && Assert(Recording) && Assert(levels >= 0))
            ((TextureStorage3DFn)Original[RecordsAt + 28])(texture, levels, format, width, height, depth);
        Heard(static s => StorageMade(s.texture, (s.levels, ((int)s.format, s.width, s.height, s.depth))),
            (texture, levels, format, width, height, depth));
    }

    // glTextureStorage*: the record's, then Writes' (Vulkan-only) for a texture of a target the units follow
    private static void StorageMade(uint texture, (int Levels, (int Format, int Width, int Height, int Depth) Spec) made)
    {
        if (!Assert(made.Levels >= 0) || !Assert(Recording)) return;
        Specified(texture, 0, (0, made.Levels), made.Spec, 0);
        var target = TextureRecords.TryGetValue(texture, out var t) ? t.Target : 0;
        if (target != 0 && Tracked(target)) Imaged(texture, target, (0, Math.Max(made.Levels, 1)), made.Spec, default, 0);
        else UntrackedCalls++;
    }

    [UnmanagedCallersOnly]
    private static void TextureParameterf(uint texture, uint name, float value)
    {
        Version++;
        Tuned(texture, name, value);
        if (Kept(29) && Assert(Recording) && Assert(name > 0))
            ((TextureParameterfFn)Original[RecordsAt + 29])(texture, name, value);
    }

    [UnmanagedCallersOnly]
    private static void TextureParameteriv(uint texture, uint name, int* values)
    {
        Version++;
        if (values != null) Tuned(texture, name, name == GlBorderColor ? values[0] / (float)int.MaxValue : values[0]);
        if (values != null) BorderInts(texture, name, values);
        if (Kept(30) && Assert(Recording) && Assert(name > 0))
            ((TextureParameterivFn)Original[RecordsAt + 30])(texture, name, values);
    }

    private static void BorderFloats(uint texture, uint name, float* values)
    {
        if (name == GlBorderColor && values != null && Assert(Recording) && Assert(texture != uint.MaxValue))
            Heard(static s => Bordered(s.texture, (float*)s.Item2, false), (texture, (nint)values));
    }

    private static void BorderInts(uint texture, uint name, int* values)
    {
        if (name != GlBorderColor || values == null || !Assert(Recording) || !Assert(texture != uint.MaxValue)) return;
        var floats = stackalloc float[4];
        for (var i = 0; i < 4; i++) floats[i] = values[i];
        Heard(static s => Bordered(s.texture, (float*)s.Item2, true), (texture, (nint)floats));
    }

    private static uint FramebufferAt(uint target) =>
        Assert(target > 0) && Assert(Recording) && target == GlReadFramebuffer ? _readFramebuffer : _framebuffer;

    // glFramebufferTexture: an array, cube map or 3D texture is attached with every layer
    private static Attachments.Attached WholeLayers(uint texture, int level)
    {
        var target = texture == 0 ? 0 : Texture(texture)?.Target ?? 0;
        var layered = target is Texture2DArray or TextureCube or 0x806F or 0x9009 or 0x8C18 or 0x9102;
        _ = Assert(level >= 0) && Assert(target != uint.MaxValue);
        return new Attachments.Attached((int)texture, level, 0, layered);
    }

    private static int SlotOf(uint attachment) => !Assert(attachment > 0) || !Assert(Recording) ? -1 : attachment switch
    {
        >= GlColor0 and < GlColor0 + Attachments.Colors => (int)(attachment - GlColor0),
        GlDepthAttachment or GlDepthStencilAttachment => Attachments.Depth,
        _ => -1
    };

    private static void Attach(uint framebuffer, uint attachment, Attachments.Attached attached,
        uint renderbuffer = 0)
    {
        if (!Recording || framebuffer == 0 || SlotOf(attachment) is not (>= 0 and var slot)) return;
        if (Framebuffer(framebuffer) is not { } f || !Index(slot, f.Attached.Length)) return;
        if (renderbuffer is > 0 and < RenderbufferBase) // as the texture it stands as (one level, one layer)
            attached = new Attachments.Attached((int)RenderbufferName(renderbuffer), 0, 0, false);
        f.Attached[slot] = attached;
        f.Renderbuffers = renderbuffer != 0 ? f.Renderbuffers | (1 << slot) : f.Renderbuffers & ~(1 << slot);
        f.Renderbuffer[slot] = renderbuffer;
        _ = Assert(f.Attached[slot] == attached);
        Reattached(framebuffer);
    }

    // Complete while every attachment is a texture level 0 the record knows, of a format its attachment point renders
    private static uint? Status(uint target)
    {
        var framebuffer = FramebufferAt(target);
        if (!Recording || framebuffer == 0 || framebuffer == Window ||
            !FramebufferRecords.TryGetValue(framebuffer, out var f) || !f.Fresh || !Assert(target > 0)) return null;
        var any = false;
        for (var i = 0; i <= Attachments.Colors; i++)
        {
            var a = f.Attached[i];
            if ((f.Renderbuffers & (1 << i)) != 0)
            {
                if (!RenderbufferFits(f.Renderbuffer[i], i == Attachments.Depth)) return null;
                any = true;
                continue;
            }

            if (a.Texture == 0) continue;
            if (a.Level != 0 || !TextureRecords.TryGetValue((uint)a.Texture, out var t) || t.Width <= 0 ||
                (t.Levels & 1) == 0 || Unsized(t.Format) || DepthFormat(t.Format) != (i == Attachments.Depth))
                return null;
            any = true;
        }

        _ = Assert(f.Attached.Length == Attachments.Colors + 1);
        return any ? GlComplete : null;
    }

    private static bool DepthFormat(int format) =>
        Assert(format >= 0) && Assert(Recording) && format is 0x81A5 or 0x81A6 or 0x81A7 or 0x8CAC or 0x88F0 or 0x8CAD;

    private static FramebufferRecord? Framebuffer(uint framebuffer)
    {
        if (framebuffer == 0 || !Recording || !Assert(FramebufferRecords.Count <= MaxRecords)) return null;
        if (FramebufferRecords.TryGetValue(framebuffer, out var known)) return known;
        if (FramebufferRecords.Count >= MaxRecords) FramebufferRecords.Clear();
        var made = new FramebufferRecord();
        Attachments.Asked((int)framebuffer).CopyTo(made.Attached, 0);
        RecordMisses++;
        return FramebufferRecords[framebuffer] = made;
    }

    private static void FramebuffersMade(int count, uint* names)
    {
        if (!Recording || names == null || !Assert(count >= 0) || count > MaxNames) return;
        if (FramebufferRecords.Count + count > MaxRecords) FramebufferRecords.Clear();
        for (var i = 0; i < Math.Min(count, MaxNames); i++)
            if (names[i] != 0)
                FramebufferRecords[names[i]] = new FramebufferRecord { Fresh = true };
        _ = Assert(FramebufferRecords.Count <= MaxRecords + MaxNames);
    }

    // The framebuffer's attachments as the record knows them (a copy), asked from OpenGL once for one made before it
    public static Attachments.Attached[]? AttachmentsOf(int framebuffer) =>
        framebuffer > 0 && Recording && Framebuffer((uint)framebuffer) is { } f && Assert(f.Renderbuffers >= 0) &&
        Assert(f.Attached.Length == Attachments.Colors + 1)
            ? [.. f.Attached]
            : null;

    // Seen made, with its draw or read buffers never set: OpenGL's defaults
    private static bool Defaulted(uint framebuffer) =>
        Recording && Assert(framebuffer > 0) && FramebufferRecords.TryGetValue(framebuffer, out var f) &&
        Assert(f.Attached.Length == Attachments.Colors + 1) && f.Fresh;

    private static void SamplersMade(int count, uint* names)
    {
        if (!Recording || names == null || !Assert(count >= 0) || count > MaxNames) return;
        if (SamplerRecords.Count + count > MaxNamed) SamplerRecords.Clear();
        for (var i = 0; i < Math.Min(count, MaxNames); i++)
            if (names[i] != 0)
                SamplerRecords[names[i]] = (0x2702, 0x2601, 0x2901, 0);
        _ = Assert(SamplerRecords.Count <= MaxNamed + MaxNames);
    }

    private static void SamplerTuned(uint sampler, uint name, int value)
    {
        if (!Recording || !SamplerRecords.TryGetValue(sampler, out var s) || !Assert(name > 0)) return;
        SamplerRecords[sampler] = name switch
        {
            GlMinFilter => s with { Min = value }, GlMagFilter => s with { Mag = value },
            GlWrapS => s with { Wrap = value }, GlCompareMode => s with { Compare = value }, _ => s
        };
        _ = Assert(SamplerRecords.Count <= MaxNamed + MaxNames);
    }

    // A sampler object seen made, as its parameters were set since; null for one the record does not know
    public static Samplers.State? SamplerOf(int sampler) =>
        Recording && sampler > 0 && SamplerRecords.TryGetValue((uint)sampler, out var s) && Assert(s.Min >= 0) &&
        Assert(s.Mag >= 0)
            ? Samplers.FromGl(s.Min, s.Mag, s.Wrap, s.Compare != 0)
            : null;

    private static void ShaderMade(uint shader, uint type)
    {
        if (!Recording || !Assert(shader > 0) || !Assert(type > 0)) return;
        if (ShaderRecords.Count >= MaxProgramRecords) ShaderRecords.Clear();
        ShaderRecords[shader] = new ShaderRecord(type);
    }

    private static void SourceSet(uint shader, int count, byte** strings, int* lengths)
    {
        if (!Recording || !ShaderRecords.TryGetValue(shader, out var s) || !Assert(count is > 0 and <= 64)) return;
        var source = new StringBuilder();
        for (var i = 0; i < Math.Min(count, 64); i++)
        {
            var at = (nint)strings[i];
            if (at == 0) continue;
            _ = source.Append(lengths == null || lengths[i] < 0
                ? Marshal.PtrToStringUTF8(at)
                : Marshal.PtrToStringUTF8(at, lengths[i]));
        }

        s.Source = source.ToString();
        _ = Assert(source.Length == s.Source.Length);
    }

    private static void ProgramMade(uint program)
    {
        if (!Recording || !Assert(program > 0)) return;
        if (ProgramRecords.Count >= MaxProgramRecords) ProgramRecords.Clear();
        ProgramRecords[program] = new ProgramRecord();
        _ = Assert(ProgramRecords.Count <= MaxProgramRecords);
    }

    private static void ShaderAttached(uint program, uint shader, bool on)
    {
        if (!Recording || !ProgramRecords.TryGetValue(program, out var p) || !Assert(shader > 0)) return;
        if (on && !p.Shaders.Contains(shader) && p.Shaders.Count < MaxShaders) p.Shaders.Add(shader);
        else if (!on) _ = p.Shaders.Remove(shader);
        _ = Assert(p.Shaders.Count <= MaxShaders);
    }

    private static void AttributeBound(uint program, uint index, byte* name)
    {
        if (!Recording || !ProgramRecords.TryGetValue(program, out var p) || !Assert(index < 4096)) return;
        if (Named(name) is { } attribute && Assert(p.AttributeBinds.Count <= MaxNamed) &&
            p.AttributeBinds.Count < MaxNamed)
            p.AttributeBinds[attribute] = (int)index;
    }

    // Linking takes the stages attached and the attribute locations bound; locations and block bindings start anew
    private static void ProgramLinked(uint program)
    {
        if (!Recording || !ProgramRecords.TryGetValue(program, out var p) || !Assert(program > 0)) return;
        (p.Stages, var vertex, var fragment, var other) = (null, "", "", 0);
        p.Locations.Clear();
        p.Blocks.Clear();
        p.Points.Clear();
        foreach (var shader in p.Shaders.Bounded(MaxShaders))
        {
            if (!ShaderRecords.TryGetValue(shader, out var s)) return;
            if (s.Type == VertexStage) vertex = s.Source;
            else if (s.Type == FragmentStage) fragment = s.Source;
            else other = (int)s.Type;
        }

        var bound = new Dictionary<string, int>(p.AttributeBinds, StringComparer.Ordinal);
        p.Stages = new Linked(vertex, fragment, other, bound);
        _ = Assert(p.Stages.Attributes.Count == p.AttributeBinds.Count);
        if (Linking is { } linking && other == 0 && vertex.Length > 0 && fragment.Length > 0) linking(program);
    }

    // The engine's (and the mods') own location questions, kept without a string for one already known
    private static void LocationAsked(uint program, byte* name, int location)
    {
        if (!Recording || !ProgramRecords.TryGetValue(program, out var p) || p.Stages is null) return;
        if (!Assert(location >= -1)) return;
        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
        if (bytes.Length >= MaxName || p.Locations.Count >= MaxNamed) return;
        Span<char> chars = stackalloc char[MaxName];
        var named = chars[..Encoding.UTF8.GetChars(bytes, chars)];
        var lookup = p.Locations.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!lookup.TryGetValue(named, out var known) || known != location) lookup[named] = location;
        _ = Assert(p.Locations.Count <= MaxNamed);
    }

    private static void BlockAsked(uint program, byte* name, uint index)
    {
        if (!Recording || !ProgramRecords.TryGetValue(program, out var p) || p.Stages is null) return;
        if (Assert(name != null) && Named(name) is { } block && Assert(p.Blocks.Count <= MaxNamed) &&
            p.Blocks.Count < MaxNamed) p.Blocks[block] = index;
    }

    private static void BlockBound(uint program, uint index, uint binding)
    {
        if (!Recording || !ProgramRecords.TryGetValue(program, out var p) || p.Stages is null) return;
        if (index == InvalidIndex) return;
        if (Assert(binding < 1024) && Assert(p.Points.Count <= MaxNamed) && p.Points.Count < MaxNamed)
            p.Points[index] = (int)binding;
    }

    private static string? Named(byte* name)
    {
        if (name == null || !Assert(MaxName > 0)) return null;
        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
        return bytes.Length is > 0 and < MaxName && Assert(Recording) ? Encoding.UTF8.GetString(bytes) : null;
    }

    // What the program was linked from, when it was linked under the record from stages the record heard
    public static Linked? LinkedOf(int program) =>
        Recording && program > 0 && ProgramRecords.TryGetValue((uint)program, out var p) &&
        Assert(p.Shaders.Count <= MaxShaders) && Assert(p.Points.Count <= MaxNamed)
            ? p.Stages
            : null;

    // A uniform's location as the engine was told it since the last link; null when nobody asked
    public static int? LocationOf(int program, string name) =>
        LinkedOf(program) is not null && NotNull(name) && Assert(name.Length > 0) &&
        ProgramRecords[(uint)program].Locations.TryGetValue(name, out var location)
            ? location
            : null;

    // Block binding point (declared: layout binding or 0 after link), -1 if unused; null if index not asked since link
    public static int? BlockPointOf(int program, string block, int declared)
    {
        if (LinkedOf(program) is null || !NotNull(block)) return null;
        if (!ProgramRecords[(uint)program].Blocks.TryGetValue(block, out var index)) return null;
        if (index == InvalidIndex) return -1;
        _ = Assert(declared >= 0);
        return ProgramRecords[(uint)program].Points.TryGetValue(index, out var point) ? point : declared;
    }

    // Size as glBufferData/glBufferStorage last gave it (record), else GL's answer (waits for glthread, not the GPU)
    public static long BufferSize(uint buffer)
    {
        if (!Assert(buffer is > 0 and < int.MaxValue)) return 0;
        if (BufferSizeOf(buffer) is { } recorded) return recorded;
        GL.GetNamedBufferParameter((int)buffer, BufferParameterName.BufferSize, out int asked);
        return Assert(asked >= 0) ? asked : 0;
    }

    public static long? BufferSizeOf(uint buffer) =>
        Recording && buffer > 0 && BufferSizes.TryGetValue(buffer, out var size) && Assert(size >= 0) &&
        Assert(BufferSizes.Count <= MaxRecords)
            ? size
            : null;

    private static void Sized(uint buffer, long size)
    {
        if (!Recording || buffer == 0 || !Assert(size >= 0)) return;
        if (BufferSizes.Count >= MaxRecords) BufferSizes.Clear();
        BufferSizes[buffer] = size;
        _ = Assert(BufferSizes.Count <= MaxRecords);
    }
}
