using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Komet.Rendering;

namespace Komet.Shapes;

// Clutter block entities (BEBehaviorShapeFromAttributes) keep their mesh copy for life after the pools copied it:
// ~77 MB of 718 MB promoted in 40 s of a join (2026-10-08). Heights are random, so copies cannot be shared.
// Where the mesh was built during this tesselation (loadMeshDuringTesselation), it is handed back afterwards: a
// non-null empty placeholder (so FromTreeAttributes does not relight) and the flag set again. Its Clone writes into the
// thread's scratch (one per mesh: LOD2 too), held only until the pools copied it.
internal static class ClutterMeshes
{
    private const string Behavior = "Vintagestory.GameContent.BEBehaviorShapeFromAttributes";
    private const string Props = "Vintagestory.GameContent.IShapeTypeProps";
    private const string Shelf = "Vintagestory.GameContent.BEBehaviorClutterBookshelf"; // overrides createMesh without calling it

    private static readonly MeshData Placeholder = new(4, 6);

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    private const int MaxInstructions = 1024, Scratches = 2; // mesh and lod2mesh

    [ThreadStatic] private static bool _building; // inside a tesselation that builds the mesh, on this thread
    [ThreadStatic] private static MeshData?[]? _scratch;
    [ThreadStatic] private static int _taken; // scratches handed out in this tesselation

    public static void Install(Harmony harmony)
    {
        Patched = false;
        if (!NotNull(harmony) || !Assert(Placeholder.VerticesCount == 0) || Target() is not { } target) return;
        Patched = NotNull(harmony.Patch(target, new HarmonyMethod(Built), finalizer: new HarmonyMethod(Drawn)));
        var rewrite = new HarmonyMethod(Rewrite);
        foreach (var behavior in (string[])[Behavior, Shelf])
            if (Patched && Create(behavior) is { } create) _ = NotNull(harmony.Patch(create, transpiler: rewrite));
    }

    internal static MethodInfo? Create(string behavior)
    {
        var method = AccessTools.TypeByName(behavior) is { } type && AccessTools.TypeByName(Props) is { } props
            ? AccessTools.DeclaredMethod(type, "createMesh", [props, typeof(bool)])
            : null;
        return method is null || Assert(method.ReturnType == typeof(MeshData)) ? method : null;
    }

    // Every MeshData.Clone() in createMesh, else the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (clone, copy) = (AccessTools.DeclaredMethod(typeof(MeshData), nameof(MeshData.Clone), []),
            AccessTools.Method(typeof(ClutterMeshes), nameof(Copy)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(clone) || !NotNull(copy)) return code;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].Calls(clone) && !Il.Substitute(code, i, copy))
                return code;
        return code;
    }

    // source.Clone() into the thread's scratch; arrays may exceed the counts, extra data via CloneExtraData
    // (MeshRecycle)
    internal static MeshData Copy(MeshData source)
    {
        if (!_building || _taken >= Scratches || !Enabled || !MeshRecycle.Enabled || !MeshRecycle.Patched || !NotNull(source) ||
            source.xyz is null || source.Indices is null)
            return source.Clone();
        var mesh = (_scratch ??= new MeshData?[Scratches])[_taken++] ??= new MeshData(initialiseArrays: false) { Recyclable = true };
        (mesh.VerticesPerFace, mesh.IndicesPerFace) = (source.VerticesPerFace, source.IndicesPerFace);
        mesh.SetVerticesCount(source.VerticesCount);
        mesh.xyz = Into(mesh.xyz, source.xyz, source.XyzCount);
        mesh.Uv = source.Uv is null ? null : Into(mesh.Uv, source.Uv, source.UvCount);
        mesh.Rgba = source.Rgba is null ? null : Into(mesh.Rgba, source.Rgba, source.RgbaCount);
        mesh.Flags = source.Flags is null ? null : Into(mesh.Flags, source.Flags, source.FlagsCount);
        mesh.Indices = Into(mesh.Indices, source.Indices, source.IndicesCount);
        mesh.SetIndicesCount(source.IndicesCount);
        (mesh.VerticesMax, mesh.IndicesMax) = (source.VerticesCount, mesh.Indices.Length);
        // a fresh clone's counts where the source lacks the array; CloneExtraData sets the others
        (mesh.NormalsCount, mesh.XyzFacesCount, mesh.TextureIndicesCount, mesh.ColorMapIdsCount, mesh.RenderPassCount) =
            (0, 0, 0, 0, 0);
        Extra(source, mesh);
        return Assert(mesh.VerticesCount == source.VerticesCount) ? mesh : source.Clone();
    }

    private static T[] Into<T>(T[]? kept, T[] source, int count)
    {
        if (!Assert(count >= 0)) return (T[])source.Clone();
        var target = kept is not null && kept.Length >= count ? kept : new T[Math.Max(count, source.Length)];
        Array.Copy(source, target, Math.Min(count, source.Length));
        return Assert(count <= target.Length) ? target : (T[])source.Clone();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CloneExtraData")]
    private static extern void Extra(MeshData source, MeshData dest);

    // OnTesselation as BEBehaviorShapeFromAttributes declares it, with the three fields the patches touch
    internal static MethodInfo? Target()
    {
        var type = AccessTools.TypeByName(Behavior);
        if (!NotNull(type)) return null; // the survival mod is part of the game
        if (AccessTools.DeclaredField(type, "mesh")?.FieldType != typeof(MeshData) ||
            AccessTools.DeclaredField(type, "lod2mesh")?.FieldType != typeof(MeshData) ||
            AccessTools.DeclaredField(type, "loadMeshDuringTesselation")?.FieldType != typeof(bool)) return null;
        var method = AccessTools.DeclaredMethod(type, "OnTesselation", [typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);
        return method is null || Assert(method.ReturnType == typeof(bool)) ? method : null;
    }

    // Whether this tesselation builds the mesh off the main thread
    private static void Built(bool ___loadMeshDuringTesselation, out bool __state)
    {
        _ = NotNull(Placeholder); // the flag as the engine left it: set where loadMesh runs during this tesselation
        __state = Enabled && ___loadMeshDuringTesselation;
        (_building, _taken) = (__state, 0);
    }

    // Hands the mesh back once the pools have their copy; a finalizer, so a throw leaves no scratch on a block entity
    private static Exception? Drawn(Exception? __exception, ref MeshData? ___mesh, ref MeshData? ___lod2mesh,
        ref bool ___loadMeshDuringTesselation, bool __state)
    {
        _building = false;
        if (!__state || ___mesh is null || ReferenceEquals(___mesh, Placeholder)) return __exception;
        _ = Assert(!ReferenceEquals(___lod2mesh, Placeholder));
        (___mesh, ___lod2mesh) = (Placeholder, null);
        ___loadMeshDuringTesselation = true;
        _ = Assert(Placeholder.VerticesCount == 0); // nothing ever adds to it: drawn by mistake it draws nothing
        return __exception;
    }
}
