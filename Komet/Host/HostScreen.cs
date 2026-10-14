using Vintagestory.Client;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.Host;

// Between world click and join: waits for the handed-over server and closes its savegame (early stop leaves it open),
// waits for a previous child still saving, starts the child, joins. Child ends or times out: engine singleplayer start.
internal sealed class HostScreen : GuiScreen
{
    private const long MaxStartMs = 240_000;

    private readonly StartServerArgs _args;
    private ServerMain? _server;
    private Step _step, _shownStep = Step.Done;
    private long _since;
    private bool _cancelled;
    private string? _shownLine;

    private enum Step
    {
        InProcess,
        Previous,
        Launch,
        Starting,
        Done
    }

    public HostScreen(ScreenManager manager, StartServerArgs args, ServerMain? server) : base(manager, null)
    {
        _ = Assert(NotNull(args) && NotNull(manager));
        (_args, _server) = (args, server);
        (_step, _since) = (server is null ? Step.Previous : Step.InProcess, ScreenManager.Platform.EllapsedMs);
        _ = NotNull(ScreenManager.GuiComposers);
        var text = ElementBounds.Fixed(0, 0, 900, 120);
        var inner = text.ForkBoundingParent(10, 10, 10, 10);
        var outer = inner.ForkBoundingParent(0, 50, 0, 100).WithAlignment(EnumDialogArea.CenterFixed).WithFixedPosition(0, 280);
        ElementComposer = ScreenManager.GuiComposers.Create("komet-host", outer).BeginChildElements(inner)
            .AddStaticCustomDraw(ElementBounds.Fill, static (ctx, _, b) =>
            {
                RoundRectangle(ctx, b.bgDrawX, b.bgDrawY, b.OuterWidth, b.OuterHeight, 1);
                ctx.SetSourceRGBA(GuiStyle.DialogLightBgColor);
                ctx.Fill();
            })
            .AddDynamicText("", CairoFont.WhiteSmallishText().WithOrientation(EnumTextOrientation.Center), text, "text")
            .EndChildElements()
            .AddButton(Lang.Get("Cancel"), Cancel, ElementStdBounds.MenuButton(4f).WithFixedPadding(10, 4), EnumButtonStyle.Normal,
                "cancel")
            .Compose();
    }

    public override void RenderToDefaultFramebuffer(float dt)
    {
        _ = Finite(dt);
        Advance(ScreenManager.Platform.EllapsedMs);
        if (_step == Step.Done) return; // the next screen is loaded
        Show();
        base.RenderToDefaultFramebuffer(dt);
    }

    private void Advance(long now)
    {
        if (!Assert(now >= _since)) _since = now;
        switch (_step)
        {
            case Step.InProcess when !ScreenManager.Platform.IsServerRunning:
                if (NotNull(_server)) HostLaunch.Close(_server);
                (_server, _step, _since) = (null, _cancelled ? Step.Done : Step.Previous, now);
                if (_cancelled) HostLaunch.MainMenu(ScreenManager);
                break;
            // however long it saves: it may hold a played world
            case Step.Previous when HostLaunch.Child is not { HasExited: false }:
                _step = Step.Launch;
                break;
            case Step.Previous:
                HostLaunch.Release();
                break;
            case Step.Launch:
                if (HostLaunch.Start(_args)) (_step, _since) = (Step.Starting, now);
                else Fail("did not start");
                break;
            case Step.Starting when HostLaunch.Ready:
                HostLaunch.Restore();
                HostLaunch.Accept();
                _step = Step.Done;
                ScreenManager.ConnectToMultiplayer("127.0.0.1:" + HostLaunch.Port, HostLaunch.Password);
                break;
            case Step.Starting when HostLaunch.Child is not { HasExited: false } || now - _since > MaxStartMs:
                Fail(HostLaunch.Line);
                break;
        }
    }

    private void Fail(string why)
    {
        _ = Assert(_step != Step.Done) && NotNull(why);
        ScreenManager.Platform.Logger.Warning("Komet: the server process failed ({0}), the world starts in the game's process", why);
        HostLaunch.Abort();
        HostLaunch.Restore();
        _step = Step.Done;
        HostLaunch.Fallback(ScreenManager, _args);
    }

    // While the in-process server stops, Cancel only marks; its savegame closes once it is gone
    private bool Cancel()
    {
        if (!Assert(_step != Step.Done)) return true;
        if (_step == Step.InProcess)
        {
            _cancelled = true;
            return true;
        }

        if (_step == Step.Starting) HostLaunch.Abort(); // a previous child keeps saving
        HostLaunch.Restore();
        _step = Step.Done;
        HostLaunch.MainMenu(ScreenManager);
        return true;
    }

    private void Show()
    {
        var line = HostLaunch.Line;
        if (!NotNull(ElementComposer)) return;
        if (_step == _shownStep && ReferenceEquals(line, _shownLine)) return;
        (_shownStep, _shownLine) = (_step, line);
        var key = _step switch
        {
            Step.InProcess => "host-handover",
            Step.Previous => "host-previous",
            _ => "host-starting"
        };
        ElementComposer.GetDynamicText("text")?.SetNewText(T(key) + "\n\n" + line);
    }

    // Client mods (Komet's lang) load only after the join; the first world's handover runs before
    private static string T(string key)
    {
        var text = Lang.Get("komet:" + key);
        return Assert(key.Length > 0) && text != "komet:" + key ? text : "Komet: starting the world's server...";
    }
}
