using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.World;

// LerpedClimateMaps keeps ten 1 MiB region maps, evicting by insertion: past ten regions (49 at view distance 1536)
// every lookup misses into the LOH. Sized after the view distance; switched off, ten again.
internal static class ClimateCache
{
    public const int EngineCapacity = 10, MaxCapacity = 144; // one entry is a MiB, so the ceiling is the memory budget

    public static bool Enabled { get; set; } = true;
    public static int Capacity { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "LerpedClimateMaps")]
    private static extern ref LimitedDictionary<long, int[]> Maps(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "LerpedClimateMapsLock")]
    private static extern ref object MapsLock(ClientWorldMap map);

    public static void Install(Harmony harmony)
    {
        var load = AccessTools.Method(typeof(ClientWorldMap),
            nameof(ClientWorldMap.LoadOrCreateLerpedClimateMapOffthread));
        var (maps, gate) = (AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMaps"),
            AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMapsLock"));
        var capacity = AccessTools.Field(typeof(LimitedDictionary<long, int[]>), "capacity");
        _world++; // what any thread remembers is the closed world's
        if (!NotNull(harmony) || !NotNull(load) || !NotNull(maps) || !NotNull(gate) || !NotNull(capacity) ||
            !Assert(maps.FieldType == typeof(LimitedDictionary<long, int[]>) && capacity.FieldType == typeof(int)))
            return; // the accessors would throw at first use
        _ = NotNull(harmony.Patch(load, new HarmonyMethod(Sized)));
        if (AccessTools.Method(typeof(ClientWorldMap), nameof(ClientWorldMap.TryLoadLerpedClimateMap), [typeof(int), typeof(int)])
            is { } find)
            _ = NotNull(harmony.Patch(find, new HarmonyMethod(Recent), new HarmonyMethod(Remember)));
    }

    // TryLoadLerpedClimateMap locks per climate lookup, among the most contended on join (trace, 2026-10-08). Each
    // thread remembers its last regions (evicted maps equal new ones), keyed by install: the main thread outlives the
    // world.
    private const int Remembered = 4;

    private static int _world;
    [ThreadStatic] private static int _lastWorld;
    [ThreadStatic] private static long[]? _lastKeys;
    [ThreadStatic] private static int[]?[]? _lastFound;
    [ThreadStatic] private static int _lastNext;

    private static bool Recent(ClientWorldMap __instance, int chunkX, int chunkZ, ref int[]? __result)
    {
        if (!Enabled || _lastFound is not { } found || _lastKeys is not { } keys || _lastWorld != _world ||
            !NotNull(__instance)) return true;
        var key = __instance.MapRegionIndex2DFromClientChunkCoord(chunkX, chunkZ);
        _ = Assert(keys.Length == Remembered) && Assert(found.Length == Remembered);
        for (var i = 0; i < Remembered; i++)
        {
            if (keys[i] != key || found[i] is not { } map) continue;
            __result = map;
            return false;
        }

        return true;
    }

    private static void Remember(ClientWorldMap __instance, int chunkX, int chunkZ, int[]? __result, bool __runOriginal)
    {
        if (!__runOriginal || __result is null || !NotNull(__instance)) return;
        if (_lastWorld != _world || _lastKeys is null || _lastFound is null)
            (_lastWorld, _lastKeys, _lastFound, _lastNext) = (_world, new long[Remembered], new int[]?[Remembered], 0);
        var slot = _lastNext;
        _lastNext = (slot + 1) % Remembered;
        (_lastKeys[slot], _lastFound[slot]) = (__instance.MapRegionIndex2DFromClientChunkCoord(chunkX, chunkZ), __result);
        _ = Assert(__result.Length > 0) && Index(slot, Remembered);
    }

    // Reachable regions per axis, squared; clamped so an extreme view distance cannot eat the heap
    internal static int CapacityFor(int region, int distance)
    {
        if (!Assert(region > 0) || !Assert(distance > 0)) return EngineCapacity;
        var perAxis = 2 * distance / region + 1;
        return Assert(perAxis > 0) ? Math.Clamp(perAxis * perAxis, EngineCapacity, MaxCapacity) : EngineCapacity;
    }

    internal static int Wanted(bool enabled, int region, int distance) =>
        enabled ? CapacityFor(region, distance) : EngineCapacity;

    // Prefix on the tesselation thread; the view distance is read once, only while on (each read lower-cases its key)
    private static void Sized(ClientWorldMap __instance)
    {
        var enabled = Enabled;
        if (NotNull(__instance))
            Fit(__instance, Wanted(enabled, __instance.RegionSize, enabled ? ClientSettings.ViewDistance : 0));
    }

    // Swapped under the engine's lock, which TryLoadLerpedClimateMap reads under; entries drop (not enumerable)
    internal static void Fit(ClientWorldMap map, int wanted)
    {
        var gate = MapsLock(map);
        if (!NotNull(gate) || !Assert(wanted is >= EngineCapacity and <= MaxCapacity)) return;
        Capacity = wanted;
        if (Maps(map) is { } maps && Limited<long, int[]>.CapacityOf(maps) == wanted) return;
        lock (gate)
        {
            ref var current = ref Maps(map);
            if (current is null || Limited<long, int[]>.CapacityOf(current) != wanted)
                current = new LimitedDictionary<long, int[]>(wanted);
        }
    }

    // An accessor into a generic type is declared on the open definition
    private static class Limited<TKey, TValue>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "capacity")]
        public static extern ref int CapacityOf(LimitedDictionary<TKey, TValue> dictionary);
    }
}

// The cloud thread builds a Vec3d per tile every 40 ms (~34k tiles, 33 MB/s); one per thread serves (IL-checked).
internal static class CloudTileScratch
{
    private const string Renderer = "FluffyClouds.CloudRendererMap", Method = "UpdateCloudTilesOffThread";
    private const string Reader = "Vintagestory.GameContent.WeatherDataReaderBase";
    private const string PreLoad = "Vintagestory.GameContent.WeatherDataReaderPreLoad";

    private const int MaxInstructions = 4096;

    [ThreadStatic] private static Vec3d? _scratch;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        var method = Target();
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(Substitute)));
    }

    // VSEssentials is not referenced: found by name once loaded
    internal static MethodInfo? Target()
    {
        var renderer = AccessTools.TypeByName(Renderer);
        if (!NotNull(renderer)) return null;
        var method = AccessTools.Method(renderer, Method, [typeof(int)]);
        return NotNull(method) && Assert(method.ReturnType == typeof(void)) ? method : null;
    }

    internal static ConstructorInfo? Constructor()
    {
        var ctor = AccessTools.Constructor(typeof(Vec3d), [typeof(double), typeof(double), typeof(double)]);
        return NotNull(ctor) && Assert(ctor.DeclaringType == typeof(Vec3d)) ? ctor : null;
    }

    // The methods the vector goes to, with its argument position: each reads only X, Y and Z
    private static MethodInfo?[] Readers()
    {
        var (reader, preload) = (AccessTools.TypeByName(Reader), AccessTools.TypeByName(PreLoad));
        if (!NotNull(reader) || !NotNull(preload)) return [];
        return
        [
            AccessTools.Method(reader, "LoadAdjacentSims", [typeof(Vec3d)]),
            AccessTools.Method(reader, "LoadLerp",
                [typeof(Vec3d), typeof(bool), typeof(float), typeof(float), typeof(float)]),
            AccessTools.Method(preload, "LoadLerp", [typeof(Vec3d)])
        ];
    }

    internal static List<CodeInstruction> Substitute(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        var (ctor, mine, readers) =
            (Constructor(), AccessTools.Method(typeof(CloudTileScratch), nameof(At)), Readers());
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor) ||
            !NotNull(mine)) return code;
        var at = Il.Single(code, c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor));
        if (!Assert(at >= 0) || !Index(at + 1, code.Count) || !Assert(code[at + 1].IsStloc())) return code;
        if (!Assert(Il.Confined(code, at + 1, (c, operand) => operand == 0
                ? c.opcode == OpCodes.Ldfld && c.operand is FieldInfo field && field.DeclaringType == typeof(Vec3d)
                : operand == 1 && c.operand is MethodInfo method && Array.IndexOf(readers, method) >= 0)))
            return code;
        Rewritten = Assert(Il.Substitute(code, at, mine));
        return code;
    }

    // Stands in for new Vec3d(x, y, z): the caller reads the vector before the next tile asks for it again
    internal static Vec3d At(double x, double y, double z)
    {
        if (!Enabled || !Finite(x) || !Finite(z)) return new Vec3d(x, y, z);
        return (_scratch ??= new Vec3d()).Set(x, y, z);
    }
}
