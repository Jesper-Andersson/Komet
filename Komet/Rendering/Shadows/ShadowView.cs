using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// Engine shadow maps centre on the camera whatever it faces, so most is shadow nothing in view receives. The engine
// makes the camera view only after the shadow passes; the same planes (projection under the pass's own, camera matrix)
// are made at each pass start and compared at RenderOpaque's start; a difference turns caster culling off for good. A
// caster outside a view plane (moved out by Reach) the light does not cross inward casts into nothing in view.
internal static class ShadowView
{
    public const double Reach = 8; // blocks the view's planes move out: filtering, bias, wind and meshes past their box
    public const int MaxCasters = 5; // near, left, right, top, bottom: no far plane
    private static readonly FrustumCulling View = new();
    private static readonly double[] ViewMatrix = Mat4d.Create();
    private static ICoreClientAPI? _api;
    private static ILogger? _logger;
    private static bool _castFrame;

    // This shadow pass's casters may be culled by the view
    public static bool Casting { get; private set; }

    // Last shadow pass's terrain casters were culled, not yet taken: EntityCasters, same stage
    private static bool _passed;

    // Planes once differed from the engine's: view culling off for good
    public static bool Differed { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "values")]
    internal static extern ref double[][] Matrices(StackMatrix4 stack);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CalcFrustumEquations")]
    private static extern void Equations(FrustumCulling culler, double[] matrix);

    // Prefixes run first: OcclusionCulling's read Casting
    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        Clear();
        var shadow = AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderShadow), [typeof(float)]);
        var opaque = AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque), [typeof(float)]);
        if (!NotNull(harmony) || !NotNull(api) || !NotNull(shadow) || !NotNull(opaque)) return;
        (_api, _logger) = (api, logger);
        _ = NotNull(harmony.Patch(shadow, new HarmonyMethod(Opening) { priority = Priority.First },
            new HarmonyMethod(Closing)));
        _ = NotNull(harmony.Patch(opaque, new HarmonyMethod(Checked) { priority = Priority.First }));
    }

    public static void Clear()
    {
        (_api, _logger, _castFrame, Casting, Differed, _passed) = (null, null, false, false, false, false);
        _ = Assert(!Casting) && Assert(!Differed);
    }

    // RenderShadow's start. DistantShadows draws what the engine maps leave out, for every view.
    internal static void Opening()
    {
        Casting = !Differed && !DistantShadows.Drawing && Unwarped(_api) && Viewed();
        _castFrame |= Casting;
        _ = Assert(!Casting || !Differed);
    }

    internal static void Closing() => (_passed, Casting) = (Casting, false);

    public static bool Take()
    {
        var passed = _passed;
        _passed = false;
        _ = Assert(!passed || !Differed);
        return passed;
    }

    // This frame's view as the engine will make it: perspective under the pass's projection, camera matrix (from
    // Before)
    private static bool Viewed()
    {
        if (_api?.World is not ClientMain { PMatrix: { Count: >= 2 } projections, MainCamera.CameraMatrix: { Length: 16 } camera })
            return false;
        var projection = Matrices(projections)[projections.Count - 2];
        if (!NotNull(projection) || projection.Length != 16) return false;
        _ = Mat4d.Multiply(ViewMatrix, projection, camera);
        Equations(View, ViewMatrix);
        return Assert(Planes(View).Length == 6);
    }

    // RenderOpaque's start: a frame with view-culled casters checks the view was the engine's
    internal static void Checked()
    {
        if (!_castFrame || _api?.World is not ClientMain { frustumCuller: { } culler }) return;
        _castFrame = false;
        var (engine, view) = (Planes(culler), Planes(View));
        if (!NotNull(engine) || engine.Length < 6) return;
        for (var p = 0; p < MaxCasters && !Differed; p++) Differed = !Same(engine[p], view[p]);
        if (Differed)
            _logger?.Warning("Komet: the view worked out before the shadow passes was not the engine's; shadow casters are " +
                             "no longer culled by it");
    }

    private static bool Same(Plane a, Plane b) => Bits(a.normalX) == Bits(b.normalX) && Bits(a.normalY) == Bits(b.normalY) &&
                                                  Bits(a.normalZ) == Bits(b.normalZ) && Bits(a.D) == Bits(b.D);

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    // Shadow pass moves no vertex far (wind only): no world warp or perception effect
    internal static bool Unwarped(ICoreClientAPI? api) => api?.Render?.ShaderUniforms is { } u && u.GlitchWaviness <= 0 &&
                                                          u.GlobalWorldWarp <= 0 &&
                                                          (u.PerceptionEffectId == 1 || u.PerceptionEffectIntensity <= 0);

    // light: the shadow pass's near plane, facing along it. Returns planes written.
    public static int Casters(Plane light, Span<Plane> into)
    {
        var planes = Planes(View);
        if (!Assert(into.Length >= MaxCasters) || !Assert(planes.Length == 6)) return 0;
        var count = 0;
        for (var p = 0; p < MaxCasters; p++)
        {
            var v = planes[p];
            if (v.normalX * light.normalX + v.normalY * light.normalY + v.normalZ * light.normalZ > 0) continue;
            into[count++] = new Plane { normalX = v.normalX, normalY = v.normalY, normalZ = v.normalZ, D = v.D + Reach };
        }

        return count;
    }

    // Near, left, right, top, bottom planes moved out by Reach, as rows.comp's Frustum block takes them
    public static void Moved(Span<double> into)
    {
        var planes = Planes(View);
        if (!Assert(into.Length >= 4 * MaxCasters) || !Assert(planes.Length == 6)) return;
        for (var p = 0; p < MaxCasters; p++)
            (into[4 * p], into[4 * p + 1], into[4 * p + 2], into[4 * p + 3]) =
                (planes[p].normalX, planes[p].normalY, planes[p].normalZ, planes[p].D + Reach);
    }

    // Tests: the given view (null: none), as a pass start would make it
    internal static void Use(FrustumCulling? view)
    {
        if (view is not null) Planes(view).AsSpan(0, 6).CopyTo(Planes(View));
        Casting = view is not null;
        _ = Assert(Casting == view is not null);
    }
}
