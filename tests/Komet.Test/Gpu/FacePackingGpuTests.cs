using Komet.Core;
using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

public sealed class FacePackingGpuTests
{
    private const int Faces = 6000, Vertices = 4 * Faces;

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 4)]
    [TestCase(true, 4)]
    public void TheUploadIsTheEngines(bool persistent, int workers)
    {
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var data = Mesh(new Random(5));
        var (hud, parallel) = (Counting.Hud, FacePacking.Parallel);
        Counting.Hud = true;
        if (workers > 0) WorkerPool.Resize(null, workers);
        var (engine, komet) = (Vao(persistent), Vao(persistent));
        try
        {
            platform.UpdateSSBOMesh(engine.Vao, data);
            Assert.That(FacePacking.Uploading(platform, komet.Vao, data), Is.False, "Komet made the upload");
            GL.Finish();
            Assert.Multiple(() =>
            {
                Assert.That(Read(komet.Faces, 64 * Faces), Is.EqualTo(Read(engine.Faces, 64 * Faces)), "the faces");
                Assert.That(Read(komet.Colors, 4 * Vertices), Is.EqualTo(Read(engine.Colors, 4 * Vertices)), "the colours");
                Assert.That(Read(komet.Ints, 4 * Vertices), Is.EqualTo(Read(engine.Ints, 4 * Vertices)), "the ints");
                Assert.That(komet.Vao.IndicesCount, Is.EqualTo(engine.Vao.IndicesCount));
                Assert.That(FacePacking.Parallel - parallel, workers > 0 ? Is.Positive : Is.Zero, "packed on the workers");
            });
        }
        finally
        {
            engine.Vao.Dispose(); // an undisposed VAO's finalizer asks for a platform the test has none of
            komet.Vao.Dispose();
            Counting.Hud = hud;
            if (workers > 0) _ = WorkerPool.Stop();
        }
    }

    // Quads with UVs inside, outside and at the bounds, a rotated corner now and then, colours and two custom ints per vertex
    private static MeshData Mesh(Random r)
    {
        var data = new MeshData(false)
        {
            xyz = new float[3 * Vertices], Uv = new float[2 * Vertices], Flags = new int[Vertices],
            Rgba = new byte[4 * Vertices], VerticesCount = Vertices, IndicesCount = 6 * Faces, XyzOffset = 0, RgbaOffset = 0,
            CustomInts = new CustomMeshDataPartInt(2 * Vertices) { Count = 2 * Vertices, InterleaveStride = 8 }
        };
        for (var i = 0; i < data.xyz.Length; i++) data.xyz[i] = (float)(r.NextDouble() * 64 - 16);
        for (var i = 0; i < data.Uv.Length; i++) data.Uv[i] = r.Next(8) == 0 ? 1.00002f : (float)r.NextDouble();
        for (var f = 0; f < Faces; f += 3) (data.Uv[8 * f + 3], data.Uv[8 * f + 5]) = (data.Uv[8 * f + 1], data.Uv[8 * f + 1]);
        for (var f = 0; f < Faces; f += 5) data.Uv[8 * f + 2] = data.Uv[8 * f + 4];
        for (var i = 0; i < Vertices; i++) data.Flags[i] = r.Next();
        r.NextBytes(data.Rgba);
        for (var i = 0; i < 2 * Vertices; i++) data.CustomInts.Values[i] = r.Next();
        return data;
    }

    private static (VAO Vao, int Faces, int Colors, int Ints) Vao(bool persistent)
    {
        var faces = Buffer(64 * Faces, false, out _);
        var colors = Buffer(4 * Vertices, persistent, out var colorPtr);
        var ints = Buffer(4 * Vertices, persistent, out var intPtr);
        var vao = new VAO
        {
            xyzVboId = faces, rgbaVboId = colors, rgbaPtr = colorPtr, customDataIntVboId = ints, customDataIntPtr = intPtr,
            Persistent = persistent
        };
        return (vao, faces, colors, ints);
    }

    private static int Buffer(int bytes, bool mapped, out nint pointer)
    {
        GL.CreateBuffers(1, out int buffer);
        // map write, persistent, coherent (0xC2), and dynamic storage (0x100) for glBufferSubData
        GL.NamedBufferStorage(buffer, bytes, IntPtr.Zero, (BufferStorageFlags)(mapped ? 0x1C2 : 0x100));
        pointer = mapped ? GL.MapNamedBufferRange(buffer, IntPtr.Zero, bytes, (BufferAccessMask)0xC2) : 0;
        return buffer;
    }

    private static byte[] Read(int buffer, int bytes)
    {
        var into = new byte[bytes];
        GL.GetNamedBufferSubData(buffer, IntPtr.Zero, bytes, into);
        return into;
    }
}
