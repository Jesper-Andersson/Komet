using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;

namespace Komet.World;

// Survival block entities redo per instance what is the same per kind; kept once instead:
// - Ignitable: BlockEntityGroundStorage.UpdateIgnitable parses the whole properties JSON per Initialize for one number,
//   kept per string, only indexed and read.
// - Meshes: BlockEntityFirepit.getOrCreateMesh never fills its "firepit-meshes" cache; kept by block id and state,
//   after the engine's dictionary.
// - Pot shapes: PotInFirepitRenderer parses pot and lid per pot; tessellation never writes a shape, so once per file.
// GPU meshes are never shared. Shape/texture reload and world leave empty every cache.
internal static class BlockEntityCaches
{
    // EngineShape of each part's Seams() in Vintage Story 1.22.7
    internal const ulong IgnitableFingerprint = 0x34197668D3D251E9UL, MeshFingerprint = 0x8820BC0184089CB8UL,
        PotFingerprint = 0xF8D80DAFDEC67646UL;

    internal const int Ignitable = 0, Meshes = 1, Pots = 2;
    private const int Parts = 3;
    private const int MaxInstructions = 512, MaxMeshes = 4096, MaxShapes = 256, PotTries = 2;
    private const string Firepit = "Vintagestory.GameContent.BlockEntityFirepit",
        GroundStorage = "Vintagestory.GameContent.BlockEntityGroundStorage",
        PotRenderer = "Vintagestory.GameContent.PotInFirepitRenderer",
        MeshCache = "firepit-meshes", FirepitShapes = "shapes/block/wood/firepit/";

    private static readonly ConditionalWeakTable<string, JsonObject> Parsed = [];
    private static readonly ConcurrentDictionary<(int Block, string State), MeshData> Built = new();
    private static int _built; // Built's count: ConcurrentDictionary.Count takes every bucket lock, and firepits mesh on several threads
    private static readonly ConcurrentDictionary<string, Shape> PotShapes = new(StringComparer.Ordinal);
    private static readonly bool[] Shaped = new bool[Parts], Foreign = new bool[Parts], Rewritten = new bool[Parts];
    private static readonly MethodBase?[][] Bodies = new MethodBase?[Parts][];
    private static readonly CreateCachableObjectDelegate<Dictionary<string, MeshData>> NewCache = static () => [];

    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;

    // Survival types present / bodies verified / a part another mod patches stands down
    internal static bool Survival { get; private set; }
    internal static bool Matched => Array.TrueForAll(Shaped, shaped => shaped);
    internal static bool Blocked => Array.Exists(Foreign, foreign => foreign);

    public static void Install(Harmony harmony, ICoreClientAPI? api, ILogger? logger)
    {
        (_logger, Survival) = (logger, AccessTools.TypeByName(Firepit) is not null);
        Clear();
        Array.Clear(Shaped);
        Array.Clear(Foreign);
        Array.Clear(Rewritten);
        for (var part = 0; part < Parts; part++) Bodies[part] = Seams(part);
        if (!NotNull(harmony) || !Survival) return;
        Shaped[Ignitable] = EngineShape.Matches(Bodies[Ignitable], IgnitableFingerprint, nameof(BlockEntityCaches), logger);
        Shaped[Meshes] = EngineShape.Matches(Bodies[Meshes], MeshFingerprint, nameof(BlockEntityCaches), logger);
        Shaped[Pots] = EngineShape.Matches(Bodies[Pots], PotFingerprint, nameof(BlockEntityCaches), logger);
        if (Shaped[Ignitable]) _ = NotNull(harmony.Patch(Bodies[Ignitable][0], transpiler: new(Method(nameof(RewriteIgnitable)))));
        if (Shaped[Meshes] && Bodies[Meshes] is [{ } mesh] && Method(nameof(Mesh)) is { } prefix && Assert(Il.Binds(mesh, prefix)))
        {
            _ = NotNull(harmony.Patch(mesh, new HarmonyMethod(prefix)));
            Rewritten[Meshes] = true;
        }

        if (Shaped[Pots]) _ = NotNull(harmony.Patch(Bodies[Pots][0], transpiler: new(Method(nameof(RewritePots)))));
        if (api?.Event is { } events)
        {
            events.ReloadShapes += Clear;
            events.ReloadTextures += Clear;
        }

        Recheck();
    }

    // A foreign transpiler/infix on a rewritten body, or a patch on what a kept result skips, stands that part down
    internal static void Recheck()
    {
        var was = Blocked;
        var tryGet = TryGet();
        var tesselate = AccessTools.DeclaredMethod(typeof(ShapeTesselator), nameof(ShapeTesselator.TesselateShape),
        [
            typeof(CollectibleObject), typeof(Shape), typeof(MeshData).MakeByRefType(), typeof(Vec3f), typeof(int?),
            typeof(string[])
        ]);
        var own = typeof(BlockEntityCaches);
        Foreign[Ignitable] = Rewritten[Ignitable] && EngineShape.Foreign(Bodies[Ignitable], PatchKinds.Body, null, own);
        // A postfix editing the result in place would re-edit the one kept mesh on every call
        Foreign[Meshes] = Rewritten[Meshes] &&
                          (EngineShape.Foreign(Bodies[Meshes], PatchKinds.All, null, own) ||
                           EngineShape.Foreign([tryGet, tesselate], PatchKinds.All, null, own));
        Foreign[Pots] = Rewritten[Pots] && (EngineShape.Foreign(Bodies[Pots], PatchKinds.Body, null, own) ||
                                            EngineShape.Foreign([tryGet], PatchKinds.All, null, own));
        _ = EngineShape.Report(_logger, nameof(BlockEntityCaches), was, Blocked);
        _ = Assert(Foreign.Length == Parts) && Assert(!Blocked || Array.Exists(Rewritten, rewritten => rewritten));
    }

    // World leave, shape or texture reload: a kept mesh holds the atlas positions of its time
    public static void Clear()
    {
        Parsed.Clear();
        Built.Clear();
        Volatile.Write(ref _built, 0);
        PotShapes.Clear();
        _ = Assert(Built.IsEmpty) && Assert(PotShapes.IsEmpty);
    }

    private static bool On(int part) => Index(part, Parts) && Enabled && Rewritten[part] && !Foreign[part];

    private static MethodInfo? Method(string name)
    {
        var method = AccessTools.DeclaredMethod(typeof(BlockEntityCaches), name);
        _ = NotNull(method) && Assert(method.IsStatic);
        return method;
    }

    internal static MethodBase?[] Seams(int part)
    {
        var (firepit, storage, renderer) =
            (AccessTools.TypeByName(Firepit), AccessTools.TypeByName(GroundStorage), AccessTools.TypeByName(PotRenderer));
        MethodBase?[] seams = part switch
        {
            Ignitable => [storage is null ? null : AccessTools.DeclaredMethod(storage, "UpdateIgnitable", Type.EmptyTypes)],
            Meshes => [firepit is null ? null : AccessTools.DeclaredMethod(firepit, "getOrCreateMesh", [typeof(string), typeof(string)])],
            _ => [renderer is null
                ? null
                : AccessTools.DeclaredConstructor(renderer, [typeof(ICoreClientAPI), typeof(ItemStack), typeof(BlockPos), typeof(bool)])]
        };
        return Index(part, Parts) && Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    private static MethodInfo? TryGet() =>
        AccessTools.DeclaredMethod(typeof(Shape), nameof(Shape.TryGet), [typeof(ICoreAPI), typeof(string)]);

    // UpdateIgnitable's one JsonObject.FromJson becomes Parse (result only indexed); else the engine's IL
    internal static List<CodeInstruction> RewriteIgnitable(IEnumerable<CodeInstruction> instructions)
    {
        var (from, parse) = (AccessTools.DeclaredMethod(typeof(JsonObject), nameof(JsonObject.FromJson), [typeof(string)]),
            Method(nameof(Parse)));
        if (!Il.Take(instructions, MaxInstructions, out var code) || !NotNull(from) || !NotNull(parse)) return code;
        var at = Il.Single(code, c => c.Calls(from));
        if (at < 0 || Il.Consumer(code, at) is not (var use, 0) || !Index(use, code.Count) ||
            code[use].operand is not MethodInfo { Name: "get_Item" } index || index.DeclaringType != typeof(JsonObject)) return code;
        Rewritten[Ignitable] = Il.Substitute(code, at, parse);
        return code;
    }

    internal static JsonObject Parse(string jsonCode)
    {
        if (jsonCode is null || !On(Ignitable)) return JsonObject.FromJson(jsonCode!); // null throws, as in the engine
        if (Parsed.TryGetValue(jsonCode, out var known)) return known;
        var parsed = JsonObject.FromJson(jsonCode);
        Parsed.AddOrUpdate(jsonCode, parsed);
        _ = Assert(Parsed.TryGetValue(jsonCode, out _));
        return parsed;
    }

    // In place of getOrCreateMesh: the engine's body, with what it tessellates kept by block id and state
    internal static bool Mesh(BlockEntity __instance, string burnstate, string contentstate, ref MeshData? __result)
    {
        if (!On(Meshes) || __instance?.Api is not ICoreClientAPI api || __instance.Pos is not { } pos) return true;
        var engine = ObjectCacheUtil.GetOrCreate(api, MeshCache, NewCache);
        var state = burnstate + "-" + contentstate;
        if (engine.TryGetValue(state, out var stored))
        {
            __result = stored;
            return false;
        }

        var block = api.World.BlockAccessor.GetBlock(pos);
        if (block.BlockId == 0)
        {
            __result = null;
            return false;
        }

        if (!Built.TryGetValue((block.BlockId, state), out var mesh))
        {
            api.Tesselator.TesselateShape(block, Shape.TryGet(api, FirepitShapes + state + ".json"), out mesh);
            if (mesh is not null && Assert(Volatile.Read(ref _built) < MaxMeshes))
            {
                var kept = Built.GetOrAdd((block.BlockId, state), mesh);
                if (ReferenceEquals(kept, mesh)) _ = Interlocked.Increment(ref _built);
                mesh = kept;
            }
        }

        __result = mesh;
        _ = Assert(Volatile.Read(ref _built) <= MaxMeshes);
        return false;
    }

    // The constructor's two Shape.TryGet calls feeding TesselateShape become PotShape; else the engine's IL
    internal static List<CodeInstruction> RewritePots(IEnumerable<CodeInstruction> instructions)
    {
        var (tryGet, kept) = (TryGet(), Method(nameof(PotShape)));
        if (!Il.Take(instructions, MaxInstructions, out var code) || !NotNull(tryGet) || !NotNull(kept) ||
            Il.Count(code, c => c.Calls(tryGet)) != PotTries) return code;
        var sites = new List<int>(PotTries);
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (!code[i].Calls(tryGet)) continue;
            if (Il.Consumer(code, i) is not (var use, 2) || !Index(use, code.Count) ||
                code[use].operand is not MethodInfo { Name: "TesselateShape" })
                return code;
            sites.Add(i);
        }

        var all = true;
        foreach (var site in sites.Bounded(PotTries)) all &= Il.Substitute(code, site, kept);
        Rewritten[Pots] = all && Assert(sites.Count == PotTries);
        return code;
    }

    internal static Shape? PotShape(ICoreAPI api, string shapePath)
    {
        if (!On(Pots) || shapePath is null) return Shape.TryGet(api, shapePath);
        if (PotShapes.TryGetValue(shapePath, out var known))
        {
            ShapeElement.locationForLogging = shapePath; // as the engine's parse leaves it
            return known;
        }

        var shape = Shape.TryGet(api, shapePath);
        if (shape is not null && Assert(PotShapes.Count < MaxShapes)) _ = PotShapes.TryAdd(shapePath, shape);
        _ = Assert(PotShapes.Count <= MaxShapes);
        return shape;
    }
}
