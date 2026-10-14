using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// One kept shadow map (FarShadowReuse, NearShadowReuse): its matrix, camera position, light and view when drawn, and
// whether terrain in its square changed since. The terrain watchers are patched once for both maps.
internal sealed class ShadowReuse(ShadowReuse.Pass pass)
{
    // Stage and Done: the pass and its end, left out while the map holds. Matrix: the engine's field, Publish: its uniform.
    // A view value i differs too much past MaxTurn (the rotation, i < Rotation) or MaxLens (the projection).
    internal sealed record Pass(EnumRenderStage Stage, EnumRenderStage Done, EnumFrameBuffer Target,
        System.Func<ClientMain, float[]> Matrix, System.Action<ClientMain, float[]> Publish, double MaxAgeSeconds, double MaxMove,
        double MaxTurn, double MaxLens);

    private const int Views = 25, Rotation = 9; // the camera's rotation (9) and projection (16)
    private const int MaxRenderers = 8192; // an animated block entity registers one
    private static readonly double MinLightDot = Math.Cos(0.05 * Math.PI / 180);
    private static readonly ShadowReuse?[] Watching = new ShadowReuse?[2];
    private static Harmony? _watched;

    private readonly long _maxAge = (long)(pass.MaxAgeSeconds * Stopwatch.Frequency);
    private readonly float[] _kept = new float[16];
    private readonly double[] _keptLight = new double[3], _keptView = new double[Views];

    // Own's answer per stage list, while the list's version stands (renderers register and leave by Add/Remove)
    private readonly (List<RenderHandler>? List, int Version, bool Own)[] _owned = new (List<RenderHandler>?, int, bool)[2];
    private double _x, _y, _z;
    private long _drawn;
    private FrameBufferRef? _target;
    private bool _valid, _changed, _skipping;

    public bool Enabled
    {
        get;
        set
        {
            field = value;
            _valid = false;
        }
    } = true;

    public bool Changed => _changed;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
    private static extern ref int ListVersion(List<RenderHandler> list);

    // The terrain watchers, once per Harmony (a world's): a failed patch leaves nothing kept
    public static bool Watch(Harmony harmony, ShadowReuse map, int slot)
    {
        if (!NotNull(harmony) || !NotNull(map) || !Index(slot, Watching.Length)) return false;
        Watching[slot] = map;
        if (ReferenceEquals(_watched, harmony)) return true;
        var (add, remove) = (AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.TryAdd)),
            AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation)));
        var hide = AccessTools.DeclaredMethod(typeof(ClientChunk), nameof(ClientChunk.SetVisibility), [typeof(bool)]);
        if (!NotNull(add) || !NotNull(remove) || !NotNull(hide)) return false;
        _ = NotNull(harmony.Patch(add, postfix: new HarmonyMethod(Touched)));
        _ = NotNull(harmony.Patch(remove, new HarmonyMethod(Removed)));
        _ = NotNull(harmony.Patch(hide, postfix: new HarmonyMethod(Hidden)));
        _watched = harmony;
        return true;
    }

    internal static void Removed(ModelDataPoolLocation location)
    {
        if (location is null || !Assert(Watching.Length == 2)) return;
        Touched(location.FrustumCullSphere);
    }

    internal static void Hidden()
    {
        _ = Assert(Watching.Length == 2) && Assert(_watched is not null || Watching[0] is null);
        if (Watching[0] is { } first) first._changed = true;
        if (Watching[1] is { } second) second._changed = true;
    }

    // MeshDataPool.TryAdd's postfix: Harmony binds the name
    internal static void Touched(Sphere? frustumCullSphere)
    {
        if (frustumCullSphere is not { } s || !Assert(Watching.Length == 2)) return;
        Watching[0]?.Touch(s);
        Watching[1]?.Touch(s);
    }

    public void Clear()
    {
        (_valid, _changed, _skipping, _target) = (false, false, false, null);
        _owned.AsSpan().Clear();
        _ = Assert(!_valid) && Assert(!_skipping) && Assert(_owned[0].List is null);
    }

    // TriggerRenderStage: false leaves the pass or its end out while the kept map holds
    public bool Staging(ClientMain game, EnumRenderStage stage)
    {
        if (stage == pass.Done && _skipping)
        {
            Stage(game) = stage;
            _skipping = false;
            return !Assert(stage == pass.Done);
        }

        if (stage != pass.Stage) return true;
        _skipping = Enabled && Reusable(game);
        if (!_skipping) return Assert(!_skipping);
        Stage(game) = stage;
        Follow(game);
        return !Assert(_valid); // false: left out
    }

    // The pass's postfix: what the map about to be drawn is kept with; false: not kept
    public bool Drawn(ClientMain? game)
    {
        if (!Enabled || game?.EntityPlayer is not { } player || !NotNull(pass.Matrix(game))) return false;
        Array.Copy(pass.Matrix(game), _kept, 16);
        (_x, _y, _z) = (player.CameraPos.X, player.CameraPos.Y, player.CameraPos.Z);
        DistantShadows.Light(game, _keptLight);
        (_target, _drawn, _changed) = (game.Platform.FrameBuffers[(int)pass.Target], Stopwatch.GetTimestamp(), false);
        _valid = View(game, _keptView, 1) && game.shUniforms.DropShadowIntensity > 0.01 && ShadowView.Unwarped(game.api) &&
                 Own(game, pass.Stage, 0) && Own(game, pass.Done, 1); // View: the pass's projection
        _ = Assert(_kept.Length == 16) && Assert(Finite(_x + _y + _z));
        return true;
    }

    // Drawn for this light, view and terrain, young enough, the camera near where it was and turned within the limits
    private bool Reusable(ClientMain game)
    {
        if (!_valid || game.EntityPlayer is not { } player || !ShadowView.Unwarped(game.api) ||
            !ReferenceEquals(_target, game.Platform.FrameBuffers[(int)pass.Target]) || _changed ||
            Stopwatch.GetTimestamp() - _drawn > _maxAge) return false;
        var (dx, dy, dz) = (player.CameraPos.X - _x, player.CameraPos.Y - _y, player.CameraPos.Z - _z);
        if (dx * dx + dy * dy + dz * dz > pass.MaxMove * pass.MaxMove) return false;
        Span<double> light = stackalloc double[3];
        DistantShadows.Light(game, light);
        if (light[0] * _keptLight[0] + light[1] * _keptLight[1] + light[2] * _keptLight[2] < MinLightDot) return false;
        Span<double> view = stackalloc double[Views];
        if (!View(game, view, 0)) return false;
        for (var i = 0; i < Views; i++)
            if (Math.Abs(view[i] - _keptView[i]) > (i < Rotation ? pass.MaxTurn : pass.MaxLens)) return false;
        return Assert(_valid);
    }

    // Kept matrix for this camera: camera-relative position now = kept one + camera move
    private void Follow(ClientMain game)
    {
        var (to, player) = (pass.Matrix(game), game.EntityPlayer);
        if (!NotNull(to) || !Assert(to.Length == 16) || player is null) return;
        _ = Mat4f.Translate(to, _kept, (float)(player.CameraPos.X - _x), (float)(player.CameraPos.Y - _y),
            (float)(player.CameraPos.Z - _z));
        pass.Publish(game, to);
    }

    // Only engine, its API, its mods and Komet draw in the pass, and only draw; a foreign renderer: skip nothing.
    // Asked again only when the stage's list changed.
    private bool Own(ClientMain game, EnumRenderStage stage, int slot)
    {
        if (game.eventManager?.renderersByStage is not { } lists || !Index((int)stage, lists.Length) ||
            !Index(slot, _owned.Length)) return false;
        if (lists[(int)stage] is not { } list) return false;
        var version = ListVersion(list);
        if (ReferenceEquals(_owned[slot].List, list) && _owned[slot].Version == version) return _owned[slot].Own;
        var own = list.Count <= MaxRenderers;
        var (engine, api) = (typeof(ClientMain).Assembly, typeof(DummyRenderer).Assembly);
        for (var i = 0; own && i < Math.Min(list.Count, MaxRenderers); i++)
        {
            var renderer = list[i]?.Renderer;
            var owner = renderer is DummyRenderer d ? d.action?.Method.DeclaringType?.Assembly : renderer?.GetType().Assembly;
            own = owner == engine || owner == api || owner == typeof(ShadowReuse).Assembly || Vanilla(owner);
        }

        _owned[slot] = (list, version, own);
        return Assert(ReferenceEquals(_owned[slot].List, list)) && own;
    }

    private static bool Vanilla(System.Reflection.Assembly? owner) =>
        owner?.FullName is { } name && Assert(name.Length > 0) && (name.StartsWith("VSSurvivalMod,", StringComparison.Ordinal) ||
                                                                 name.StartsWith("VSEssentials,", StringComparison.Ordinal) ||
                                                                 name.StartsWith("VSCreativeMod,", StringComparison.Ordinal));

    // Tests: a map kept with this matrix at this camera position
    public void Use(float[] kept, double x, double y, double z)
    {
        Array.Copy(kept, _kept, 16);
        (_x, _y, _z, _valid, _changed) = (x, y, z, true, false);
        _ = Assert(kept.Length == 16) && Assert(_valid);
    }

    // ShadowView's input: camera rotation and projection, `above` matrices below the stack top
    private static bool View(ClientMain game, Span<double> into, int above)
    {
        var (camera, stack) = (game.MainCamera?.CameraMatrix, game.PMatrix);
        if (camera is not { Length: 16 } || stack is null || stack.Count <= above || !Assert(into.Length == Views)) return false;
        var projection = ShadowView.Matrices(stack)[stack.Count - 1 - above];
        if (projection is not { Length: 16 }) return false;
        for (var i = 0; i < Rotation; i++) into[i] = camera[i / 3 * 4 + i % 3];
        projection.AsSpan().CopyTo(into[Rotation..]);
        return Assert(into.Length == Views);
    }

    // Sphere reaches into the map's square (depth ignored: anything along the light may cast)
    public void Touch(Sphere s)
    {
        if (!_valid || _changed || !Assert(_kept.Length == 16)) return;
        var (x, y, z) = (s.x - _x, s.y - _y, s.z - _z);
        for (var i = 0; i < 2; i++)
        {
            var at = _kept[i] * x + _kept[4 + i] * y + _kept[8 + i] * z + _kept[12 + i];
            var r = s.radius * Math.Sqrt(_kept[i] * _kept[i] + _kept[4 + i] * _kept[4 + i] + _kept[8 + i] * _kept[8 + i]);
            if (at < -r || at > 1 + r) return;
        }

        _changed = Assert(Finite(s.radius));
    }
}
