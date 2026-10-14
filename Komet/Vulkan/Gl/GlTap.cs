using HarmonyLib;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// OpenTK calls every GL function through GL's EntryPoints table, so its entries are where all callers pass (inlined
// setters and raw uploads too). Komet's own GL work runs Quietly: no listener hears it.
internal static unsafe partial class GlTap
{
    private enum Group
    {
        Programs,
        State,
        Touch,
        Objects,
        Records // optional: an entry OpenTK's table lacks is left out
    }

    private readonly record struct Entry(string Name, IntPtr Function, Group Kind);

    private const int MaxEntries = 224;

    private static readonly IntPtr[] Original = new IntPtr[MaxEntries];
    private static readonly int[] At = new int[MaxEntries]; // each entry's place in OpenTK's table, -1 when it has none
    private static Entry[] _entries = [];
    private static IntPtr[]? _table;
    private static int _quiet;

    public static bool Tapped => _table is not null;

    // Whether the Objects and Touch groups are in (always, while tapped)
    public static bool Scene { get; private set; }

    // Told of every texture about to be deleted, while tapped; its owner sets and clears it
    public static Action<uint>? Deleting { get; set; }

    // Told of every FBO about to be deleted, while tapped (its name may come back with other attachments)
    public static Action<uint>? FramebufferGone { get; set; }

    // Told of every program about to be deleted, while tapped (its name may come back for another program)
    public static Action<uint>? ProgramGone { get; set; }

    // Told of every program linked under the record with vertex and fragment stages (its sources are the record's now)
    public static Action<uint>? Linking { get; set; }

    // Every group, the record's entries too
    public static bool Tap()
    {
        if (_table is not null) return Scene && Recording;
        var table = AccessTools.Field(typeof(GL), "EntryPoints")?.GetValue(null) as IntPtr[];
        var names = AccessTools.Field(typeof(GL), "EntryPointNames")?.GetValue(null) as string[];
        if (table is null || names is null || !Assert(table.Length == names.Length)) return false;
        _entries = [.. ProgramEntries(), .. StateEntries(), .. Drawings(), .. Uploads(), .. ObjectEntries(), .. RecordEntries()];
        if (!Assert(_entries.Length <= MaxEntries) || !Assert(_entries.Length == RecordsAt + RecordCount)) return false;
        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++)
        {
            At[i] = Array.IndexOf(names, _entries[i].Name);
            Original[i] = At[i] >= 0 ? table[At[i]] : IntPtr.Zero;
            if (Original[i] != IntPtr.Zero) continue;
            if (_entries[i].Kind == Group.Records)
            {
                At[i] = -1;
                continue;
            }

            Array.Clear(Original);
            (_entries, Missing) = ([], _entries[i].Name);
            _ = Assert(Missing.Length > 0) && Assert(_entries.Length == 0);
            return false;
        }

        Seed();
        SeedObjects();
        SeedStencil();
        // Untapped calls went uncounted: nothing seen before may be taken as unchanged
        (Version, TextureVersion) = (Version + 1, TextureVersion + 1);
        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++)
            if (At[i] >= 0)
                table[At[i]] = _entries[i].Function;
        (_table, Scene, Missing, Recording) = (table, true, "", true);
        Taps++;
        return true;
    }

    // The GL function OpenTK's table lacks, when the last Tap failed
    public static string Missing { get; private set; } = "";

    // Taps with the record so far: a renderer made under one heard nothing between two (Vulkan-only)
    public static long Taps { get; private set; }

    // An exception must not leave an [UnmanagedCallersOnly] tap (the runtime ends the game). The first drops all
    // listeners, GL runs on alone, the renderer fails next frame
    public static string Fault { get; private set; } = "";

    private static bool Faulted(Exception e)
    {
        if (Fault.Length == 0) Fault = $"a GL tap threw {e.GetType().Name}: {e.Message}";
        Unlisten();
        return !Assert(Fault.Length > 0) && !NotNull(e.StackTrace);
    }

    private static void Unlisten()
    {
        (Drawing, Clearing, Touching, Writes, Querying, Answering) = (null, null, null, null, null, null);
        (Blitting, Reading, Resting, TextureClearing) = (null, null, false, null); // OpenGL draws again at once
        _ = Assert(_quiet >= 0) && Assert(_entries.Length <= MaxEntries);
    }

    private static void Heard<T>(Action<T> heard, T state) where T : allows ref struct
    {
        try
        {
            heard(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = Faulted(e);
        }
    }

    private static TResult Asked<T, TResult>(System.Func<T, TResult> asked, T state, TResult failed) where T : allows ref struct
    {
        try
        {
            return asked(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = Faulted(e);
            return failed;
        }
    }

    private static void Changed(uint texture)
    {
        if (Writes is { } writes && Assert(texture != uint.MaxValue)) Heard(static s => s.writes.TextureChanged(s.texture), (writes, texture));
    }

    private static void Unseen(uint buffer, string why)
    {
        if (Writes is { } writes && NotNull(why)) Heard(static s => s.writes.BufferUnseen(s.buffer, s.why), (writes, buffer, why));
    }

    private static void Queried(uint target, uint id)
    {
        if (Querying is { } querying && Assert(target > 0)) Heard(static s => s.querying(s.target, s.id), (querying, target, id));
    }

    private static long? Answered(uint id, uint name) => Answering is { } answering && Assert(name > 0)
        ? Asked(static s => s.answering(s.id, s.name), (answering, id, name), null)
        : null;

    public static void Untap()
    {
        if (_table is not { } table || !Assert(_entries.Length <= MaxEntries)) return;
        for (var i = 0; i < Math.Min(_entries.Length, MaxEntries); i++)
            if (At[i] >= 0 && Original[i] != IntPtr.Zero)
                table[At[i]] = Original[i];
        (_table, Scene, _quiet, Fault) = (null, false, 0, "");
        Unlisten();
        Unrecord();
        Unlean();
        Reads.Clear();
        Programs.Clear();
        SamplerUnits.Clear();
        (_using, _samplers, _current, _unit) = (null, null, 0, 0);
        _ = Assert(Programs.Count == 0) && Assert(UnitTextures.Length == MaxUnits);
        Buffers.Clear();
        Outputs.Clear();
        Array.Clear(Blends);
        _ = Assert(Buffers.Count == 0) && Assert(Blends[0] == 0);
        Arrays.Clear();
        TextureTargets.Clear();
        (_array, _vao) = (null, 0);
        Array.Clear(Uniforms);
        Array.Clear(Storages);
        Array.Clear(UnitArrays);
        Array.Clear(UnitCubes);
        _ = Assert(Arrays.Count == 0) && Assert(TextureTargets.Count == 0) && Assert(!Tapped);
    }

    public static QuietScope Quietly()
    {
        _quiet++;
        _ = Assert(_quiet is > 0 and < 64);
        return new QuietScope(true);
    }

    public readonly struct QuietScope(bool live) : IDisposable
    {
        public void Dispose() => Loud(live);
    }

    private static void Loud(bool live)
    {
        if (live && Assert(_quiet > 0)) _quiet--;
        _ = Assert(_quiet >= 0);
    }
}
