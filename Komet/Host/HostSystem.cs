using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using HarmonyLib;
using Komet.Core;
using Komet.World;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace Komet.Host;

// Server side of HostLaunch. In-process, StartPre is the handover point. In the child (ParentVariable set) it keeps
// singleplayer rules (highest role) and stops like /stop when the player leaves or the game is gone. Pause arrives on a
// loopback link thread: a suspended server runs no main thread tasks.
public sealed class HostSystem : ModSystem, IDisposable
{
    private const int WatchMs = 1000, MaxMessages = int.MaxValue;

    private ICoreServerAPI? _api;
    private Harmony? _harmony;
    private int _parent;
    private TcpClient? _link;

    public override bool ShouldLoad(EnumAppSide forSide) => Assert(Enum.IsDefined(forSide)) && forSide == EnumAppSide.Server;

    public override void StartPre(ICoreAPI api)
    {
        if (api is not ICoreServerAPI server || !NotNull(Mod.Logger)) return;
        var parent = Environment.GetEnvironmentVariable(HostLaunch.ParentVariable);
        if (parent is null)
        {
            if (!server.Server.IsDedicated) HostLaunch.Handover(server);
            return;
        }

        Attach(Mod.Logger);
        if (!Assert(int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out _parent))) return;
        var join = AccessTools.Method(typeof(ServerMain), "HandleRequestJoin");
        if (!NotNull(join)) return;
        _harmony = new Harmony("komet.host.child");
        _ = _harmony.Patch(join, prefix: new HarmonyMethod(typeof(HostSystem), nameof(Join)));
        ServerFeatures(_harmony, Mod.Logger);
        _api = server;
        Mod.Logger.Notification("Komet: running a singleplayer world for game process {0}", _parent);
        if (int.TryParse(Environment.GetEnvironmentVariable(HostLaunch.ControlVariable), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port)) Connect(port, server.World as ServerMain);
    }

    // Server-code patches, installed client side only while the server runs in-process: here every feature the table
    // marks ServerOnly, in its order, its switches as komet.json saved them
    private static void ServerFeatures(Harmony harmony, ILogger logger)
    {
        if (!NotNull(harmony) || !NotNull(logger)) return;
        var features = Features.All;
        var context = FeatureContext.Server(harmony, logger);
        for (var i = 0; i < Math.Min(features.Length, Features.MaxFeatures); i++)
        {
            if (!features[i].ServerOnly) continue;
            foreach (var knob in features[i].Knobs.Bounded(Knobs.MaxKnobs))
                if (knob.IsSwitch && HostLaunch.Saved(knob.Persisted) is { } on) knob.Set(on ? 1 : 0);
            features[i].Install?.Invoke(context);
        }
    }

    private void Connect(int port, ServerMain? server)
    {
        if (!Assert(port is > 0 and < 65536) || !NotNull(server) || !Assert(_link is null)) return;
        try
        {
            _link = new TcpClient();
            _link.Connect(IPAddress.Loopback, port);
            var stream = _link.GetStream(); // here: Dispose may null _link before the thread runs
            new Thread(() => Listen(stream, server)) { IsBackground = true, Name = "komet-host-link" }.Start();
        }
        catch (SocketException e)
        {
            Mod.Logger.Warning("Komet: no link to the game process, it cannot pause this server: {0}", e.Message);
        }
    }

    // Suspend takes its own lock and is what the in-process server's thread calls between two ticks
    private void Listen(NetworkStream stream, ServerMain server)
    {
        if (!NotNull(stream) || !NotNull(server)) return;
        Span<byte> message = stackalloc byte[1];
        _ = Assert(message.Length == 1);
        try
        {
            for (var i = 0; i < MaxMessages; i++)
            {
                if (stream.Read(message) == 0) break;
                _ = server.Suspend(message[0] == HostLaunch.Paused);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Mod.Logger.Notification("Komet: link to the game process lost: {0}", e.Message);
        }

        _ = server.Suspend(false);
        Stop("the game left the world or ended");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        if (_api is null || !NotNull(api) || !NotNull(api.Event) || !Assert(_parent > 0)) return;
        api.Event.PlayerDisconnect += _ => Stop("the player left");
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, LightRepair.Recheck); // once every mod has patched
        _ = api.Event.RegisterGameTickListener(_ =>
        {
            if (!Alive(_parent)) Stop("the game process ended");
        }, WatchMs);
    }

    public override void Dispose()
    {
        _ = Assert(_harmony is null || _parent > 0); // patched only in a child
        _harmony?.UnpatchAll(_harmony.Id);
        _link?.Dispose();
        (_harmony, _api, _link) = (null, null, null);
    }

    // Once (tick, disconnect, link thread; ShutDown disconnects again); queued so the leave completes first
    private void Stop(string why)
    {
        if (!Assert(why.Length > 0) || Interlocked.Exchange(ref _api, null) is not { } api || !NotNull(api.Server)) return;
        Mod.Logger.Notification("Komet: stopping, {0}", why);
        api.Event.EnqueueMainThreadTask(api.Server.ShutDown, "komet-host-stop");
    }

    private static bool Alive(int pid)
    {
        if (!Assert(pid > 0)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    // As HandleRequestJoin does for its in-memory client
    private static void Join(ServerMain __instance, ConnectedClient client)
    {
        if (!NotNull(__instance) || client?.Player?.serverdata is not { } data || !NotNull(__instance.Config.Roles)) return;
        if (__instance.Config.Roles.MaxBy(static role => role.PrivilegeLevel) is { } top) data.RoleCode = top.Code;
    }
}
