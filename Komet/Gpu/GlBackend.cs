using System.Runtime.InteropServices;
using Komet.Diagnostics;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Gpu;

// IGpuBackend on OpenGL. Leaves engine state as found: program in use restored after each dispatch (ShaderUseCache
// trusts CurrentShaderProgram); bindings above the engine's (SSBO 3, low texture units, UBO points).
internal sealed class GlBackend : IGpuBackend
{
    public static readonly GlBackend Instance = new();

    private GlBackend()
    {
    }

    // 4.3 compute and SSBOs, 4.5 DSA, 4.6 indirect count draws
    public bool Supported() => Assert(GpuStats.GlVersion > 0) && GpuStats.GlVersion >= 46;

    public int Program(string shader, ILogger logger)
    {
        if (!NotNull(logger) || GpuShaders.Source(shader) is not { } source) return 0;
        var stage = GL.CreateShader(ShaderType.ComputeShader);
        if (!Assert(stage != 0)) return 0;
        GL.ShaderSource(stage, source);
        GL.CompileShader(stage);
        GL.GetShader(stage, ShaderParameter.CompileStatus, out var compiled);
        if (compiled == 0)
        {
            logger.Warning("Komet: {0} does not compile: {1}", shader, GL.GetShaderInfoLog(stage));
            GL.DeleteShader(stage);
            return 0;
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, stage);
        GL.LinkProgram(program);
        GL.DetachShader(program, stage);
        GL.DeleteShader(stage);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        if (linked != 0) return program;
        logger.Warning("Komet: {0} does not link: {1}", shader, GL.GetProgramInfoLog(program));
        GL.DeleteProgram(program);
        return 0;
    }

    public void Dispatch(int program, int x, int y = 1)
    {
        if (!Assert(program != 0) || !Assert(x >= 0 && y >= 0) || x == 0 || y == 0) return;
        GL.UseProgram(program);
        GL.DispatchCompute(x, y, 1);
        GL.UseProgram(ShaderProgramBase.CurrentShaderProgram?.ProgramId ?? 0);
    }

    // MemoryBarrierFlags lacks [Flags]: fetch 0x8, image 0x20, command 0x40, update 0x200, storage 0x2000
    public void Barrier(GpuBarrier wait)
    {
        if (!Assert(wait != GpuBarrier.None) || !Assert((int)wait < 64)) return;
        var bits = (wait.HasFlag(GpuBarrier.Storage) ? 0x2200 : 0) | (wait.HasFlag(GpuBarrier.Images) ? 0x20 : 0) |
                   (wait.HasFlag(GpuBarrier.Textures) ? 0x8 : 0) | (wait.HasFlag(GpuBarrier.Indirect) ? 0x2040 : 0) |
                   (wait.HasFlag(GpuBarrier.Copy) ? 0x200 : 0);
        if (wait.HasFlag(GpuBarrier.Attachments)) GL.TextureBarrier();
        if (bits != 0) GL.MemoryBarrier((MemoryBarrierFlags)bits);
    }

    public int Buffer()
    {
        GL.CreateBuffers(1, out int buffer);
        _ = Assert(buffer != 0);
        return buffer;
    }

    // Orphans the old store
    public void Upload<T>(int buffer, ReadOnlySpan<T> data, int minBytes = 0) where T : unmanaged
    {
        if (!Assert(buffer != 0) || !Assert(minBytes >= 0)) return;
        var bytes = Math.Max(data.Length * Marshal.SizeOf<T>(), Math.Max(minBytes, 16));
        GL.BindBuffer(BufferTarget.CopyWriteBuffer, buffer);
        GL.BufferData(BufferTarget.CopyWriteBuffer, bytes, IntPtr.Zero, BufferUsageHint.StreamDraw);
        if (data.Length > 0)
            GL.BufferSubData(BufferTarget.CopyWriteBuffer, IntPtr.Zero, data.Length * Marshal.SizeOf<T>(),
                ref MemoryMarshal.GetReference(data));
        GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
    }

    public void UploadAt<T>(int buffer, int offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (!Assert(buffer != 0) || !Assert(offsetBytes >= 0) || data.IsEmpty) return;
        GL.BindBuffer(BufferTarget.CopyWriteBuffer, buffer);
        GL.BufferSubData(BufferTarget.CopyWriteBuffer, offsetBytes, data.Length * Marshal.SizeOf<T>(),
            ref MemoryMarshal.GetReference(data));
        GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
    }

    public void Zero(int buffer, int firstUint, int uints)
    {
        if (!Assert(buffer != 0) || !Assert(firstUint >= 0 && uints > 0)) return;
        uint zero = 0;
        GL.ClearNamedBufferSubData(buffer, PixelInternalFormat.R32ui, 4 * firstUint, 4 * uints, PixelFormat.RedInteger,
            PixelType.UnsignedInt, ref zero);
    }

    // Also waits for Mesa's glthread
    public void Read(int buffer, int firstUint, Span<uint> into)
    {
        if (!Assert(buffer != 0) || !Assert(firstUint >= 0) || into.IsEmpty) return;
        GL.GetNamedBufferSubData(buffer, 4 * firstUint, 4 * into.Length, ref MemoryMarshal.GetReference(into));
    }

    public int Engine(int texture) => Assert(texture >= 0) ? texture : 0;

    // GL_STREAM_READ is Mesa's staging usage: cached system memory, which a read maps directly
    public void Readable(int buffer, int bytes)
    {
        if (!Assert(buffer != 0) || !Assert(bytes > 0)) return;
        GL.NamedBufferData(buffer, bytes, IntPtr.Zero, BufferUsageHint.StreamRead);
    }

    public void Copy(int from, int to, int firstUint, int uints)
    {
        if (!Assert(from != 0 && to != 0 && from != to) || !Assert(firstUint >= 0 && uints > 0)) return;
        GL.CopyNamedBufferSubData(from, to, 4 * firstUint, 4 * firstUint, 4 * uints);
    }

    public void Storage(int binding, int buffer)
    {
        if (!Assert(binding > 3) || !Assert(buffer > 0)) return;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, binding, buffer);
    }

    public void Uniforms(int binding, int buffer)
    {
        if (!Assert(binding > 8) || !Assert(buffer > 0)) return;
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, binding, buffer);
    }

    public void Texture(int unit, int texture)
    {
        if (!Assert(unit > 8) || !Assert(texture >= 0)) return;
        GL.BindTextureUnit(unit, texture);
    }

    public int Pyramid(int width, int height, out int levels)
    {
        levels = 0;
        if (!Assert(width > 0 && height > 0)) return 0;
        levels = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int texture);
        if (!Assert(texture != 0)) return 0;
        GL.TextureStorage2D(texture, levels, SizedInternalFormat.R32f, width, height);
        return texture;
    }

    public void Image(int unit, int texture, int level, bool write)
    {
        if (!Assert(unit >= 0) || !Assert(texture > 0 && level >= 0)) return;
        GL.BindImageTexture(unit, texture, level, false, 0, write ? TextureAccess.WriteOnly : TextureAccess.ReadOnly,
            SizedInternalFormat.R32f);
    }

    public void DeleteProgram(ref int program)
    {
        if (!Assert(program >= 0)) return;
        if (program != 0) GL.DeleteProgram(program);
        program = 0;
    }

    public void DeleteBuffer(ref int buffer)
    {
        if (!Assert(buffer >= 0)) return;
        if (buffer != 0) GL.DeleteBuffer(buffer);
        buffer = 0;
    }

    public void DeleteTexture(ref int texture)
    {
        if (!Assert(texture >= 0)) return;
        if (texture != 0) GL.DeleteTexture(texture);
        texture = 0;
    }
}
