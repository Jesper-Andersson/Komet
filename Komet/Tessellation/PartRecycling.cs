using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// Tessellation garbage is per chunk part (survey 2026-10-07); a world-join trace had its threads 65 % in GC polls. Four
// sources go, results unchanged. Small parts: CloneUsingRecycler Clone()s meshes under 121 vertices (VerticesCount * 34
// < 4096) into exact arrays, garbage after upload; Clone takes its recycler branch for them too, same checks, values
// and counts. Dispose returns the mesh (Recycle, then DoRecycling at the next populate, whose FacePacking drain waits
// for packs reading it); the recycler keeps four per quad capacity. Only chunk parts change; a disabled recycler gets
// Clone(). Decors: NowProcessChunk's dictionary lives within the pass; the thread's cleared one keeps insertion order.
// Ocean corners: LoadOceanityCorners' float[4] is the thread's while BeginProcessChunk calls it. Upload: the
// AddCenter/EdgeToPools list goes only to AddToPools and ToArray. IL checks prove each object leaves only that way,
// else engine IL; a foreign patch on a method seeing a shared object makes its group allocate.
internal static class PartRecycling
{
    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Shape = 0x4F02F432D94CEABDUL;

    // First recycled vertex count: VerticesCount * BaseSizeInBytes >= MinimumSizeForRecycling
    private const int SmallParts = (MeshDataRecycler.MinimumSizeForRecycling + MeshData.BaseSizeInBytes - 1) /
                                   MeshData.BaseSizeInBytes;

    private const float VertexSlack = 1.05f, IndexSlack = 1.2f; // CloneUsingRecycler's waste limits
    private const int CloneSites = 4, CornerCount = 4, MaxDecors = 1 << 14, MaxLocations = 1 << 10, MaxTargets = 8;
    private const int PartsGroup = 0, DecorGroup = 1, OceanGroup = 2, ListGroup = 3, Groups = 4;
    private const int PartsBit = 1, DecorBit = 2, OceanBit = 4, CornersBit = 8, CenterBit = 16, EdgeBit = 32, AllBits = 63;

    private static readonly Type DecorMap = typeof(Dictionary<int, Block>), Locations = typeof(List<ModelDataPoolLocation>);

    private static readonly MethodInfo? Populate = Method(typeof(ChunkTesselator), "populateTesselatedChunkPart",
        typeof(MeshData[][][]), typeof(TesselatedChunkPart[]).MakeByRefType());

    private static readonly MethodInfo? CloneCall = Method(typeof(MeshData), nameof(MeshData.CloneUsingRecycler));
    private static readonly ConstructorInfo? DecorNew = AccessTools.Constructor(DecorMap, Type.EmptyTypes);
    private static readonly MethodInfo? Cull = Method(typeof(ChunkTesselator), "CullVisibleFacesWithDecor", DecorMap, DecorMap);

    private static readonly MethodInfo? Build = Method(typeof(ChunkTesselator), "BuildDecorPolygons", typeof(int), typeof(int),
        typeof(int), DecorMap, typeof(bool));

    private static readonly MethodInfo? LoadCorners = Method(typeof(ClientWorldMap),
        nameof(ClientWorldMap.LoadOceanityCorners), typeof(int), typeof(int));

    private static readonly MethodInfo? Lerp = Method(typeof(IntDataMap2D), nameof(IntDataMap2D.GetIntLerpedCorrectly),
        typeof(float), typeof(float));

    private static readonly ConstructorInfo? ListNew = AccessTools.Constructor(Locations, [typeof(int)]);
    private static readonly MethodInfo? ToArray = AccessTools.Method(Locations, nameof(List<>.ToArray));

    private static readonly MethodInfo? AddToPools = Method(typeof(TesselatedChunkPart), "AddToPools", typeof(ChunkRenderer),
        Locations, typeof(Vec3i), typeof(int), typeof(Sphere), typeof(Bools));

    // A branch on whether the value (operand 0) is null
    private static readonly System.Func<CodeInstruction, int, bool> Tested = static (c, operand) =>
        operand == 0 && (c.opcode == OpCodes.Brfalse || c.opcode == OpCodes.Brfalse_S || c.opcode == OpCodes.Brtrue ||
                         c.opcode == OpCodes.Brtrue_S);

    [ThreadStatic] private static Dictionary<int, Block>? _decors;
    [ThreadStatic] private static List<ModelDataPoolLocation>? _locations; // null while lent
    [ThreadStatic] private static float[]? _corners;
    [ThreadStatic] private static bool _armed; // the next float[4] LoadOceanityCorners makes on this thread is _corners

    private static (MethodBase?[] Bodies, MethodBase?[] Receivers)[] _seams = [];
    private static ILogger? _logger;
    private static int _rewritten, _foreign; // bits: the methods rewritten, the groups another mod patches
    private static bool _shaped;

    public static bool Enabled { get; set; } = true;

    public static bool Rewritten => _rewritten == AllBits;

    public static bool StoodDown => _foreign != 0;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CopyBasicData")]
    private static extern void CopyBasicData(MeshData source, MeshData dest);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CloneExtraData")]
    private static extern void CloneExtraData(MeshData source, MeshData dest);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "disposed")]
    internal static extern ref bool Disposed(MeshDataRecycler recycler);

    // The populate transpiler only for the bodies Clone was written against; the others check the IL they rewrite
    public static void Install(Harmony harmony, ILogger? logger = null, ulong shape = Shape)
    {
        (_rewritten, _foreign, _shaped, _logger, _seams) = (0, 0, false, logger, Seams());
        if (!NotNull(harmony) || !Assert(_seams.Length == Groups)) return;
        // the private members the accessors name: a renamed one would throw on the tessellation thread
        var mesh = typeof(MeshData);
        var accessible = Method(mesh, "CopyBasicData", mesh)?.ReturnType == typeof(void) &&
                         Method(mesh, "CloneExtraData", mesh)?.ReturnType == typeof(void) &&
                         AccessTools.DeclaredField(typeof(MeshDataRecycler), "disposed")?.FieldType == typeof(bool);
        if (!accessible) logger?.Warning("Komet PartRecycling: a MeshData member is missing, small parts are cloned as shipped");
        _shaped = accessible && EngineShape.Matches(Shaped(), shape, nameof(PartRecycling), logger);
        Recheck();
        var rewrite = new HarmonyMethod(AccessTools.Method(typeof(PartRecycling), nameof(Rewrite)));
        MethodBase?[] targets =
        [
            _shaped ? Populate : null, _seams[DecorGroup].Bodies[0], _seams[OceanGroup].Bodies[0], LoadCorners,
            .. _seams[ListGroup].Bodies
        ];
        foreach (var target in targets.Bounded(MaxTargets))
            if (target is not null)
                _ = NotNull(harmony.Patch(target, transpiler: rewrite));
        // the transpilers ran inside Patch
        if (Volatile.Read(ref _rewritten) != (_shaped ? AllBits : AllBits & ~PartsBit))
            logger?.Warning("Komet PartRecycling: an engine method no longer looks as expected, its garbage stays");
    }

    // Install, then LevelFinalize, when every mod has patched
    internal static void Recheck()
    {
        if (!Assert(_seams.Length == Groups)) return;
        var foreign = 0;
        for (var g = 0; g < Groups; g++)
            if (EngineShape.Foreign(_seams[g].Bodies, PatchKinds.Body, null) ||
                EngineShape.Foreign(_seams[g].Receivers, PatchKinds.All, null))
                foreign |= 1 << g;
        _ = EngineShape.Report(_logger, nameof(PartRecycling), _foreign != 0, foreign != 0);
        _foreign = foreign;
    }

    // Bodies Clone replaces for small parts: CloneUsingRecycler's checks, Clone(), its copy (CopyBasicData)
    internal static MethodBase?[] Shaped()
    {
        var mesh = typeof(MeshData);
        MethodBase?[] methods =
        [
            CloneCall, Method(mesh, nameof(MeshData.Clone)), Method(mesh, "CloneBasicData"),
            Method(mesh, "CopyBasicData", mesh)
        ];
        _ = Assert(methods.Length <= EngineShape.MaxMethods) && Assert(Array.IndexOf(methods, null) < 0);
        return methods;
    }

    // Per group: rewritten bodies (a transpiler there may keep the object), methods skipped or handed it (any patch)
    private static (MethodBase?[] Bodies, MethodBase?[] Receivers)[] Seams()
    {
        var (tesselator, chunk, i) = (typeof(ChunkTesselator), typeof(TesselatedChunk), typeof(int));
        Type[] upload = [typeof(ChunkRenderer), typeof(Vec3i), i, typeof(Sphere), typeof(ClientChunk)];
        Type[] store =
        [
            typeof(MeshDataPoolManager), Locations, typeof(MeshData), typeof(Vec3i), i, typeof(Sphere), typeof(Bools), i
        ];
        (MethodBase?[] Bodies, MethodBase?[] Receivers)[] seams =
        [
            ([Populate], Shaped()),
            ([Method(tesselator, nameof(ChunkTesselator.NowProcessChunk), i, i, i, chunk, typeof(bool))], [Cull, Build]),
            ([Method(tesselator, nameof(ChunkTesselator.BeginProcessChunk), i, i, i, typeof(ClientChunk), typeof(bool))],
                [LoadCorners]),
            ([Method(chunk, "AddCenterToPools", upload), Method(chunk, "AddEdgeToPools", upload)],
                [AddToPools, Method(typeof(TesselatedChunkPart), "AddModelAndStoreLocation", store)])
        ];
        // a missing body is another game version: EngineShape counts it as foreign, and Install patches what it finds
        _ = Assert(seams.Length == Groups) && Assert(!Array.Exists(seams, s => Array.IndexOf(s.Bodies, null) >= 0));
        return seams;
    }

    private static bool On(int group) => Enabled && Index(group, 31) && (_foreign & (1 << group)) == 0;

    // One transpiler for six methods, rerun on each new patch: a reshaped body keeps its IL and clears its bit
    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code)) || !NotNull(original)) return code;
        var (bit, done) = original.Name switch
        {
            "populateTesselatedChunkPart" => (PartsBit, RewriteParts(code)),
            nameof(ChunkTesselator.NowProcessChunk) => (DecorBit, RewriteDecors(code)),
            nameof(ChunkTesselator.BeginProcessChunk) => (OceanBit, RewriteOcean(code)),
            nameof(ClientWorldMap.LoadOceanityCorners) => (CornersBit, RewriteCorners(code)),
            "AddCenterToPools" => (CenterBit, RewriteLists(code)),
            _ => (EdgeBit, RewriteLists(code))
        };
        _rewritten = done ? _rewritten | bit : _rewritten & ~bit;
        _ = Assert(_rewritten is >= 0 and <= AllBits);
        return code;
    }

    // The four CloneUsingRecycler calls, one per LOD mesh of a part
    private static bool RewriteParts(List<CodeInstruction> code)
    {
        var (clone, own) = (CloneCall, AccessTools.Method(typeof(PartRecycling), nameof(Clone)));
        if (clone is null || !NotNull(own) || Il.Count(code, c => c.Calls(clone)) != CloneSites) return false;
        var sites = 0;
        for (var i = 0; i < Math.Min(code.Count, Il.MaxInstructions); i++)
            if (code[i].Calls(clone) && Il.Substitute(code, i, own))
                sites++;
        return Assert(sites == CloneSites);
    }

    // new Dictionary<int, Block>(): its local only null-tested and passed to the decor cull and polygon build
    private static bool RewriteDecors(List<CodeInstruction> code)
    {
        var (made, cull, build) = (DecorNew, Cull, Build);
        var own = AccessTools.Method(typeof(PartRecycling), nameof(Drawn));
        if (made is null || cull is null || build is null || !NotNull(own)) return false;
        var at = Il.Single(code, c => c.Is(OpCodes.Newobj, made));
        return at >= 0 && Kept(code, at + 1,
                   (c, operand) => (c.Calls(cull) && operand == 2) || (c.Calls(build) && operand == 4) || Tested(c, operand)) &&
               Il.Substitute(code, at, own);
    }

    // The LoadOceanityCorners call, whose array goes into a local that is only null-tested and read by index
    private static bool RewriteOcean(List<CodeInstruction> code)
    {
        var (load, own) = (LoadCorners, AccessTools.Method(typeof(PartRecycling), nameof(Ocean)));
        if (load is null || !NotNull(own)) return false;
        var at = Il.Single(code, c => c.Calls(load));
        return at >= 0 && Index(at + 1, code.Count) && code[at + 1].IsStloc() &&
               Il.Confined(code, at + 1,
                   static (c, operand) => (c.opcode == OpCodes.Ldelem_R4 && operand == 0) || Tested(c, operand)) &&
               Il.Substitute(code, at, own);
    }

    // LoadOceanityCorners' one newarr float32, which is filled and returned
    private static bool RewriteCorners(List<CodeInstruction> code)
    {
        var (lerp, own) = (Lerp, AccessTools.Method(typeof(PartRecycling), nameof(Corners)));
        if (lerp is null || !NotNull(own)) return false;
        var at = Il.Single(code, static c => c.opcode == OpCodes.Newarr && Equals(c.operand, typeof(float)));
        return at > 0 && Returned(code, at, lerp) && Il.Substitute(code, at, own);
    }

    // The list's constructor and its ToArray: the list goes only to AddToPools (as its list) and to that ToArray
    private static bool RewriteLists(List<CodeInstruction> code)
    {
        var (made, copy, parts) = (ListNew, ToArray, AddToPools);
        var (rent, drain) = (AccessTools.Method(typeof(PartRecycling), nameof(Rent)),
            AccessTools.Method(typeof(PartRecycling), nameof(Drain)));
        if (made is null || copy is null || parts is null || !NotNull(rent) || !NotNull(drain)) return false;
        var (at, array) = (Il.Single(code, c => c.Is(OpCodes.Newobj, made)), Il.Single(code, c => c.Calls(copy)));
        if (at < 0 || array < 1 || !Index(at + 1, code.Count) || !code[at + 1].IsStloc()) return false;
        var list = Il.Local(code[at + 1]);
        return list >= 0 && Il.Local(code[array - 1], Il.Uses.Load) == list &&
               Il.Confined(code, at + 1,
                   (c, operand) => (c.Calls(parts) && operand == 2) || (c.Calls(copy) && operand == 0)) &&
               Il.Substitute(code, at, rent) && Il.Substitute(code, array, drain);
    }

    // Il.Confined for a local that may also be set to null elsewhere (NowProcessChunk declares it null first)
    private static bool Kept(List<CodeInstruction> code, int store, System.Func<CodeInstruction, int, bool> allowed)
    {
        var local = Index(store, code.Count) && code[store].IsStloc() ? Il.Local(code[store]) : -1;
        if (local < 0 || !NotNull(allowed) || !Assert(code.Count <= Il.MaxInstructions)) return false;
        for (var i = 0; i < Math.Min(code.Count, Il.MaxInstructions); i++)
        {
            if (i == store || Il.Local(code[i]) != local) continue;
            if (Il.Local(code[i], Il.Uses.Store) == local)
            {
                if (i == 0 || code[i - 1].opcode != OpCodes.Ldnull || code[i].labels.Count > 0) return false;
                continue;
            }

            var (use, operand) = Il.Local(code[i], Il.Uses.Load) == local ? Il.Consumer(code, i) : (-1, -1);
            if (use < 0 || !allowed(code[use], operand)) return false;
        }

        return true;
    }

    // From newarr to ret the array is only dup'd and stored into: between, each instruction loads a local, argument or
    // constant, reads a lerped corner (floats in, float out), or is dup or stelem.r4; no jump targets
    private static bool Returned(List<CodeInstruction> code, int made, MethodInfo lerp)
    {
        if (!Index(made, code.Count) || !NotNull(lerp)) return false;
        for (var i = made + 1; i < Math.Min(code.Count, Il.MaxInstructions); i++)
        {
            var c = code[i];
            if (c.labels.Count > 0 || c.blocks.Count > 0) return false;
            if (c.opcode == OpCodes.Ret) return i > made + 1;
            if (c.opcode != OpCodes.Dup && c.opcode != OpCodes.Stelem_R4 && !c.Calls(lerp) && !c.LoadsConstant() &&
                !c.IsLdarg() && Il.Local(c, Il.Uses.Load) < 0) return false;
        }

        return false;
    }

    // Replaces CloneUsingRecycler() (null throws alike): a part Clone()d for its size alone takes the recycler branch
    private static MeshData Clone(MeshData mesh)
    {
        if (!_shaped || !On(PartsGroup) || mesh.VerticesCount is <= 0 or >= SmallParts ||
            mesh.Uv is null || mesh.Rgba is null || mesh.Flags is null ||
            mesh.VerticesPerFace != MeshData.StandardVerticesPerFace ||
            mesh.IndicesPerFace != MeshData.StandardIndicesPerFace || MeshData.Recycler is not { } recycler ||
            Disposed(recycler)) return mesh.CloneUsingRecycler();
        var (vertices, indices) = (MeshData.StandardVerticesPerFace, MeshData.StandardIndicesPerFace);
        var size = Math.Max(mesh.VerticesCount, (mesh.IndicesCount + indices - 1) / indices * vertices);
        if (size > mesh.VerticesCount * VertexSlack || size * indices / vertices > mesh.IndicesCount * IndexSlack)
            return mesh.CloneUsingRecycler();
        var copy = recycler.GetOrCreateMesh(size);
        if (!Assert(copy.Recyclable) || !Assert(copy.VerticesMax >= mesh.VerticesCount) ||
            !Assert(copy.IndicesMax >= mesh.IndicesCount)) return mesh.CloneUsingRecycler();
        CopyBasicData(mesh, copy);
        CloneExtraData(mesh, copy);
        return copy;
    }

    // Replaces NowProcessChunk's new Dictionary<int, Block>(): the thread's own, emptied (Clear drops the blocks)
    private static Dictionary<int, Block> Drawn()
    {
        if (!On(DecorGroup)) return [];
        var decors = _decors;
        if (decors is null || decors.EnsureCapacity(0) > MaxDecors) _decors = decors = [];
        else decors.Clear();
        _ = Assert(decors.Count == 0) && Assert(decors.EnsureCapacity(0) <= MaxDecors);
        return decors;
    }

    // In place of map.LoadOceanityCorners(chunkX, chunkZ) in BeginProcessChunk (a null map throws as the callvirt did)
    private static float[]? Ocean(ClientWorldMap map, int chunkX, int chunkZ)
    {
        _ = Assert(chunkX >= 0 && chunkZ >= 0) && Assert(!_armed);
        _armed = On(OceanGroup);
        try
        {
            return map.LoadOceanityCorners(chunkX, chunkZ);
        }
        finally
        {
            _armed = false;
        }
    }

    // Replaces LoadOceanityCorners' newarr float32: the thread's array, once while armed (the method fills it whole)
    private static float[] Corners(int length)
    {
        if (!Assert(length >= 0) || !_armed || length != CornerCount) return new float[length];
        _armed = false;
        var corners = _corners ??= new float[CornerCount];
        return Assert(corners.Length == CornerCount) ? corners : new float[length];
    }

    // Replaces new List<ModelDataPoolLocation>(capacity): the thread's list, lent until Drain (else a new one)
    private static List<ModelDataPoolLocation> Rent(int capacity)
    {
        var list = On(ListGroup) ? _locations : null;
        _locations = null;
        _ = Assert(list is null || list.Count == 0) && Assert(capacity >= 0);
        return list ?? new List<ModelDataPoolLocation>(capacity);
    }

    // Replaces list.ToArray() (null throws as the callvirt did): the same new array; the emptied list is kept
    private static ModelDataPoolLocation[] Drain(List<ModelDataPoolLocation> list)
    {
        var locations = list.ToArray();
        _ = Assert(!ReferenceEquals(list, _locations)); // lent by Rent, or made there
        // not while another mod's patch on AddToPools may have kept it
        if (On(ListGroup) && Assert(locations.Length == list.Count) &&
            list.Capacity <= MaxLocations)
        {
            list.Clear();
            _locations = list;
        }

        return locations;
    }
}
