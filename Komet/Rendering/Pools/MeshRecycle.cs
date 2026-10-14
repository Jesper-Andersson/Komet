using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;

namespace Komet.Rendering;

// CloneUsingRecycler keeps basic arrays, but CloneExtraData clones extra data from scratch and DisposeExtraData drops
// it: two thirds of client allocations. A recyclable mesh keeps them as capacity; custom parts copy Count values, not
// SetFrom's whole buffer. Fields the source lacks get a fresh mesh's state (null, TextureIds empty). The recycler
// matches by size only, while passes differ in custom parts (liquid 2 floats + 2 ints, topsoil int + two shorts, others
// one int), so dropped or outgrown values go to a few spare slots per element type, and a new buffer takes the smallest
// fitting spare. Only CloneExtraData moves arrays: its destination's parts are held by nothing else.
internal static class MeshRecycle
{
    private static readonly DataConversion ShortConversion = new CustomMeshDataPartShort().Conversion;
    private static readonly DataConversion IntConversion = new CustomMeshDataPartInt().Conversion;

    // A recycled mesh carries buffers, so the clone must clear what a source lacks
    private static bool _kept;

    // Totals while Counting.Hud; Interlocked: tessellation and main thread clone
    private static long _clones, _reused, _saved;

    public static bool Enabled { get; set; } = true;

    // Both patches in: CloneExtraData nulls what a source lacks on reuse (ClutterMeshes' scratch relies on it)
    public static bool Patched { get; private set; }
    public static long Clones => Interlocked.Read(ref _clones);
    public static long Reused => Interlocked.Read(ref _reused);
    public static long Saved => Interlocked.Read(ref _saved);

    private static void Reuse(long values)
    {
        _ = Interlocked.Increment(ref _reused);
        _ = Interlocked.Add(ref _saved, values);
    }

    public static void Install(Harmony harmony)
    {
        (_kept, Patched) = (false, false); // the previous world's recycler is gone
        var clone = AccessTools.Method(typeof(MeshData), "CloneExtraData");
        var dispose = AccessTools.Method(typeof(MeshData), "DisposeExtraData");
        var part = typeof(CustomMeshDataPart<float>);
        var prefix = new HarmonyMethod(CloneExtraData);
        if (!NotNull(harmony) || !NotNull(clone) || !NotNull(dispose) || !Assert(Il.Binds(clone, prefix.method)) ||
            !Assert(AccessTools.Field(part, "customAllocationSize") != null) ||
            !Assert(AccessTools.Field(part, "allocationSize") != null))
            return; // the Allocation accessors would throw at first use
        _ = NotNull(harmony.Patch(clone, prefix));
        _ = NotNull(harmony.Patch(dispose, new HarmonyMethod(DisposeExtraData)));
        Patched = true;
    }

    // Engine copies TextureIds only with TextureIndices and throws on indices without ids: left to it
    internal static bool CloneExtraData(MeshData __instance, MeshData dest)
    {
        if ((!Enabled && !_kept) || !NotNull(__instance) || !NotNull(dest) || ReferenceEquals(__instance, dest))
            return true;
        var source = __instance;
        if (source.TextureIndices != null && source.TextureIds == null) return true;
        dest.Normals = Exact(dest.Normals, source.Normals, source.NormalsCount);
        dest.XyzFaces = Exact(dest.XyzFaces, source.XyzFaces, source.XyzFacesCount);
        dest.TextureIndices = Exact(dest.TextureIndices, source.TextureIndices, source.TextureIndicesCount);
        dest.TextureIds =
            source.TextureIndices is null ? [] : Exact(dest.TextureIds, source.TextureIds, source.TextureIds!.Length);
        dest.ClimateColorMapIds = Exact(dest.ClimateColorMapIds, source.ClimateColorMapIds, source.ColorMapIdsCount);
        dest.SeasonColorMapIds = Exact(dest.SeasonColorMapIds, source.SeasonColorMapIds, source.ColorMapIdsCount);
        dest.RenderPassesAndExtraBits = Exact(dest.RenderPassesAndExtraBits, source.RenderPassesAndExtraBits,
            source.RenderPassCount);
        Counts(source, dest);
        dest.CustomFloats = Part<CustomMeshDataPartFloat, float>(dest.CustomFloats, source.CustomFloats);
        dest.CustomShorts = Part<CustomMeshDataPartShort, short>(dest.CustomShorts, source.CustomShorts);
        dest.CustomBytes = Part<CustomMeshDataPartByte, byte>(dest.CustomBytes, source.CustomBytes);
        dest.CustomInts = Part<CustomMeshDataPartInt, int>(dest.CustomInts, source.CustomInts);
        if (Counting.Hud) _ = Interlocked.Increment(ref _clones);
        return false;
    }

    // Only a mesh returning to the recycler keeps anything
    internal static bool DisposeExtraData(MeshData __instance)
    {
        if (!Enabled || !NotNull(__instance) || !__instance.Recyclable) return true;
        var mesh = __instance;
        if (!Assert(mesh.XyzFacesCount >= 0) || !Assert(mesh.RenderPassCount >= 0)) return true;
        _kept = true;
        (mesh.NormalsCount, mesh.XyzFacesCount, mesh.TextureIndicesCount) = (0, 0, 0);
        (mesh.ColorMapIdsCount, mesh.RenderPassCount) = (0, 0);
        if (mesh.CustomFloats is { } floats) floats.Count = 0;
        if (mesh.CustomShorts is { } shorts) shorts.Count = 0;
        if (mesh.CustomBytes is { } bytes) bytes.Count = 0;
        if (mesh.CustomInts is { } ints) ints.Count = 0;
        return false;
    }

    // Exactly CloneExtraData's counts: Normals none, the two colour maps share one
    private static void Counts(MeshData source, MeshData dest)
    {
        if (!Assert(source.XyzFacesCount >= 0) || !Assert(source.RenderPassCount >= 0)) return;
        if (source.XyzFaces != null) dest.XyzFacesCount = source.XyzFacesCount;
        if (source.TextureIndices != null) dest.TextureIndicesCount = source.TextureIndicesCount;
        if (source.ClimateColorMapIds != null || source.SeasonColorMapIds != null)
            dest.ColorMapIdsCount = source.ColorMapIdsCount;
        if (source.RenderPassesAndExtraBits != null) dest.RenderPassCount = source.RenderPassCount;
    }

    // Engine sizes it to the count: reused only at that exact length
    private static T[]? Exact<T>(T[]? existing, T[]? source, int count) where T : unmanaged
    {
        if (source is null) return null;
        if (!Assert(count >= 0 && count <= source.Length)) return (T[])source.Clone();
        var target = existing is not null && existing.Length == count
            ? existing : GC.AllocateUninitializedArray<T>(count);
        Array.Copy(source, target, count);
        if (Counting.Hud && ReferenceEquals(target, existing)) Reuse(count);
        return target;
    }

    // Engine Clone is new TPart() + SetFrom; a part without values is left to that
    private static TPart? Part<TPart, T>(TPart? dest, TPart? source)
        where TPart : CustomMeshDataPart<T>, new() where T : unmanaged
    {
        var engine = source?.Values is null || !Assert(source.Count >= 0 && source.Count <= source.Values.Length);
        if (engine && Enabled && dest?.Values is { } dropped) Spare<T>.Put(dropped);
        if (source is null) return null;
        var part = engine ? new TPart() : Filled(dest ?? new TPart(), source);
        if (engine) part.SetFrom(source);
        // Byte Clone copies Conversion; Short and Int clones leave the default
        if (part is CustomMeshDataPartByte bytes && source is CustomMeshDataPartByte from)
            bytes.Conversion = from.Conversion;
        else if (part is CustomMeshDataPartShort shorts) shorts.Conversion = ShortConversion;
        else if (part is CustomMeshDataPartInt ints) ints.Conversion = IntConversion;
        return part;
    }

    // Values is addressed via Count, its length only capacity, so the buffer may be larger
    private static TPart Filled<TPart, T>(TPart dest, CustomMeshDataPart<T> source)
        where TPart : CustomMeshDataPart<T> where T : unmanaged
    {
        if (!NotNull(source.Values) || !Assert(source.Count <= source.Values.Length)) return dest;
        var values = dest.Values is { } kept && kept.Length >= source.Count ? kept : Grown(dest.Values, source.Count);
        var reuse = ReferenceEquals(values, dest.Values);
        Array.Copy(source.Values, values, source.Count);
        (dest.Values, dest.Count) = (values, source.Count);
        Allocation<T>.Custom(dest) = Allocation<T>.Custom(source);
        Allocation<T>.Size(dest) = Allocation<T>.Size(source);
        dest.InterleaveSizes = Exact(dest.InterleaveSizes, source.InterleaveSizes, source.InterleaveSizes?.Length ?? 0);
        dest.InterleaveOffsets = Exact(dest.InterleaveOffsets, source.InterleaveOffsets,
            source.InterleaveOffsets?.Length ?? 0);
        (dest.InterleaveStride, dest.Instanced, dest.StaticDraw, dest.BaseOffset) =
            (source.InterleaveStride, source.Instanced, source.StaticDraw, source.BaseOffset);
        if (Counting.Hud && reuse) Reuse(source.Values.Length);
        return dest;
    }

    private static int Capacity(int count) => Assert(count >= 0) ? Math.Max(4, count + count / 4) : 4;

    private static T[] Grown<T>(T[]? small, int count) where T : unmanaged
    {
        if (!Enabled) return GC.AllocateUninitializedArray<T>(Capacity(count));
        if (small is not null) Spare<T>.Put(small);
        var spare = Spare<T>.Take(count);
        if (spare is null) return GC.AllocateUninitializedArray<T>(Capacity(count));
        _ = Assert(spare.Length >= count) && Assert(!ReferenceEquals(spare, small) || small.Length >= count);
        if (Counting.Hud) Reuse(count);
        return spare;
    }

    // Unheld values by element type; tessellation threads and main thread clone at once
    private static class Spare<T> where T : unmanaged
    {
        private const int Slots = 16;
        private static readonly T[]?[] Kept = new T[]?[Slots]; // also the lock: one per element type

        public static T[]? Take(int count)
        {
            lock (Kept)
            {
                var best = -1;
                for (var i = 0; i < Slots; i++)
                    if (Kept[i] is { } array && array.Length >= count &&
                        (best < 0 || array.Length < Kept[best]!.Length)) best = i;
                if (best < 0) return null;
                var taken = Kept[best];
                Kept[best] = null;
                return Assert(taken is not null) ? taken : null;
            }
        }

        public static void Put(T[] array)
        {
            if (!NotNull(array)) return;
            lock (Kept)
            {
                var smallest = 0;
                for (var i = 0; i < Slots; i++)
                {
                    if (Kept[i] is null)
                    {
                        Kept[i] = array;
                        return;
                    }

                    if (Kept[i]!.Length < Kept[smallest]!.Length) smallest = i;
                }

                if (Kept[smallest]!.Length < array.Length) Kept[smallest] = array;
            }
        }
    }

    // Fields SetFrom copies behind AllocationSize
    private static class Allocation<T>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "customAllocationSize")]
        public static extern ref bool Custom(CustomMeshDataPart<T> part);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "allocationSize")]
        public static extern ref int Size(CustomMeshDataPart<T> part);
    }
}
