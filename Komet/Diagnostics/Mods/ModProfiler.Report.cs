using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;

namespace Komet.Diagnostics;

internal sealed partial class ModProfiler
{
    public const int ShownMethods = 40, ShownTypes = 15;

    internal static bool Engine(Assembly assembly) => NotNull(assembly) && Assert(MaxTypes > 0) &&
        assembly.GetName().Name is { } name && (name.StartsWith("Vintagestory", StringComparison.Ordinal) ||
                                                 name.StartsWith("VS", StringComparison.Ordinal));

    internal static MethodInfo[] Candidates(Assembly assembly)
    {
        if (!NotNull(assembly) || Engine(assembly)) return [];
        var found = new HashSet<MethodInfo>();
        foreach (var patch in PatchMethods(assembly).Bounded(MaxMethods)) _ = found.Add(patch);
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types; // a type whose dependency is missing (an optional mod) is skipped
        }

        // RuntimeType[] as Type[]: a span over it throws
        foreach (var type in types.Where(static t => t is { IsInterface: false, ContainsGenericParameters: false }).Bounded(MaxTypes))
            Entries(type!, found);
        return Assert(found.Count <= MaxTypes * 64) && NotNull(found) ? [.. found.Where(Patchable).Take(MaxMethods)] : [];
    }

    private static void Entries(Type type, HashSet<MethodInfo> found)
    {
        if (!NotNull(type) || !NotNull(found) || found.Count >= MaxMethods) return;
        var closure = type.IsDefined(typeof(CompilerGeneratedAttribute), false);
        foreach (var method in AccessTools.GetDeclaredMethods(type).Bounded(MaxMethods))
        {
            var lambda = closure && method.Name.Contains(">b__", StringComparison.Ordinal);
            var engineVirtual = method.IsVirtual && method.GetBaseDefinition() is { } root && root != method &&
                                root.DeclaringType is { } owner && Engine(owner.Assembly);
            if (lambda || engineVirtual) _ = found.Add(method);
        }

        foreach (var face in type.GetInterfaces().AsEnumerable().Bounded(MaxMethods))
            if (Engine(face.Assembly) && !type.IsAbstract)
                foreach (var target in type.GetInterfaceMap(face).TargetMethods.AsEnumerable().Bounded(MaxMethods))
                    if (target.DeclaringType == type) _ = found.Add(target);
    }

    private static List<MethodInfo> PatchMethods(Assembly assembly)
    {
        var found = new List<MethodInfo>();
        foreach (var original in Harmony.GetAllPatchedMethods().Bounded(HarmonyAudit.MaxMethods))
            if (Harmony.GetPatchInfo(original) is { } info)
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Bounded(HarmonyAudit.MaxPatches))
                    if (patch.PatchMethod.DeclaringType?.Assembly == assembly && patch.owner != HarmonyId) found.Add(patch.PatchMethod);
        _ = Assert(found.Count <= HarmonyAudit.MaxMethods * 4) && NotNull(found);
        return found;
    }

    private static bool Patchable(MethodInfo method) =>
        NotNull(method) && !method.IsAbstract && !method.ContainsGenericParameters && method.DeclaringType is { ContainsGenericParameters: false } &&
        method.GetMethodBody() is { } body && body.GetILAsByteArray() is { Length: >= MinIlBytes } && Assert(method.Name.Length > 0);

    internal readonly record struct ProfileRow(string Name, string Type, double MsPerFrame, double PeakMs, long Calls, double MeanUs,
        double MaxUs, double KbPerFrame, double BytesPerCall, double DrawsPerFrame, double MainShare);

    internal readonly record struct ProfileSummary(double Seconds, int Frames, double MsPerFrame, double P99Ms, double WorstMs,
        int SlowFrames, double KbPerFrame, double AllocShare, (int Gen0, int Gen1, int Gen2) Collections, double PauseMs,
        double DrawsPerFrame, float[] Series);

    public const double SlowMs = 2;
    public const int MaxHoldings = 8;

    // "<>c__DisplayClass3_0.<Register>b__0" -> SmokeSystem.Register (lambda)
    internal static string Pretty(MethodBase method)
    {
        if (!NotNull(method)) return "?";
        var type = method.DeclaringType;
        for (var depth = 0; depth < 4 && type is { DeclaringType: { } outer } && type.Name.StartsWith('<'); depth++) type = outer;
        var name = method.Name;
        if (name.StartsWith('<') && name.IndexOf('>', StringComparison.Ordinal) is var close and > 1)
        {
            var host = name[1..close];
            var bar = name.IndexOf('|', StringComparison.Ordinal);
            var local = name.IndexOf(">g__", StringComparison.Ordinal) is var g and > 0 && bar > g ? name[(g + 4)..bar] : "";
            name = local.Length > 0 ? $"{host} › {local}" : host + " (lambda)";
        }

        return Assert(name.Length > 0) ? $"{type?.Name ?? "?"}.{name}" : "?";
    }

    private double Frames1 => Math.Max(1, _frames);

    private ProfileRow RowOf(int i) => !Index(i, _methods.Length) || !Assert(_calls[i] > 0) ? default : new ProfileRow(Pretty(_methods[i]),
        _methods[i].DeclaringType?.FullName ?? "?", FrameClock.ToMs(_self[i]) / Frames1, FrameClock.ToMs(_peak[i]), _calls[i],
        FrameClock.ToMs(_ticks[i]) * 1000 / _calls[i], FrameClock.ToMs(_max[i]) * 1000, _alloc[i] / 1024.0 / Frames1,
        (double)_alloc[i] / _calls[i], _draws[i] / Frames1, (double)_mainCalls[i] / _calls[i]);

    public ProfileRow[] Rows(int count, string sort = "self")
    {
        if (!Assert(count is > 0 and <= MaxMethods) || !NotNull(sort)) return [];
        var called = Enumerable.Range(0, _methods.Length).Where(i => _calls[i] > 0);
        var order = sort switch
        {
            "peak" => called.OrderByDescending(i => _peak[i]),
            "alloc" => called.OrderByDescending(i => _alloc[i]),
            "calls" => called.OrderByDescending(i => _calls[i]),
            _ => called.OrderByDescending(i => _self[i])
        };
        return [.. order.Take(count).Select(RowOf)];
    }

    public ProfileSummary Summary()
    {
        var n = Math.Min(_frames, MaxSeries);
        var series = _series.AsSpan(0, n).ToArray();
        var sorted = series.ToArray();
        Array.Sort(sorted);
        var alloc = _alloc.Sum();
        var all = Math.Max(1, _allocEnd - _allocStart);
        var collections = (_gcEnd.Gen0 - _gcStart.Gen0, _gcEnd.Gen1 - _gcStart.Gen1, _gcEnd.Gen2 - _gcStart.Gen2);
        var (mean, p99, worst) = (0.0, 0.0, 0.0);
        if (n > 0) (mean, p99, worst) = (series.Average(static v => (double)v), FrameStats.Percentile(sorted, 990), sorted[^1]);
        return Assert(n >= 0) && Assert(alloc >= 0)
            ? new ProfileSummary(Elapsed, _frames, mean, p99, worst,
                series.Count(static v => v >= SlowMs), alloc / 1024.0 / Frames1, Math.Min(1, (double)alloc / all), collections,
                (_pauseEnd - _pauseStart) / 1000.0, _draws.Sum() / Frames1, series)
            : default;
    }

    // Statics of types that ran, largest first (no cctor triggered). An estimate, not a heap walk.
    public (string Field, string Kind, long Count)[] Holdings()
    {
        var found = new List<(string, string, long)>();
        foreach (var type in Enumerable.Range(0, _methods.Length).Where(i => _calls[i] > 0).Select(i => _methods[i].DeclaringType)
                     .OfType<Type>().Distinct().Bounded(MaxTypes))
            foreach (var field in AccessTools.GetDeclaredFields(type).Bounded(256))
                if (field is { IsStatic: true, IsLiteral: false } && Size(field) is { } held && held.Count > 0)
                    found.Add(($"{Pretty(type)}.{field.Name}", held.Kind, held.Count));
        return Assert(found.Count < MaxTypes * 256) ? [.. found.OrderByDescending(static h => h.Item3).Take(MaxHoldings)] : [];
    }

    private static string Pretty(Type type)
    {
        var t = type;
        for (var depth = 0; depth < 4 && t is { DeclaringType: { } outer } && t.Name.StartsWith('<'); depth++) t = outer;
        return NotNull(t) ? t.Name : "?";
    }

    private static (string Kind, long Count)? Size(FieldInfo field)
    {
        if (!NotNull(field) || field.FieldType.IsPrimitive) return null;
        try
        {
            return field.GetValue(null) switch
            {
                Array array => (field.FieldType.Name, array.LongLength),
                System.Collections.ICollection collection => (field.FieldType.Name.Split('`')[0], collection.Count),
                string text when text.Length > 256 => ("String", text.Length),
                _ => null
            };
        }
        catch (Exception e) when (e is TargetInvocationException or TypeInitializationException or InvalidOperationException
                                      or FieldAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public string Report()
    {
        var sum = Summary();
        var called = Enumerable.Range(0, _methods.Length).Count(i => _calls[i] > 0);
        var text = new StringBuilder();
        _ = text.Append(CultureInfo.InvariantCulture,
                $"Komet profile: {ModId} {Version}, {sum.Seconds:F1} s, {_frames} frames, {_methods.Length} methods instrumented, ")
            .Append(CultureInfo.InvariantCulture, $"{_failed} not patchable, {called} called\n")
            .Append("Times include the profiler's own cost, about 0.1 us per call. self = the method minus profiled calls inside it.\n\n")
            .Append(CultureInfo.InvariantCulture,
                $"Main thread per frame: mean {sum.MsPerFrame:F3} ms, p99 {sum.P99Ms:F3} ms, worst {sum.WorstMs:F3} ms, ")
            .Append(CultureInfo.InvariantCulture, $"{sum.SlowFrames} frames over {SlowMs:F0} ms\n")
            .Append(CultureInfo.InvariantCulture,
                $"Allocated: {sum.KbPerFrame:F1} KB per frame, {100 * sum.AllocShare:F1} % of everything allocated meanwhile; ")
            .Append(CultureInfo.InvariantCulture,
                $"collections meanwhile gen0 {sum.Collections.Gen0}, gen1 {sum.Collections.Gen1}, gen2 {sum.Collections.Gen2}, ")
            .Append(CultureInfo.InvariantCulture, $"paused {sum.PauseMs:F1} ms\n")
            .Append(CultureInfo.InvariantCulture, $"Draw calls: {sum.DrawsPerFrame:F1} per frame\n\n")
            .Append(CultureInfo.InvariantCulture,
                $"{"method",-64} {"calls",9} {"self ms",9} {"total ms",9} {"ms/frame",9} {"peak ms",8} {"max ms",8} {"KB/frame",9} {"B/call",8} {"draws/f",8} {"main",5}\n");
        foreach (var row in Rows(Math.Min(ShownMethods, MaxMethods)).Bounded(ShownMethods)) Line(text, row);
        _ = text.Append("\nBy type (self ms)\n");
        var order = Enumerable.Range(0, _methods.Length).Where(i => _calls[i] > 0).ToArray();
        var types = ByType(order);
        foreach (var (type, self) in ((ReadOnlySpan<(string, long)>)types)[..Math.Min(types.Length, ShownTypes)].Bounded(ShownTypes))
            _ = text.Append(CultureInfo.InvariantCulture, $"  {type,-62} {FrameClock.ToMs(self),10:F2}\n");
        var holdings = Holdings();
        if (holdings.Length > 0) _ = text.Append("\nHeld in static fields (elements)\n");
        foreach (var (field, kind, count) in holdings.Bounded(MaxHoldings))
            _ = text.Append(CultureInfo.InvariantCulture, $"  {field,-62} {kind,-20} {count,10}\n");
        return Assert(sum.Seconds >= 0) && Assert(text.Length > 0) ? text.ToString() : "";
    }

    private void Line(StringBuilder text, ProfileRow r)
    {
        if (!NotNull(text) || !Assert(r.Calls > 0)) return;
        var name = r.Name.Length > 64 ? "..." + r.Name[^61..] : r.Name;
        _ = text.Append(CultureInfo.InvariantCulture,
            $"{name,-64} {r.Calls,9} {r.MsPerFrame * Frames1,9:F2} {r.MeanUs * r.Calls / 1000,9:F2} {r.MsPerFrame,9:F3} {r.PeakMs,8:F2} " +
            $"{r.MaxUs / 1000,8:F2} {r.KbPerFrame,9:F2} {r.BytesPerCall,8:F0} {r.DrawsPerFrame,8:F2} {100 * r.MainShare,4:F0}%\n");
    }

    private (string Type, long Self)[] ByType(int[] order) =>
        Assert(order.Length <= _methods.Length) && NotNull(order)
            ? [.. order.GroupBy(i => _methods[i].DeclaringType is { } t ? Pretty(t) : "?").Select(g => (g.Key, g.Sum(i => _self[i])))
                .OrderByDescending(static t => t.Item2)]
            : [];

    public double Elapsed
    {
        get
        {
            if (_started == 0) return 0;
            var end = _ended > 0 ? _ended : System.Diagnostics.Stopwatch.GetTimestamp();
            return Assert(end >= _started) ? FrameClock.ToMs(end - _started) / 1000 : 0;
        }
    }
    public int Frames => _frames;
    public float Seconds => _seconds;

    public (string Name, double Ms)[] Top(int count) =>
        Assert(count is > 0 and <= ShownMethods) && NotNull(_methods)
            ? [.. Rows(count).Select(static r => (r.Name, r.MsPerFrame))]
            : [];
}
