using System.Globalization;
using Komet.Diagnostics;

namespace Komet.Hud;

// /komet profile <modid> [seconds]: report to clipboard, Logs/komet-debug, chat (top three) and a running capture.
internal sealed partial class HudOverlay
{
    private const float DefaultProfileSeconds = 10;
    private const int ChatTop = 3;
    private ModProfiler? _profile;

    private readonly List<(string Title, string Text)> _profiled = [];

    private void RegisterProfile(ICoreClientAPI capi)
    {
        if (!NotNull(capi.ChatCommands) || !Assert(DefaultProfileSeconds > 0)) return;
        _ = capi.ChatCommands.GetOrCreate("komet").BeginSubCommand("profile")
            .WithDescription(HudText.Translate("cmd-profile"))
            .WithArgs(capi.ChatCommands.Parsers.Word("modid"), capi.ChatCommands.Parsers.OptionalWord("seconds"))
            .HandleWith(args => ProfileCommand(args[0] as string ?? "", args[1] as string)).EndSubCommand();
    }

    private TextCommandResult ProfileCommand(string modId, string? seconds)
    {
        if (!Assert(modId.Length < 256) || !NotNull(_capi.ModLoader)) return TextCommandResult.Error("");
        if (modId == "stop")
        {
            if (_profile is null) return TextCommandResult.Error(HudText.Translate("cmd-profile-idle"));
            _profile.Stop();
            ProfileDone(_profile);
            return TextCommandResult.Success("");
        }

        var time = DefaultProfileSeconds;
        if (seconds is not null && (!float.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out time) ||
                                    time is <= 0 or > MaxSeconds))
            return TextCommandResult.Error(HudText.Translate("cmd-profile-usage"));
        _profile = ModProfiler.Start(_capi.ModLoader, modId, time, out var error);
        if (_profile is null) return TextCommandResult.Error(HudText.Translate("cmd-profile-" + error, modId));
        UpdateProfiler();
        return TextCommandResult.Success(HudText.Translate("cmd-profile-started", modId, _profile.Methods,
            DebugText.Num(time, "F0")));
    }

    private void ProfileFrame()
    {
        if (_profile is not { } profile || !Assert(profile.State != ModProfiler.Phase.Done)) return;
        var was = profile.State;
        profile.Step();
        if (was == ModProfiler.Phase.Patching && profile.State == ModProfiler.Phase.Measuring) Quiet(); // the patching frames
        if (profile.State == ModProfiler.Phase.Done) ProfileDone(profile);
    }

    private void ProfileDone(ModProfiler profile)
    {
        if (!NotNull(profile) || !Assert(profile.State == ModProfiler.Phase.Done)) return;
        _profile = null;
        UpdateProfiler();
        var report = profile.Report();
        (_profileResult, _profileHoldings, _profileOpen) = (profile, profile.Holdings(), "");
        if (_profileMod.Length == 0) _profileMod = profile.ModId;
        if (_debug is not null) _profiled.Add(($"Profile {profile.ModId}", report));
        var top = string.Join(", ", profile.Top(ChatTop).Select(static t => $"{t.Name} {DebugText.Num(t.Ms, "F3")} ms"));
        try
        {
            var folder = Path.Join(GamePaths.Logs, DebugFiles.Folder);
            _ = Directory.CreateDirectory(folder);
            var file = Path.Join(folder, string.Create(CultureInfo.InvariantCulture, $"profile-{profile.ModId}-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
            File.WriteAllText(file, DebugProtocol.Anonymize(report));
            _capi.Input.ClipboardText = DebugProtocol.Anonymize(report);
            _capi.ShowChatMessage(HudText.Translate("cmd-profile-done", profile.ModId, top, DebugProtocol.Anonymize(file)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _capi.ShowChatMessage(HudText.Translate("cmd-debug-failed", e.Message));
        }
    }
}
