using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// A hilly world of block faces with tree crowns, far from the origin as the game's, in chunk sections of 32 blocks spread over the
// pools at random (as the engine's pools come to hold them): each pool a VAO of positions relative to its origin, uvs and indices,
// each section with faces a location. Drawn by OpenGL, whole or from the commands rows.comp wrote, as the engine's pools are.
// Sorted: each section's faces go through FaceSorting as the tessellator's do, and its groups are the location's.
internal sealed class TerrainWorld : IDisposable
{
    public const double Cx = 512000, Cz = 512000;
    private const int Chunk = 32, CommandBytes = 20, StatUints = 8;
    private const string Culling = "Komet.Rendering.OcclusionCulling, Komet";

    private readonly List<VAO> _vaos = [];
    private readonly List<int> _buffers = [];
    private readonly int _texture;

    private readonly bool _sorted;

    public TerrainWorld(int radius, int pools, int seed, bool sorted = false)
    {
        _sorted = sorted;
        var random = new Random(seed);
        var sections = new Dictionary<(int, int, int), List<float>>();
        Faces(radius, sections);
        var chosen = sections.Keys.ToDictionary(k => k, _ => random.Next(pools));
        for (var p = 0; p < pools; p++)
            Pools.Add(Pool([.. sections.Where(s => chosen[s.Key] == p).OrderBy(_ => random.Next())]));
        _texture = Texture();
    }

    public List<MeshDataPool> Pools { get; } = [];
    public long Triangles { get; private set; }

    // Each pool's positions, three floats a vertex, relative to its origin
    public List<float[]> Positions { get; } = [];

    // Each location of each pool, in the order its pool lists them
    public IEnumerable<ModelDataPoolLocation> Locations => Pools.SelectMany(p =>
        (List<ModelDataPoolLocation>)AccessTools.Field(typeof(MeshDataPool), "poolLocations").GetValue(p)!);

    public static int Height(int x, int z) => (int)(100 + 18 * Math.Sin(x / 37.0) * Math.Cos(z / 53.0) +
                                                   9 * Math.Sin((x + z) / 23.0) + 5 * Math.Cos(x / 11.0 - z / 17.0));

    private int[][] _heights = [];
    private int _edge;

    private int Ground(int x, int z) => _heights[x + _edge][z + _edge];

    // A crown of 5x5x4 leaves four above the ground on a trunk, every 11 blocks
    private bool Tree(int x, int y, int z)
    {
        var (tx, tz) = ((int)Math.Floor(x / 11.0) * 11 + 5, (int)Math.Floor(z / 11.0) * 11 + 5);
        var ground = Ground(tx, tz);
        if (Math.Abs(x - tx) <= 2 && Math.Abs(z - tz) <= 2 && y >= ground + 4 && y <= ground + 7) return y > Ground(x, z);
        return x == tx && z == tz && y > ground && y < ground + 4;
    }

    private bool Solid(int x, int y, int z) => y <= Ground(x, z) || Tree(x, y, z);

    private static readonly (int X, int Y, int Z)[] Sides = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    private void Faces(int radius, Dictionary<(int, int, int), List<float>> sections)
    {
        _edge = radius + 16;
        _heights = [.. Enumerable.Range(-_edge, 2 * _edge).Select(x => Enumerable.Range(-_edge, 2 * _edge)
            .Select(z => Height(x, z)).ToArray())];
        for (var x = -radius; x < radius; x++)
        {
            for (var z = -radius; z < radius; z++) Column(sections, x, z);
        }
    }

    private void Column(Dictionary<(int, int, int), List<float>> sections, int x, int z)
    {
        var (h, low) = (Ground(x, z), Sides.Where(s => s.Y == 0).Min(s => Ground(x + s.X, z + s.Z)));
        for (var y = Math.Min(low, h); y <= h + 24; y++)
        {
            if (!Solid(x, y, z)) continue;
            foreach (var side in Sides)
                if (!Solid(x + side.X, y + side.Y, z + side.Z))
                    Face(sections, (x, y, z), side);
        }
    }

    // A block's face toward the side, its corners counter-clockwise seen from outside, positions relative to the pool's origin
    private static void Face(Dictionary<(int, int, int), List<float>> sections, (int X, int Y, int Z) b, (int X, int Y, int Z) s)
    {
        var key = (Floor(b.X), Floor(b.Y), Floor(b.Z));
        if (!sections.TryGetValue(key, out var list)) sections[key] = list = [];
        var (u, v) = ((1, 0, 0), (0, 1, 0));
        if (s.X != 0) (u, v) = ((0, 1, 0), (0, 0, 1));
        else if (s.Y != 0) (u, v) = ((0, 0, 1), (1, 0, 0));
        if (s.X + s.Y + s.Z < 0) (u, v) = (v, u);
        var o = (X: b.X + (s.X > 0 ? 1 : 0), Y: b.Y + (s.Y > 0 ? 1 : 0), Z: b.Z + (s.Z > 0 ? 1 : 0));
        ReadOnlySpan<(int, int)> corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
        foreach (var (a, c) in corners)
            list.AddRange([o.X + a * u.Item1 + c * v.Item1, o.Y + a * u.Item2 + c * v.Item2, o.Z + a * u.Item3 + c * v.Item3,
                a * 0.25f, c * 0.25f]);
    }

    private static int Floor(int v) => (int)Math.Floor(v / (double)Chunk);

    private MeshDataPool Pool(KeyValuePair<(int X, int Y, int Z), List<float>>[] sections)
    {
        var (vertices, indices, locations) = (new List<float>(), new List<int>(), new List<ModelDataPoolLocation>());
        var meshes = new List<MeshData>();
        foreach (var ((cx, cy, cz), listed) in sections)
        {
            var (first, quads, vertex) = (indices.Count, listed.Count / 20, vertices.Count / 5);
            var faces = _sorted ? Sorted(listed, meshes) : listed;
            for (var q = 0; q < quads; q++)
            {
                var corner = vertices.Count / 5 + 4 * q;
                indices.AddRange([corner, corner + 1, corner + 2, corner, corner + 2, corner + 3]);
            }

            vertices.AddRange(faces);
            const float half = Chunk / 2f;
            locations.Add(new ModelDataPoolLocation
            {
                IndicesStart = first, IndicesEnd = indices.Count, VerticesStart = vertex, LodLevel = 1,
                CullVisible = new Bools(true, true),
                FrustumCullSphere = new Sphere((float)(Cx + cx * Chunk + half), cy * Chunk + half, (float)(Cz + cz * Chunk + half),
                    half * 1.7320508f, half * 1.7320508f, half * 1.7320508f)
            });
        }

        Triangles += indices.Count / 3;
        var vao = new VAO { VaoId = GL.GenVertexArray() };
        GL.BindVertexArray(vao.VaoId);
        var (positions, elements) = (GL.GenBuffer(), GL.GenBuffer());
        GL.BindBuffer(BufferTarget.ArrayBuffer, positions);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * 4, vertices.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 20, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 20, 12);
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, elements);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * 4, indices.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
        (vao.vboIdIndex, vao.IndicesCount) = (elements, indices.Count);
        for (var m = 0; m < meshes.Count; m++)
        {
            meshes[m].XyzOffset = locations[m].VerticesStart * 12;
            FaceSorting.Uploaded(vao.VaoId, meshes[m]);
        }

        var xyz = new float[vertices.Count / 5 * 3];
        for (var v = 0; v < xyz.Length / 3; v++)
            (xyz[3 * v], xyz[3 * v + 1], xyz[3 * v + 2]) = (vertices[5 * v], vertices[5 * v + 1], vertices[5 * v + 2]);
        Positions.Add(xyz);
        _buffers.AddRange([positions, elements]);
        _vaos.Add(vao);
        var pool = (MeshDataPool)RuntimeHelpers.GetUninitializedObject(typeof(MeshDataPool));
        AccessTools.Field(typeof(MeshDataPool), "poolLocations").SetValue(pool, locations);
        AccessTools.Field(typeof(MeshDataPool), "modelRef").SetValue(pool, vao);
        AccessTools.Field(typeof(MeshDataPool), "poolOrigin").SetValue(pool, new Vec3i((int)Cx, 0, (int)Cz));
        return pool;
    }

    // The section's faces as a mesh FaceSorting sorts, back as they now lie
    private static List<float> Sorted(List<float> faces, List<MeshData> meshes)
    {
        var vertices = faces.Count / 5;
        var mesh = new MeshData(false)
        {
            VerticesCount = vertices, IndicesCount = vertices / 4 * 6, xyz = new float[3 * vertices], Uv = new float[2 * vertices],
            Flags = new int[vertices], Indices = [.. Enumerable.Range(0, vertices / 4 * 6).Select(i => i / 6 * 4 + Quad[i % 6])],
            VerticesPerFace = 4, IndicesPerFace = 6
        };
        for (var v = 0; v < vertices; v++)
        {
            (mesh.xyz[3 * v], mesh.xyz[3 * v + 1], mesh.xyz[3 * v + 2]) = (faces[5 * v], faces[5 * v + 1], faces[5 * v + 2]);
            (mesh.Uv[2 * v], mesh.Uv[2 * v + 1]) = (faces[5 * v + 3], faces[5 * v + 4]);
        }

        FaceSorting.Sort(mesh, culled: true);
        meshes.Add(mesh);
        return [.. Enumerable.Range(0, vertices).SelectMany(v => new[]
            { mesh.xyz[3 * v], mesh.xyz[3 * v + 1], mesh.xyz[3 * v + 2], mesh.Uv[2 * v], mesh.Uv[2 * v + 1] })];
    }

    private static readonly int[] Quad = [0, 1, 2, 0, 2, 3];

    private static int Texture()
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var pixels = Enumerable.Range(0, 16 * 16 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : i * 7 % 251)).ToArray();
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 16, 16, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
            pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    // The engine's vertex shader reduced to its positions: the pool's origin relative to the camera, then the matrix
    public const string Vertex = """
        #version 460 core
        layout(location = 0) in vec3 xyz;
        layout(location = 1) in vec2 uvIn;
        uniform mat4 mvp;
        uniform vec3 origin;
        out vec2 uv;
        void main() { uv = uvIn; gl_Position = mvp * vec4(xyz + origin, 1.0); }
        """;

    // chunkshadowmap.fsh
    public const string ShadowFragment = """
        #version 460 core
        uniform sampler2D tex;
        in vec2 uv;
        out vec4 outColor;
        void main() { outColor = texture(tex, uv); if (outColor.a < 0.02) discard; }
        """;

    // As heavy as chunkopaque's: two fetches, fog and light, an alpha test, four outputs
    public const string OpaqueFragment = """
        #version 460 core
        uniform sampler2D tex;
        in vec2 uv;
        layout(location = 0) out vec4 outColor;
        layout(location = 1) out vec4 outGlow;
        layout(location = 2) out vec4 outNormal;
        layout(location = 3) out vec4 outPosition;
        void main()
        {
            vec4 color = texture(tex, uv) * texture(tex, uv.yx * 0.5 + 0.25);
            float z = gl_FragCoord.z / gl_FragCoord.w, fog = clamp(1.0 - exp(-z * 0.002), 0.0, 1.0);
            for (int i = 0; i < 24; i++) color.rgb = mix(color.rgb, sqrt(abs(sin(color.gbr * 3.1 + float(i)))), 0.05);
            if (color.a < 0.001) discard;
            outColor = vec4(mix(color.rgb, vec3(0.6, 0.7, 0.8), fog), 1.0);
            outGlow = vec4(fog, 0.0, 0.0, 1.0);
            outNormal = vec4(normalize(vec3(uv, 1.0)), 1.0);
            outPosition = vec4(gl_FragCoord.xyz, z);
        }
        """;

    public static int Program(string fragment) => Parity.Linked(Vertex, fragment);

    private void Prepare(int program, float[] mvp)
    {
        GL.UseProgram(program);
        GL.UniformMatrix4(GL.GetUniformLocation(program, "mvp"), 1, false, mvp);
        GL.BindTextureUnit(0, _texture);
        GL.Uniform1(GL.GetUniformLocation(program, "tex"), 0);
    }

    private static void Origin(int program, Vec3d camera) =>
        GL.Uniform3(GL.GetUniformLocation(program, "origin"), (float)(Cx - camera.X), (float)-camera.Y, (float)(Cz - camera.Z));

    // Every face, as no culling
    public void DrawAll(int program, float[] mvp, Vec3d camera)
    {
        Prepare(program, mvp);
        Origin(program, camera);
        foreach (var vao in _vaos)
        {
            GL.BindVertexArray(vao.VaoId);
            GL.DrawElements(PrimitiveType.Triangles, vao.IndicesCount, DrawElementsType.UnsignedInt, 0);
        }

        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    // The last culled call's draws as OcclusionCulling issues them: a pool's commands from its region (each pool's after the
    // earlier pools' rows), its count where rows.comp counted; the shadow passes' from their own place
    public void DrawCulled(int program, float[] mvp, Vec3d camera, bool shadow, bool sorted, int factor = 1)
    {
        Prepare(program, mvp);
        Origin(program, camera);
        var (counters, frame) = (CounterBuffers(null), FrameCount(null));
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, sorted ? SortedBuffer(null) : CommandBuffer(null));
        GL.BindBuffer(BufferTarget.ParameterBuffer, counters[(frame - 1) % counters.Length]);
        var (region, draw) = (shadow ? 2 * OcclusionCulling.MaxRanges : 0, shadow ? OcclusionCulling.MaxDraws : 0);
        var countBase = StatUints + (shadow ? OcclusionCulling.MaxDraws : 0);
        for (var p = 0; p < _vaos.Count; p++)
        {
            var rows = factor * ((List<ModelDataPoolLocation>)AccessTools.Field(typeof(MeshDataPool), "poolLocations")
                .GetValue(Pools[p])!).Count;
            GL.BindVertexArray(_vaos[p].VaoId);
            GL.MultiDrawElementsIndirectCount(PrimitiveType.Triangles, DrawElementsType.UnsignedInt,
                region * CommandBytes, 4 * (countBase + draw + p), rows, CommandBytes);
            region += rows;
        }

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
        GL.BindBuffer(BufferTarget.ParameterBuffer, 0);
        GL.UseProgram(0);
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CounterBuffers")]
    private static extern ref int[] CounterBuffers([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_commands")]
    private static extern ref int CommandBuffer([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_sorted")]
    internal static extern ref int SortedBuffer([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_frame")]
    private static extern ref int FrameCount([UnsafeAccessorType(Culling)] object? culling);

    public void Dispose()
    {
        foreach (var vao in _vaos)
        {
            GL.DeleteVertexArray(vao.VaoId);
            vao.Dispose(); // its finalizer wants the engine's platform, which the tests do not have
        }

        foreach (var buffer in _buffers) GL.DeleteBuffer(buffer);
        GL.DeleteTexture(_texture);
    }
}

// The camera and the engine's far shadow map as SystemRenderShadowMap makes them (ShadowBox with its identity rotation: a box
// around a view down -z, whichever way the camera looks), camera-relative for drawing, in the world for culling
internal sealed class TerrainView
{
    public const double Fov = 70, Aspect = 2560 / 1366.0, Near = 0.1, Far = 1500, ShadowDistance = 255;

    public TerrainView(double yaw, double pitch, double[] sun)
    {
        Camera = new Vec3d(TerrainWorld.Cx + 0.5, TerrainWorld.Height(0, 0) + 2.62, TerrainWorld.Cz + 0.5);
        var look = new[] { Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), -Math.Cos(pitch) * Math.Cos(yaw) };
        Projection = Mat4d.Perspective(Mat4d.Create(), Fov * Math.PI / 180, Aspect, Near, Far);
        double[] eye = [Camera.X, Camera.Y, Camera.Z], up = [0, 1, 0];
        World = Mat4d.LookAt(Mat4d.Create(), eye, [eye[0] + look[0], eye[1] + look[1], eye[2] + look[2]], up);
        View.CalcFrustumEquations(Block, Projection, World);
        var relative = Mat4d.LookAt(Mat4d.Create(), [0, 0, 0], look, up);
        CameraMvp = Floats(Mat4d.Mul(Mat4d.Create(), Projection, relative));
        Inverse = Mat4d.Invert(Mat4d.Create(), Mat4d.Mul(Mat4d.Create(), Projection, relative));
        Shadow(sun);
    }

    public Vec3d Camera { get; }
    public BlockPos Block => new((int)Camera.X, (int)Camera.Y, (int)Camera.Z);
    public double[] Projection { get; }
    public double[] World { get; } // the camera's matrix in the world
    public FrustumCulling View { get; } = new();
    public FrustumCulling ShadowCuller { get; } = new();
    public float[] CameraMvp { get; }
    public double[] Inverse { get; }
    public double[] ShadowMvp { get; private set; } = [];

    private void Shadow(double[] sun)
    {
        var length = Math.Sqrt(sun.Sum(v => v * v));
        double[] s = [sun[0] / length, sun[1] / length, sun[2] / length], up = [0, 1, 0];
        var extend = 100 + 60 * Math.Abs(1 - s[1]);
        var light = Mat4d.LookAt(Mat4d.Create(), s, [0, 0, 0], up);
        var k = Math.Min(1, Fov / 90);
        var (farW, nearW) = (ShadowDistance * k, Near * k);
        var (farH, nearH) = (farW / Aspect, nearW / Aspect);
        var (min, max) = (new[] { double.MaxValue, double.MaxValue, double.MaxValue }, new[] { double.MinValue, double.MinValue, double.MinValue });
        foreach (var (depth, w, h) in new[] { (ShadowDistance, farW, farH), (Near, nearW, nearH) })
        foreach (var (a, b) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
        {
            var p = Mat4d.MulWithVec4(light, [a * w, b * h, -depth, 1]);
            for (var i = 0; i < 3; i++) (min[i], max[i]) = (Math.Min(min[i], p[i]), Math.Max(max[i], p[i]));
        }

        var ortho = Mat4d.Create();
        (ortho[0], ortho[5], ortho[10]) = (2 / (max[0] - min[0]), 2 / (max[1] - min[1]), -2 / (max[2] + extend - min[2]));
        ShadowMvp = Mat4d.Mul(Mat4d.Create(), ortho, light);
        double[] center = [Camera.X, Camera.Y, Camera.Z];
        var looking = Mat4d.LookAt(Mat4d.Create(), [center[0] + s[0], center[1] + s[1], center[2] + s[2]], center, up);
        ShadowCuller.CalcFrustumEquations(Block, ortho, looking);
        (ShadowCuller.shadowRangeX, ShadowCuller.shadowRangeZ) = (ShadowDistance + extend, ShadowDistance);
    }

    public static float[] Floats(double[] m) => [.. m.Select(v => (float)v)];
}
