namespace Komet.Gpu;

// Handles are the backend's own numbers; 0 is none.
internal interface IGpuBackend
{
    bool Supported();

    // Doubles in compute (rows.comp's exact frustum test)
    bool Doubles() => true;

    // A program from one of GpuShaders' files; 0, with the log written, when it does not build
    int Program(string shader, ILogger logger);

    int Buffer();

    // Fresh store of at least minBytes; the old one may still be in GPU use and is not waited for
    void Upload<T>(int buffer, ReadOnlySpan<T> data, int minBytes = 0) where T : unmanaged;

    // At the byte offset, keeping the rest; the store must be large enough
    void UploadAt<T>(int buffer, int offsetBytes, ReadOnlySpan<T> data) where T : unmanaged;

    void Zero(int buffer, int firstUint, int uints);

    // Host memory to fill before the next recorded command, landing like UploadAt; null when unsupported (OpenGL)
    unsafe byte* Staged(int buffer, int offsetBytes, int bytes) => null;

    // Waits for GPU writes: only for buffers last written frames ago, on OpenGL only Readable ones
    void Read(int buffer, int firstUint, Span<uint> into);

    // Storage Read maps as is; radeonsi reads VRAM/write-combined buffers via a copy that waits for the frame so far
    void Readable(int buffer, int bytes);

    // Same offset in both buffers, on the GPU, after a Copy barrier
    void Copy(int from, int to, int firstUint, int uints);

    void Storage(int binding, int buffer);
    void Uniforms(int binding, int buffer);
    void Texture(int unit, int texture);
    void Image(int unit, int texture, int level, bool write);

    // An R32F image with every level down to 1x1
    int Pyramid(int width, int height, out int levels);

    // An engine texture (its depth buffer) as this backend's Texture handle; 0 when unreadable
    int Engine(int texture);

    // In local-size groups; the engine's program in use is restored afterwards
    void Dispatch(int program, int x, int y = 1);

    void Barrier(GpuBarrier wait);

    void DeleteProgram(ref int program);
    void DeleteBuffer(ref int buffer);
    void DeleteTexture(ref int texture);
}

internal static class GpuBackends
{
    // Vulkan's while it draws the terrain; else OpenGL's, which only the GPU tests dispatch on
    public static IGpuBackend Current { get; set; } = GlBackend.Instance;
}

// Destination accesses of a Vulkan pipeline barrier, or glMemoryBarrier bits
[Flags]
internal enum GpuBarrier
{
    None = 0,
    Storage = 1, // compute writes to buffers, then compute reads them
    Images = 2, // image stores, then image loads
    Textures = 4, // image stores, then texture fetches
    Indirect = 8, // compute writes, then indirect draw commands and counts read them
    Attachments = 16, // draws into the depth buffer, then texture fetches of it
    Copy = 32 // compute writes to buffers, then a buffer copy reads them
}
