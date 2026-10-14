namespace Komet.Vulkan;

// A Vulkan image stands for each attachment texture of the engine framebuffers (Stood); the texture keeps name, storage
// and framebuffers.
internal sealed class TerrainTargets(VulkanDevice device) : IDisposable
{
    public const int Primary = 0, Transparent = 1, LiquidDepth = 5, ShadowFar = 11, ShadowNear = 12;
    private const int MaxImages = 80, MaxColors = 8;
    private const uint Usage = Vk.Sampled | Vk.TransferSrc | Vk.TransferDst;

    private readonly Dictionary<int, SharedImage> _images = [];

    private readonly Dictionary<int, int> _resting = [];
    private readonly Dictionary<int, Samplers.State> _sampling = [];
    private readonly List<(FrameBufferRef Framebuffer, int Colors)> _swapped = [];
    private IReadOnlyList<FrameBufferRef> _all = [];

    public IReadOnlyList<FrameBufferRef> Framebuffers
    {
        get => _all;
        set => _all = NotNull(value) && Assert(value.Count < 64) ? value : [];
    }

    public long Version { get; private set; }

    public List<string> Replaced { get; } = [];

    public IEnumerable<SharedImage> Images => _images.Values;

    public IEnumerable<VulkanFrame.Shared> Resting =>
        _images.Values.Select(i => new VulkanFrame.Shared(i, _resting.GetValueOrDefault(i.Texture, Vk.LayoutShaderRead)));

    public FrameBufferRef? Of(int fbo)
    {
        if (!Assert(fbo >= 0) || !Assert(_all.Count < 64) || fbo == 0) return null;
        for (var i = 0; i < Math.Min(_all.Count, 64); i++) // asked for every draw: no closure
            if (_all[i]?.FboId == fbo)
                return _all[i];
        return null;
    }

    public bool Swapped(FrameBufferRef framebuffer)
    {
        if (!NotNull(framebuffer) || !Assert(_swapped.Count <= MaxImages)) return false;
        foreach (var (swapped, _) in _swapped.Bounded(MaxImages))
            if (ReferenceEquals(swapped, framebuffer))
                return true;
        return false;
    }

    public SharedImage? Find(int texture) => Assert(texture >= 0) ? _images.GetValueOrDefault(texture) : null;

    // Layout the image rests in between segments
    public int RestingOf(int texture) =>
        Assert(texture >= 0) && Assert(_resting.Count <= MaxImages) ? _resting.GetValueOrDefault(texture, Vk.LayoutShaderRead) : 0;

    public Samplers.State? Sampling(int texture) =>
        Assert(texture >= 0) && _sampling.TryGetValue(texture, out var s) ? s : null;

    public void Forget(int texture)
    {
        if (!Assert(texture >= 0) || !_images.ContainsKey(texture)) return;
        _ = _swapped.RemoveAll(s => (s.Framebuffer.ColorTextureIds ?? []).Contains(texture) ||
                                    s.Framebuffer.DepthTextureId == texture);
        _ = Free(texture, true);
    }

    public bool Swap(FrameBufferRef framebuffer, out string why)
    {
        why = "";
        if (!NotNull(framebuffer) || !Assert(framebuffer.FboId > 0)) return false;
        var colors = framebuffer.ColorTextureIds ?? [];
        if (!Assert(colors.Length <= MaxColors) || _images.Count + colors.Length + 1 > MaxImages) return false;
        if (_swapped.Exists(s => ReferenceEquals(s.Framebuffer, framebuffer))) return true;
        for (var i = 0; i < Math.Min(colors.Length, MaxColors); i++)
            if (Swapped(colors[i], out why) is null)
                return false;
        if (framebuffer.DepthTextureId > 0)
        {
            var depth = Swapped(framebuffer.DepthTextureId, out why);
            if (depth is null) return false;
            // shadow map: sampled more than drawn, so it rests so from the start (transitions start from the current
            // layout)
            if (colors.Length == 0 && !depth.Rest(device, RestingOf(depth.Texture), Vk.LayoutShaderRead)) return false;
            if (colors.Length == 0) _resting[depth.Texture] = Vk.LayoutShaderRead;
        }

        _swapped.Add((framebuffer, colors.Length));
        return true;
    }

    private SharedImage? Swapped(int texture, out string why)
    {
        why = "";
        if (!Assert(texture > 0) || !Assert(_images.Count <= MaxImages)) return null;
        return _images.GetValueOrDefault(texture) ?? Stood(texture, out why);
    }

    // Other holders of engine textures (transparent pass reveal/accumulation, in statics): Owned asks
    public System.Func<int, bool>? Owned { get; set; }

    // Told of each texture an image here stands for (TextureImages then keeps none)
    public Action<int>? Claimed { get; set; }

    public SharedImage? Share(int texture, out string why)
    {
        why = "";
        if (!Assert(texture > 0)) return null;
        if (_images.TryGetValue(texture, out var known)) return known;
        if (Owned?.Invoke(texture) != true)
        {
            why = $"texture {texture} belongs to no framebuffer and no owner Komet knows";
            return null;
        }

        return Stood(texture, out why);
    }

    // An image of the texture's size and format (RGB as RGBA with alpha 1; contents not carried over: the engine
    // clears its targets every frame); sampling from the record
    private SharedImage? Stood(int texture, out string why)
    {
        var gl = GlTexture.Of(texture);
        if (!Assert(texture > 0) || gl.Width <= 0 || SharedFormat.Only(gl.Format) is not var (f, opaque))
        {
            why = $"texture {texture} (format 0x{gl.Format:X}) has no Vulkan format";
            return null;
        }

        var arrayed = gl.Kind == GlTexture.Array2D;
        var layers = arrayed ? Math.Max(gl.Depth, 1) : 1;
        var depth = (f.Aspect & Vk.AspectDepth) != 0; // a depth and stencil one too
        var usage = Usage | (depth ? Vk.DepthAttachment : Vk.ColorAttachment);
        var image = SharedImage.Private(device, (gl.Width, gl.Height, 1, layers), f, usage, out why, stands: texture,
            arrayed: arrayed);
        if (image is null) return null;
        var resting = depth ? Vk.LayoutDepthAttachment : Vk.LayoutColorAttachment;
        if (!image.Rest(device, Vk.LayoutUndefined, resting))
        {
            image.Dispose();
            why = $"texture {texture}: the device did not take its image";
            return null;
        }

        image.OpaqueAlpha = opaque;
        Claimed?.Invoke(texture);
        if (Replaced.Count < 64)
            Replaced.Add($"texture {texture} as a Vulkan image {gl.Width}x{gl.Height}, {layers} layers, format 0x{gl.Format:X}");
        _sampling[texture] = GlTexture.Sampling(texture) with { Mipmaps = 0 };
        (_images[texture], Version, _resting[texture]) = (image, Version + 1, resting);
        return image;
    }

    // The engine deletes the textures; only their Vulkan side goes here
    public void Release(FrameBufferRef framebuffer)
    {
        if (!NotNull(framebuffer) || !Assert(_images.Count <= MaxImages)) return;
        var colors = framebuffer.ColorTextureIds ?? [];
        if (_images.ContainsKey(framebuffer.DepthTextureId) || Array.Exists(colors, _images.ContainsKey))
            _ = device.WaitIdle(); // once for all of them
        for (var i = 0; i < Math.Min(colors.Length, MaxColors); i++) _ = Free(colors[i], false);
        _ = Free(framebuffer.DepthTextureId, false);
        if (_swapped.RemoveAll(s => ReferenceEquals(s.Framebuffer, framebuffer)) > 0) Version++;
    }

    private bool Free(int texture, bool wait)
    {
        if (!_images.Remove(texture, out var shared) || !Assert(texture > 0) || !Assert(shared.Texture == texture))
            return false;
        Version++;
        if (wait) _ = device.WaitIdle();
        _ = _sampling.Remove(texture);
        _ = _resting.Remove(texture);
        shared.Dispose(); // the texture stays: its owner deletes it
        return true;
    }

    public void Dispose()
    {
        _ = Assert(_images.Count <= MaxImages) && device.WaitIdle();
        foreach (var image in _images.Values.Bounded(MaxImages)) image.Dispose();
        _images.Clear();
        _sampling.Clear();
        _swapped.Clear();
        _resting.Clear();
    }
}
