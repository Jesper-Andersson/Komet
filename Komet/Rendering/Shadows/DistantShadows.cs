using System.Reflection;
using HarmonyLib;
using Komet.Core;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Engine shadow maps fade out between 40 and 90 % of their range (nothing past ~200 blocks at best quality). A third
// map covers the view distance; distant terrain and sun change slowly, so it is redrawn one tile a frame into a back
// texture. Light view snapped to the world texel grid so edges don't crawl.
internal static partial class DistantShadows
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0x0EB00BD70865F789UL;

    private const int Unit = 15, Tiles = 4, MaxPrograms = 512;
    private const double MaxAngle = 0.3 * Math.PI / 180, Margin = 96, MinRadius = 256, MaxRadius = 1024, Bias = 1.5;
    private const long MaxAge = 20000;

    private static bool _shaped, _injected, _failed;
    private static ILogger? _logger;
    private static FieldInfo? _includes;

    // _front is sampled, the other is being drawn
    private static readonly int[] Textures = new int[2], Buffers = new int[2];
    private static readonly Map[] Maps = [new(), new()];
    private static int _front, _size, _tile = -1;

    // For the shaders: matrix into the front map for this frame's camera (_version bumps on change)
    private static readonly float[] Matrix = new float[16], Mvp = new float[16], OnTexel = new float[3];

    // Per-frame matrix scratch (no allocation); main thread
    private static readonly double[] Lit = new double[3], Cam = new double[3], Eye = new double[3];
    private static readonly double[] Step1 = Mat4d.Create(), Step2 = Mat4d.Create(), Clip = Mat4d.Create();
    private static readonly double[] Sample = Mat4d.Create(), Part = Mat4d.Create(), World = Mat4d.Create();
    private static readonly double[] Offset =
        Mat4d.Scale(Mat4d.Create(), Mat4d.Translate(Mat4d.Create(), Mat4d.Create(), 0.5, 0.5, 0.5), [0.5, 0.5, 0.5]);
    private static readonly double[] Up = [0, 1, 0], North = [0, 0, 1], Origin = [0, 0, 0];
    private static float _on;
    private static int _version = 1, _bound, _maps = 1; // _maps: new whenever the front texture's content changes
    private static readonly Dictionary<int, Program> Programs = [];

    public static bool Enabled { get; set; } = true;
    internal static bool Matched => _shaped;
    internal static bool Injected => _injected;
    internal static bool Failed => _failed;

    // Map being drawn: kept across frames, so its casters are not the view's (ShadowView)
    internal static bool Drawing { get; private set; }


    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, _injected, _failed, _logger, _tile) = (false, false, false, logger, -1);
        Programs.Clear();
        _includes = AccessTools.Field(typeof(ShaderRegistry), "includes");
        var seams = Seams();
        if (!NotNull(harmony) || !NotNull(_includes) || !Assert(seams.Length == 4) || seams[0] is not MethodInfo done ||
            seams[1] is not MethodInfo load || seams[3] is not MethodInfo use) return;
        _shaped = EngineShape.Matches(seams, fingerprint, nameof(DistantShadows), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(load, new HarmonyMethod(Inject), new HarmonyMethod(Define)));
        _ = NotNull(harmony.Patch(done, postfix: new HarmonyMethod(Frame)));
        _ = NotNull(harmony.Patch(use, postfix: new HarmonyMethod(Use)));
    }

    internal static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(SystemRenderShadowMap), "OnRenderShadowFarDone", [typeof(float)]),
            AccessTools.DeclaredMethod(typeof(ShaderRegistry), "LoadShaderProgram",
                [typeof(ShaderProgram), typeof(bool)]),
            AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderShadow), [typeof(float)]),
            AccessTools.DeclaredMethod(typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use))
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // Once per full shader load (reload re-reads assets), both includes or neither
    internal static void Inject()
    {
        if (_includes?.GetValue(null) is not Dictionary<string, string> includes || !NotNull(includes)) return;
        var vertex = includes.GetValueOrDefault(VertexFile);
        if (vertex is not null && vertex.Contains(Marker, StringComparison.Ordinal)) return;
        Programs.Clear(); // programs are rebuilt
        var (v, f) = (Vertex(vertex), Fragment(includes.GetValueOrDefault(FragmentFile)));
        _injected = v is not null && f is not null;
        if (!_injected)
        {
            _logger?.Notification("Komet: the shadow shaders are not the engine's, distant shadows stay off");
            return;
        }

        (includes[VertexFile], includes[FragmentFile]) = (v!, f!);
    }

    internal static void Define(ShaderProgram program)
    {
        if (!_injected || !NotNull(program) || program.FragmentShader is not { } fragment) return;
        if (program.includes.Contains(VertexFile) && program.includes.Contains(FragmentFile))
            fragment.PrefixCode = (fragment.PrefixCode ?? "") + Definition;
    }

    internal static void Frame(SystemRenderShadowMap __instance, float dt)
    {
        if (NotNull(__instance) && Game(__instance) is { } game) Tick(game, dt);
    }

    // Also where FarShadowReuse leaves the far pass out
    internal static void Tick(ClientMain game, float dt)
    {
        if (!_injected || _failed || !NotNull(game)) return;
        try
        {
            Step(game, dt);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            (_failed, _on) = (true, 0);
            _version++;
            _logger?.Error("Komet: distant shadows failed, they stay off: {0}", e);
        }
    }

    private static void Step(ClientMain game, float dt)
    {
        var (quality, player) = (FrameSettings.ShadowMapQuality, game.EntityPlayer);
        var intensity = game.shUniforms.DropShadowIntensity;
        if (!Enabled || quality <= 0 || intensity <= 0.01f || player is null)
        {
            Off();
            return;
        }

        if (!Ready(quality >= 3 ? 4096 : 2048)) return;
        (Cam[0], Cam[1], Cam[2]) = (player.CameraPos.X, player.CameraPos.Y, player.CameraPos.Z);
        Light(game, Lit);
        var radius = Math.Clamp(FrameSettings.ViewDistance, MinRadius, MaxRadius) + Margin;
        if (_tile < 0 && Due(Maps[_front], Lit, Cam, radius))
            (_tile, Maps[1 - _front]) = (0, Plan(Lit, Cam, radius, _size));
        if (_tile >= 0) Draw(game, Maps[1 - _front], dt);
        Publish(Maps[_front], Cam);
    }

    // As the engine: stronger of moon and sun, normalised
    internal static void Light(ClientMain game, Span<double> into)
    {
        var calendar = game.Calendar as ClientGameCalendar;
        var v = calendar is not null && calendar.MoonLightStrength > calendar.SunLightStrength
            ? game.Calendar.MoonPosition
            : game.Calendar.SunPosition;
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (!Assert(into.Length == 3)) return;
        if (Assert(length > 0)) (into[0], into[1], into[2]) = (v.X / length, v.Y / length, v.Z / length);
        else (into[0], into[1], into[2]) = (0, 1, 0);
    }

    internal static bool Due(Map map, double[] light, double[] camera, double radius)
    {
        if (!map.Valid || Math.Abs(map.Radius - radius) > 0.5) return true;
        var dot = light[0] * map.Direction[0] + light[1] * map.Direction[1] + light[2] * map.Direction[2];
        var (dx, dz) = (camera[0] - map.Center[0], camera[2] - map.Center[2]);
        return Math.Acos(Math.Clamp(dot, -1, 1)) > MaxAngle || dx * dx + dz * dz > Margin * Margin ||
               Environment.TickCount64 - map.Drawn > MaxAge;
    }

    // Camera snapped in the light plane to the nearest texel corner, so the grid stays put in the world
    internal static Map Plan(double[] light, double[] camera, double radius, int size)
    {
        var map = new Map { Radius = radius, Size = size, Depth = 4 * radius + 1024, Drawn = Environment.TickCount64 };
        if (!Assert(size > 0) || !Assert(radius > 0)) return map;
        _ = Mat4d.LookAt(map.View, light, Origin, Math.Abs(light[1]) > 0.999 ? North : Up);
        _ = Mat4d.Ortho(map.Projection, -radius, radius, -radius, radius, -map.Depth, map.Depth);
        var texel = 2 * radius / size;
        var v = map.View;
        ReadOnlySpan<double> right = [v[0], v[4], v[8]], above = [v[1], v[5], v[9]];
        var (x, y) = (right[0] * camera[0] + right[1] * camera[1] + right[2] * camera[2],
            above[0] * camera[0] + above[1] * camera[1] + above[2] * camera[2]);
        var (dx, dy) = (Math.Round(x / texel) * texel - x, Math.Round(y / texel) * texel - y);
        for (var i = 0; i < 3; i++) map.Center[i] = camera[i] + right[i] * dx + above[i] * dy;
        (map.Direction[0], map.Direction[1], map.Direction[2]) = (light[0], light[1], light[2]);
        return map;
    }

    // Camera-relative position to the map's clip space
    internal static double[] Transform(Map map, double[] camera)
    {
        if (!Assert(camera.Length == 3) || !NotNull(map)) return Clip;
        _ = Mat4d.Identity(Step1);
        var c = map.Center;
        _ = Mat4d.Translate(Step1, Step1, camera[0] - c[0], camera[1] - c[1], camera[2] - c[2]);
        _ = Mat4d.Mul(Step2, map.View, Step1);
        return Mat4d.Mul(Clip, map.Projection, Step2);
    }

    private static void Publish(Map front, double[] camera)
    {
        var on = front.Valid ? 1f : 0f;
        _ = Mat4d.Mul(Sample, Offset, Transform(front, camera));
        var changed = Differ(on, _on);
        for (var i = 0; i < 16; i++)
        {
            changed |= Differ(Matrix[i], (float)Sample[i]);
            Matrix[i] = (float)Sample[i];
        }

        _on = on;
        if (changed && Assert(_version < int.MaxValue)) _version++;
    }

    private static bool Differ(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) != BitConverter.SingleToInt32Bits(b);

    private static void Off()
    {
        if (!Differ(_on, 0) || !Assert(_version < int.MaxValue)) return;
        (_on, _tile) = (0, -1);
        _version++;
    }

    private static void Draw(ClientMain game, Map map, float dt)
    {
        if (!Index(_tile, Tiles * Tiles) || !NotNull(map)) return;
        var (tx, ty, step) = (_tile % Tiles, _tile / Tiles, _size / Tiles);
        var (culler, shader, terrain) = (game.frustumCuller, ShaderPrograms.Chunkshadowmap, TerrainOf(game));
        if (!NotNull(culler) || !NotNull(shader) || !NotNull(terrain)) return;
        var (rangeX, rangeZ, stage) = (culler.shadowRangeX, culler.shadowRangeZ, Stage(game));
        Cull(game, map, tx, ty);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, Buffers[1 - _front]);
        GL.Viewport(0, 0, _size, _size);
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(tx * step, ty * step, step, step);
        GL.DepthMask(true);
        // RenderShadow culls as far pass in that stage; Vulkan takes the tile's clear and draws in it
        Stage(game) = EnumRenderStage.ShadowFar;
        try
        {
            GL.Clear(ClearBufferMask.DepthBufferBit);
            shader.Use();
            var clip = Transform(map, Cam);
            for (var i = 0; i < 16; i++) Mvp[i] = (float)clip[i];
            shader.MvpMatrix = Mvp;
            Drawing = true;
            terrain.RenderShadow(dt);
        }
        finally
        {
            (Stage(game), Drawing) = (stage, false);
            shader.Stop();
            GL.Disable(EnableCap.ScissorTest);
            (culler.shadowRangeX, culler.shadowRangeZ) = (rangeX, rangeZ);
            // as the engine leaves its own shadow pass
            game.Platform.UnloadFrameBuffer(EnumFrameBuffer.ShadowmapFar);
            game.Platform.LoadFrameBuffer(EnumFrameBuffer.Primary);
        }

        Tiled(map);
    }

    private static void Cull(ClientMain game, Map map, int tx, int ty)
    {
        var (r, part) = (map.Radius, 2 * map.Radius / Tiles);
        _ = Mat4d.Ortho(Part, -r + tx * part, -r + (tx + 1) * part, -r + ty * part, -r + (ty + 1) * part, -map.Depth,
            map.Depth);
        for (var i = 0; i < 3; i++) Eye[i] = map.Center[i] + map.Direction[i];
        _ = Mat4d.LookAt(World, Eye, map.Center, Math.Abs(map.Direction[1]) > 0.999 ? North : Up);
        (game.frustumCuller.shadowRangeX, game.frustumCuller.shadowRangeZ) = (r, r);
        game.frustumCuller.CalcFrustumEquations(game.EntityPlayer.Pos.AsBlockPos, Part, World);
        _ = Assert(Finite(part)) && Assert(r > 0);
    }

    private static void Tiled(Map map)
    {
        if (++_tile < Tiles * Tiles) return;
        (_tile, map.Valid, _front) = (-1, true, 1 - _front);
        _ = Assert(Maps[_front] == map) && Assert(_version < int.MaxValue) && Assert(_maps < int.MaxValue);
        (_version, _maps) = (_version + 1, _maps + 1);
    }

    internal static void Use(ShaderProgramBase __instance)
    {
        if (!_injected || !NotNull(__instance) || Programs.Count > MaxPrograms) return;
        if (!Programs.TryGetValue(__instance.ProgramId, out var p))
            Programs[__instance.ProgramId] = p = Locate(__instance);
        if (p.SamplerAt < 0 || p.Version == _version) return;
        GL.UniformMatrix4(p.MatrixAt, 1, false, Matrix);
        GL.Uniform1(p.OnAt, _on);
        GL.Uniform2(p.TexelAt, 1f / Math.Max(1, _size), (float)(Bias / (2 * Maps[_front].Depth)));
        GL.Uniform1(p.SamplerAt, Unit);
        p.Version = _version;
        if (_bound == _version) return;
        GL.ActiveTexture(TextureUnit.Texture0 + Unit);
        GL.BindTexture(TextureTarget.Texture2D, Textures[_front]);
        GL.ActiveTexture(TextureUnit.Texture0); // engine code binds to unit 0 without selecting it
        _bound = _version;
    }

    // For a non-GL reader (VulkanRenderer); version changes per redraw, not on matrix moves alone
    internal static bool Published(out int texture, out int version, out float[] matrix, out float[] onTexel)
    {
        (texture, version, matrix, onTexel) = (Textures[_front], _maps, Matrix, []);
        if (!_injected || texture == 0 || !Assert(_front is 0 or 1)) return false;
        (OnTexel[0], OnTexel[1], OnTexel[2], onTexel) =
            (_on, 1f / Math.Max(1, _size), (float)(Bias / (2 * Maps[_front].Depth)), OnTexel); // called every frame
        return Assert(matrix.Length == 16);
    }

    private static Program Locate(ShaderProgramBase program)
    {
        var id = program.ProgramId;
        var p = new Program
        {
            MatrixAt = GL.GetUniformLocation(id, "kometDistantMatrix"),
            OnAt = GL.GetUniformLocation(id, "kometDistantOn"),
            SamplerAt = GL.GetUniformLocation(id, "kometDistantMap"),
            TexelAt = GL.GetUniformLocation(id, "kometDistantTexel")
        };
        _ = Assert(id > 0) && NotNull(p);
        return p;
    }

    private static bool Ready(int size)
    {
        if (_size == size && Textures[0] != 0) return true;
        Forget();
        for (var i = 0; i < 2; i++)
        {
            Textures[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, Textures[i]);
            // 32-bit float like the engine's maps on Mesa, copyable by Vulkan
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, size, size, 0,
                PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
            Parameters();
            Buffers[i] = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Buffers[i]);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, Textures[i], 0);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("the distant shadow framebuffer is incomplete");
            GL.Clear(ClearBufferMask.DepthBufferBit);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        (_size, _front, _tile, Maps[0], Maps[1]) = (size, 0, -1, new Map(), new Map());
        return Assert(Textures[1] != 0) && Assert(Buffers[1] != 0);
    }

    // As the engine's shadow maps
    private static void Parameters()
    {
        var target = TextureTarget.Texture2D;
        GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(target, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(target, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        GL.TexParameter(target, TextureParameterName.TextureBorderColor, [1f, 1f, 1f, 1f]);
        GL.TexParameter(target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        _ = Assert(Textures[0] != 0 || Textures[1] != 0);
    }

    internal static void Forget()
    {
        for (var i = 0; i < 2; i++)
        {
            if (Textures[i] != 0) GL.DeleteTexture(Textures[i]);
            if (Buffers[i] != 0) GL.DeleteFramebuffer(Buffers[i]);
            (Textures[i], Buffers[i]) = (0, 0);
        }

        (_size, _tile, _on) = (0, -1, 0);
        _ = Assert(Textures[0] == 0) && Assert(_version < int.MaxValue) && Assert(_maps < int.MaxValue);
        (_version, _maps) = (_version + 1, _maps + 1);
    }

    internal sealed class Map
    {
        public readonly double[] Direction = new double[3], Center = new double[3];
        public readonly double[] View = Mat4d.Create(), Projection = Mat4d.Create();
        public double Radius, Depth;
        public int Size;
        public long Drawn;
        public bool Valid;
    }

    private sealed class Program
    {
        public int MatrixAt, OnAt, SamplerAt, TexelAt, Version;
    }
}
