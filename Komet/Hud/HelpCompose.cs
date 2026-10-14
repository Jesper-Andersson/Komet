using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Hud;

// The block interaction help (DrawWorldInteractionUtil) composes its Cairo lines anew at every block looked at, every
// 20 ticks at an entity and at every block change nearby, even when the lines would be the same: 5-15 ms of the main
// thread each. Skipped when what would be drawn (actions, keys, stacks) equals what the composer shows.
internal static class HelpCompose
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0x0FB2BE826EC4613FUL;

    private const int MaxInteractions = 64, MaxStacks = 256;
    private const string Util = "Vintagestory.Client.NoObf.DrawWorldInteractionUtil";

    private static readonly ConditionalWeakTable<DrawWorldInteractionUtil, StrongBox<string>> Shown = [];
    private static readonly StringBuilder Line = new(); // Signature's, main thread
    private static readonly SeamGuard Guard = new(nameof(HelpCompose), PatchKinds.Replacing, [typeof(HelpCompose)]);

    public static bool Enabled { get; set; } = true;

    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    // Counting.Hud totals (main thread)
    public static long Skipped { get; private set; }
    public static long Composed { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || !Assert(seams.Length == 1) || seams[0] is null || !Guard.Match(seams, fingerprint)) return;
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Composing)));
        Recheck();
    }

    internal static MethodBase?[] Seams()
    {
        var type = AccessTools.TypeByName(Util);
        MethodBase?[] seams =
            [type is null ? null : AccessTools.DeclaredMethod(type, "ComposeBlockWorldInteractionHelp", [typeof(WorldInteraction[])])];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // False leaves the composer as it is: the same lines would be drawn
    internal static bool Composing(DrawWorldInteractionUtil __instance, WorldInteraction[]? wis, ICoreClientAPI ___capi,
        GuiDialog.DlgComposers ___Composers, string ___composerKeyCode)
    {
        if (!Enabled || Blocked || __instance is null || ___capi?.World?.Player is not { } player || ___Composers is null) return true;
        var box = Shown.GetValue(__instance, static _ => new StrongBox<string>(""));
        var key = wis is null || wis.Length == 0 ? "" : Signature(wis, player);
        var held = key.Length > 0 && ___composerKeyCode is not null && Held(___Composers, ___composerKeyCode);
        if (held && key == box.Value)
        {
            if (Counting.Hud) Skipped++;
            return false;
        }

        box.Value = key;
        if (Counting.Hud) Composed++;
        return Assert(box.Value == key);
    }

    // The indexer looks up by TryGetValue: a missing composer is null, nothing thrown
    private static bool Held(GuiDialog.DlgComposers composers, string code) =>
        NotNull(composers) && Assert(code.Length > 0) && composers[code] is not null;

    // What ComposeBlockWorldInteractionHelp would draw: each line's action, keys and stacks, as it filters them
    private static string Signature(WorldInteraction[] wis, IClientPlayer player)
    {
        var (sb, block, entity) = (Line.Clear(), player.CurrentBlockSelection, player.CurrentEntitySelection);
        for (var i = 0; i < Math.Min(wis.Length, MaxInteractions); i++)
        {
            var wi = wis[i];
            if (wi is null) continue;
            var stacks = wi.Itemstacks;
            if (stacks != null && wi.GetMatchingStacks != null)
            {
                stacks = wi.GetMatchingStacks(wi, block, entity);
                if (stacks == null || stacks.Length == 0) continue;
            }

            if (stacks == null && wi.ShouldApply != null && !wi.ShouldApply(wi, block, entity)) continue;
            _ = sb.Append(wi.ActionLangCode).Append('|').Append((int)wi.MouseButton).Append('|').Append(wi.HotKeyCode)
                .Append('|').Append(wi.RequireFreeHand ? 1 : 0).Append('|');
            if (wi.HotKeyCodes is { } codes) _ = sb.AppendJoin(',', codes);
            _ = sb.Append(';');
            for (var s = 0; stacks != null && s < Math.Min(stacks.Length, MaxStacks); s++)
                _ = sb.Append((int)(stacks[s]?.Class ?? 0)).Append(':').Append(stacks[s]?.Id ?? 0).Append(',');
            _ = sb.Append('\n');
        }

        return Assert(wis.Length <= MaxInteractions) ? sb.ToString() : sb.Append(wis.Length).ToString();
    }
}
