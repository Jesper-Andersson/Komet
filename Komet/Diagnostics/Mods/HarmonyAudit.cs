using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Komet.Diagnostics;

internal enum PatchKind { Prefix, Postfix, Transpiler, Finalizer }

// Skips: a bool prefix, which may stop the original and later prefixes
internal readonly record struct PatchLink(string Owner, string Mod, PatchKind Kind, int Priority, string Method, bool Skips);

internal enum PatchRisk { Low, Medium, High }

internal sealed record PatchedMethod(string Name, PatchLink[] Chain, int Owners, PatchRisk Risk);

// Off the main thread (GetPatchInfo pays System.Text.Json's warm-up), once per debug capture
internal sealed class HarmonyAudit
{
    public const int MaxMethods = 8192, MaxPatches = 64, MaxLoadedMods = 512;
    private static readonly PatchKind[] Kinds = [PatchKind.Prefix, PatchKind.Postfix, PatchKind.Transpiler, PatchKind.Finalizer];

    public PatchedMethod[] Methods { get; private init; } = [];
    public (string Owner, string Mod, int Methods)[] Owners { get; private init; } = [];
    public int Shared => Methods.Count(static m => m.Owners > 1);

    public static HarmonyAudit Walk(IModLoader loader)
    {
        if (!NotNull(loader) || !Assert(MaxMethods > 0)) return new HarmonyAudit();
        var mods = ModsById(loader);
        var methods = new List<PatchedMethod>();
        var byOwner = new Dictionary<string, int>();
        foreach (var method in Harmony.GetAllPatchedMethods().Bounded(MaxMethods))
        {
            if (Harmony.GetPatchInfo(method) is not { } info || info.Owners.Count == 0) continue;
            var chain = Chain(info, mods);
            foreach (var owner in info.Owners.Bounded(MaxPatches)) byOwner[owner] = byOwner.GetValueOrDefault(owner) + 1;
            methods.Add(new PatchedMethod(Describe(method), chain, info.Owners.Count, Rate(chain, info.Owners.Count)));
        }

        methods.Sort(static (a, b) => (b.Risk, b.Owners).CompareTo((a.Risk, a.Owners)) is var order && order != 0
            ? order
            : string.CompareOrdinal(a.Name, b.Name));
        var owners = byOwner.OrderByDescending(static o => o.Value).ThenBy(static o => o.Key, StringComparer.Ordinal)
            .Select(o => (o.Key, mods.GetValueOrDefault(o.Key, ""), o.Value)).ToArray();
        return Assert(methods.Count <= MaxMethods) && NotNull(owners) ? new HarmonyAudit { Methods = [.. methods], Owners = owners }
            : new HarmonyAudit();
    }

    private static Dictionary<string, string> ModsById(IModLoader loader)
    {
        var mods = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mod in loader.Mods.Bounded(MaxLoadedMods))
            if (mod.Info is { } info) mods[info.ModID] = info.Name + " " + info.Version;
        return Assert(mods.Count <= MaxLoadedMods) && NotNull(mods) ? mods : [];
    }

    private static PatchLink[] Chain(Patches info, Dictionary<string, string> mods)
    {
        var chain = new List<PatchLink>();
        foreach (var kind in Kinds.Bounded(Kinds.Length))
        {
            var patches = kind switch
            {
                PatchKind.Prefix => info.Prefixes, PatchKind.Postfix => info.Postfixes,
                PatchKind.Transpiler => info.Transpilers, _ => info.Finalizers
            };
            foreach (var patch in patches.OrderByDescending(static p => p.priority).ThenBy(static p => p.index).Bounded(MaxPatches))
                chain.Add(new PatchLink(patch.owner, mods.GetValueOrDefault(patch.owner, ""), kind, patch.priority,
                    Describe(patch.PatchMethod), kind == PatchKind.Prefix && patch.PatchMethod.ReturnType == typeof(bool)));
        }

        return Assert(chain.Count <= Kinds.Length * MaxPatches) && NotNull(chain) ? [.. chain] : [];
    }

    // High: two owners rewrite IL, or one's prefix can skip another's. Medium: one transpiler among owners. Low:
    // otherwise.
    internal static PatchRisk Rate(PatchLink[] chain, int owners)
    {
        if (!NotNull(chain) || !Assert(owners >= 0) || owners < 2) return PatchRisk.Low;
        var rewriters = chain.Where(static l => l.Kind == PatchKind.Transpiler).Select(static l => l.Owner).Distinct().Count();
        var skipper = chain.FirstOrDefault(static l => l.Skips);
        var skipsOthers = skipper.Skips && chain.Any(l => l.Owner != skipper.Owner);
        if (rewriters > 1 || skipsOthers) return PatchRisk.High;
        return rewriters == 1 ? PatchRisk.Medium : PatchRisk.Low;
    }

    internal static string Describe(MethodBase? method) =>
        NotNull(method) && Assert(method.Name.Length > 0) ? $"{method.DeclaringType?.FullName}.{method.Name}" : "?";

    public string Text()
    {
        var text = new StringBuilder();
        _ = text.Append("Harmony patches: ").Append(Methods.Length).Append(" methods, ").Append(Owners.Length)
            .Append(" owners, ").Append(Shared).Append(" shared\n\nOwners\n");
        foreach (var (owner, mod, count) in Owners.Bounded(MaxMethods))
            _ = text.Append("  ").Append(owner).Append(mod.Length > 0 ? $" ({mod})" : "").Append(": ").Append(count).Append('\n');
        foreach (var method in Methods.Bounded(MaxMethods))
        {
            _ = text.Append('\n').Append(method.Name).Append("  [").Append(method.Risk).Append(", ").Append(method.Owners)
                .Append(" owners]\n");
            foreach (var link in method.Chain.Bounded(Kinds.Length * MaxPatches))
                _ = text.Append("  ").Append(link.Kind).Append(' ').Append(link.Owner).Append(" prio ").Append(link.Priority)
                    .Append(link.Skips ? " can-skip " : " ").Append(link.Method).Append('\n');
        }

        return Assert(text.Length > 0) && NotNull(Methods) ? text.ToString() : "";
    }
}
