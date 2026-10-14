using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace Komet.Core;

internal enum PreJitState
{
    Idle,
    Running,
    Done,
    Cancelled
}

// No game assembly is ReadyToRun: first calls JIT on the main thread (character dialog SVG painters 3-11 ms each), and
// each new tier-0 method restarts the 100 ms call-counting delay, blocking tier 1. PrepareMethod compiles tier-0 ahead
// without cctors, from metadata tables (no 40k MethodInfos); skipped: no IL, generic, already patched. 9 of ~42k
// methods compile optimized and may inline a callee patched later, as the game would anyway.
internal static partial class PreJit
{
    public const int CheckEvery = 256, Niceness = 10;
    public const string ThreadName = "komet-prejit";
    private const int MaxAssemblies = 8, MaxLoaded = 4096, MaxMethods = 1 << 17, MaxPatched = 1 << 16, MaxNesting = 16;
    private const int MethodDefTable = 0x06, PrioProcess = 0;

    // What the join and first minutes call first
    private static readonly string[] Targets =
        ["VSEssentials", "VintagestoryAPI", "VintagestoryLib", "VSSurvivalMod", "VSCreativeMod"];

    private static volatile bool _cancel;
    private static Thread? _thread;
    private static int _skipped;
    private static bool _niced;

    // Checked at every checkpoint; on applies from the next world join
    public static bool Enabled { get; set; } = true;
    public static PreJitState State { get; private set; }

    public static int Prepared { get; private set; }
    public static int Failed { get; private set; }
    public static double JitMs { get; private set; } // compile time on the walking thread only
    public static double WallMs { get; private set; }

    internal static void Reset()
    {
        if (!Assert(_thread is not { IsAlive: true }) || !Assert(State != PreJitState.Running)) return;
        (_cancel, State, Prepared, Failed, _skipped, JitMs, WallMs, _niced) =
            (false, PreJitState.Idle, 0, 0, 0, 0, 0, false);
    }

    // The last Install: Komet's patches are in the skip set.
    public static void Start(ILogger logger)
    {
        if (!NotNull(logger) || !Enabled) return;
        // compiled code outlives the world
        if (State == PreJitState.Done || _thread is { IsAlive: true }) return;
        Reset();
        var patched = PatchedSet(); // Harmony's registry is read here, on the thread that patches
        _thread = new Thread(() => Run(patched, logger))
        {
            Name = ThreadName, IsBackground = true, Priority = ThreadPriority.BelowNormal // effective on Windows only
        };
        _thread.Start();
    }

    public static void Stop() => _cancel = true;

    private static void Run(HashSet<(Guid, int)> patched, ILogger logger)
    {
        // a foreground walker would hold the process open after the game quits
        if (!NotNull(patched) || !NotNull(logger) || !Assert(Thread.CurrentThread.IsBackground)) return;
        State = PreJitState.Running;
        try
        {
            Lower(); // in the catch-all: a misbehaving libc must not take the game down
            var wall = Stopwatch.StartNew();
            var jit = JitInfo.GetCompilationTime(true);
            foreach (var name in Targets.Bounded(MaxAssemblies))
            {
                if (_cancel || !Enabled) break;
                if (Find(name) is { } assembly) Walk(assembly, patched);
                (JitMs, WallMs) = ((JitInfo.GetCompilationTime(true) - jit).TotalMilliseconds,
                    wall.Elapsed.TotalMilliseconds);
            }

            State = _cancel || !Enabled ? PreJitState.Cancelled : PreJitState.Done;
            logger.Notification(
                "Komet: pre-compiled {0} engine methods in {1:F0} ms ({2:F0} ms JIT, {3} failed, {4} skipped, {5}): {6}",
                Prepared, WallMs, JitMs, Failed, _skipped, _niced ? "at nice " + Niceness : "at normal priority",
                State);
        }
        catch (Exception e)
        {
            State = PreJitState.Cancelled;
            logger.Warning("Komet: pre-compiling stopped after {0} methods: {1}", Prepared, e);
        }
    }

    // Thread.Priority is a no-op on Linux; setpriority on the tid works. nice 10: at 19 GC time-to-suspend grew to 9
    // ms.
    private static void Lower()
    {
        if (!OperatingSystem.IsLinux() || !Assert(Thread.CurrentThread.Name == ThreadName)) return;
        try
        {
            var tid = GetTid();
            _niced = Assert(tid > 0) && SetPriority(PrioProcess, (uint)tid, Niceness) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _niced = false; // a libc without the symbols: the walk runs at normal priority
        }
    }

    private static Assembly? Find(string name)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        if (!Assert(name.Length > 0) || !Assert(loaded.Length < MaxLoaded)) return null;
        for (var i = 0; i < Math.Min(loaded.Length, MaxLoaded); i++)
            if (!loaded[i].IsDynamic && loaded[i].GetName().Name == name)
                return loaded[i];
        return null; // VSCreativeMod is absent when the mod is disabled
    }

    // UnsafeLoadFrom assemblies each have a file to read tables from
    internal static void Walk(Assembly assembly, IReadOnlySet<(Guid, int)> patched)
    {
        if (!NotNull(assembly) || !NotNull(patched) ||
            !Assert(assembly is { IsDynamic: false, Location.Length: > 0 })) return;
        try
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            Walk(pe.GetMetadataReader(), assembly.ManifestModule, patched);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException
                                      or InvalidOperationException)
        {
            Failed++; // the whole assembly, unreadable
        }
    }

    private static void Walk(MetadataReader md, Module module, IReadOnlySet<(Guid, int)> patched)
    {
        var count = md.GetTableRowCount(TableIndex.MethodDef);
        if (!NotNull(module) || !Assert(count <= MaxMethods)) return;
        var id = module.ModuleVersionId;
        for (var i = 0; i < Math.Min(count, MaxMethods); i++)
        {
            if (i % CheckEvery == 0 && (_cancel || !Enabled)) return;
            var handle = MetadataTokens.MethodDefinitionHandle(i + 1); // rows are 1-based
            if (!Eligible(md, handle, id, patched)) _skipped++;
            else if (Prepare(module, MetadataTokens.GetToken(handle))) Prepared++;
            else Failed++;
        }
    }

    internal static bool Eligible(MetadataReader md, MethodDefinitionHandle handle, Guid module,
        IReadOnlySet<(Guid, int)> patched)
    {
        if (!NotNull(md) || !NotNull(patched) || !Assert(!handle.IsNil)) return false;
        var method = md.GetMethodDefinition(handle);
        return method.RelativeVirtualAddress != 0 && !IsGeneric(md, method) &&
               !patched.Contains((module, MetadataTokens.GetToken(handle)));
    }

    private static bool IsGeneric(MetadataReader md, MethodDefinition method)
    {
        if (method.GetGenericParameters().Count > 0) return true;
        var type = method.GetDeclaringType();
        if (!NotNull(md) || !Assert(!type.IsNil)) return true; // even global methods have a type, <Module>
        for (var depth = 0; depth < MaxNesting && !type.IsNil; depth++)
        {
            var definition = md.GetTypeDefinition(type);
            if (definition.GetGenericParameters().Count > 0) return true;
            type = definition.GetDeclaringType();
        }

        return !Assert(type.IsNil); // nested deeper than any compiler emits: leave it alone
    }

    internal static bool Prepare(Module module, int token)
    {
        if (!NotNull(module) || !Assert(token >> 24 == MethodDefTable)) return false;
        try
        {
            RuntimeHelpers.PrepareMethod(module.ModuleHandle.ResolveMethodHandle(token));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                                      or MissingMemberException or FileNotFoundException or FileLoadException
                                      or InvalidProgramException or MemberAccessException)
        {
            return false;
        }
    }

    // Keyed by module version id and token: no MethodBase materialised
    internal static HashSet<(Guid, int)> PatchedSet()
    {
        HashSet<(Guid, int)> set = [];
        foreach (var method in Harmony.GetAllPatchedMethods().Bounded(MaxPatched))
            _ = NotNull(method) && set.Add((method.Module.ModuleVersionId, method.MetadataToken));
        return set;
    }

    [LibraryImport("libc", EntryPoint = "gettid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetTid();

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetPriority(int which, uint who, int value);
}
