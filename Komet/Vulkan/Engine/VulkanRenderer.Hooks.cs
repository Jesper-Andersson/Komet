using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Komet.Rendering;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;
using Komet.Gpu;
using static Komet.Rendering.Fields;

namespace Komet.Vulkan;

// Before 0.01 decides the frame; Preparing (0.993) runs after tesselator uploads, ahead of liquid depth (0.995). Every
// hook catches Vulkan exceptions and fails the renderer, not the game.
internal static partial class VulkanRenderer
{
    private const int MaxGlSamplers = 16;
    private const double PrepareOrder = 0.993, EarliestOrder = -1e9, LastOrder = 1e6, OitOrder = 0.0001;
    private const string StartingMark = "komet-vulkan-pre", StartedMark = "komet-vulkan-start";

    private static readonly Dictionary<int, Samplers.State> GlSamplers = [];
    private static readonly List<IRenderer> Renderers = [];
    private static int _distant = -1;
    private static bool _watched = true;

    private static HarmonyMethod Of(string name)
    {
        var method = AccessTools.Method(typeof(VulkanRenderer), name);
        _ = NotNull(method) && Assert(method.IsStatic);
        return new HarmonyMethod(method);
    }

    private static void Patch(Harmony harmony)
    {
        if (!NotNull(harmony) || !Assert(Programs.Length == Built.Length)) return;
        var platform = typeof(ClientPlatformWindows);
        _ = harmony.Patch(AccessTools.DeclaredMethod(typeof(VAO), nameof(VAO.Dispose)), Of(nameof(Disposing)));
        _ = harmony.Patch(AccessTools.DeclaredMethod(platform, nameof(ClientPlatformWindows.DisposeFrameBuffer)),
            Of(nameof(Unframing)));
        _ = harmony.Patch(AccessTools.DeclaredMethod(platform, nameof(ClientPlatformWindows.AllocateEmptyMesh)),
            postfix: Of(nameof(MeshAllocated)));
        foreach (var cairo in (ReadOnlySpan<string>)[nameof(ClientPlatformWindows.LoadCairoTexture),
                     nameof(ClientPlatformWindows.LoadOrUpdateCairoTexture)])
            if (AccessTools.DeclaredMethod(platform, cairo) is { } made) // the GUI's: never lean
                _ = harmony.Patch(made, Of(nameof(CairoMaking)), finalizer: Of(nameof(CairoMade)));
        var opaque = AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque), [typeof(float)]);
        _ = harmony.Patch(opaque, Of(nameof(OpaqueDrawing)));
        // after OcclusionCulling's postfix (installed before, same priority): its last pass records into the segment
        _ = harmony.Patch(opaque, postfix: new HarmonyMethod(AccessTools.Method(typeof(VulkanRenderer), nameof(OpaqueDrawn)))
        {
            priority = Priority.Last
        });

        IndirectDraw.Takeover = Takeover;
        PoolUpdates.Install(harmony, _logger);
        PatchBirths(harmony);
        var swap = AccessTools.Method(typeof(OpenTK.Windowing.Desktop.GameWindow), "SwapBuffers");
        if (NotNull(swap)) _ = harmony.Patch(swap, Of(nameof(Swapping)));
    }

    private static void Register(IClientEventAPI events)
    {
        if (!NotNull(events) || !Assert(Renderers.Count == 0)) return;
        Renderers.AddRange([
            new Hook(EnumRenderStage.Before, EarliestOrder, StartingFirst), new Hook(EnumRenderStage.Before, 0.01, StartingThen),
            new Hook(EnumRenderStage.Before, PrepareOrder, Preparing), new Hook(EnumRenderStage.Done, 0.99, Ended),
            new Hook(EnumRenderStage.OIT, OitOrder, OitBegins),
            new Hook(EnumRenderStage.Ortho, EarliestOrder, OrthoBegins), new Hook(EnumRenderStage.Ortho, LastOrder, OrthoEnds)
        ]);
        foreach (var renderer in Renderers.Bounded(32))
            events.RegisterRenderer(renderer, ((Hook)renderer).Own, "komet-vulkan");
    }

    private static void Unhook()
    {
        if (IndirectDraw.Takeover == Takeover) IndirectDraw.Takeover = null;
        Renderers.Clear();
        GlSamplers.Clear();
        (_distant, _watched, _oit) = (-1, true, (0, 0));
        (GlTap.ProgramGone, GlTap.Linking) = (null, null);
        _ = Assert(Renderers.Count == 0);
    }

    private static void Guarded(Action work)
    {
        if (!NotNull(work)) return;
        try
        {
            work();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(e);
        }
    }

    // Per-frame hooks: a static lambda with its state allocates nothing; a capturing one makes a closure per call
    private static void Guarded<T>(Action<T> work, T state)
    {
        if (!NotNull(work)) return;
        try
        {
            work(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(e);
        }
    }

    private static void Failed(Exception e)
    {
        Fail($"Vulkan failed: {e.GetType().Name}: {e.Message}");
        _logger?.Error("Komet: {0}", e);
        _ = Assert(State is Phase.Failed or Phase.Off);
    }

    private static bool _begunFirst; // began this frame ahead of the Before stage's renderers

    // The frame begins before every Before-stage renderer: their work (first frame too) is Vulkan's with GL resting, not
    // drawn by GL beforehand and missing from the picture
    private static void StartingFirst()
    {
        _begunFirst = false;
        if (!OnlyOn || VulkanCore.Device is null || !Assert(Renderers.Count > 0)) return; // the device starts at Before 0
        Starting();
        _begunFirst = State is Phase.Running or Phase.Waiting; // else Before 0.01 decides (the device's first frame)
        _ = Assert(!_begunFirst || OnlyOn);
    }

    // Before 0.01: the frame the device started in, or after a wait
    private static void StartingThen()
    {
        var first = _begunFirst;
        _begunFirst = false;
        if (!first) Starting();
        _ = Assert(!_begunFirst);
    }

    private static void Starting()
    {
        if (GlTap.Stranded) // until the world is left: OpenGL shows the GUI over a cleared frame
        {
            if (_renderer is not null) Fail("Vulkan-only stopped: the device cannot give OpenGL the world back");
            Overlaid();
            return;
        }

        var device = VulkanCore.Device;
        if (!OnlyOn) (_failed, _failure) = (false, ""); // switched off after a failure: switching on tries anew
        if (!OnlyOn || device is null)
        {
            Paused(OnlyOn ? "waits for the Vulkan device (render settings, Vulkan)" : "");
            return;
        }

        if (_failed) return;
        VulkanWatch.Mark("Before: starting", _renderer?.Frame.Number ?? -1);
        if (_renderer?.Frame.Fault is { Length: > 0 } fault)
        {
            Fail("Vulkan stopped: " + fault);
            return;
        }

        if (GlTap.Fault.Length > 0)
        {
            Fail("Vulkan stopped: " + GlTap.Fault);
            return;
        }

        Guarded(Begin, device);
    }

    private static void Begin(VulkanDevice device)
    {
        if (!NotNull(device) || _api?.Render is not { } render) return;
        DecideScene();
        if (!Tapped())
        {
            Fail("OpenTK's GL table is not where Vulkan looks for it");
            return;
        }

        Remade();
        if (State == Phase.Failed) return; // stranded as it let go
        _renderer ??= Made(device);
        if (_renderer is not { } renderer) return;
        renderer.Requads(ClientPlatformAbstract.singleIndexBufferSize / 24);
        Hooked(renderer, GlTap.Scene);
        _scene?.Warming(); // the programs ported as they were linked, their known pipelines begun
        if (!_watched) renderer.Rewatch();
        _watched = true;
        Decide(device);
        Update(Engine(), renderer, Taking);
        var waits = Joined(Unported(), SceneWaits());
        var (framebuffers, shared) = Windowing(renderer, render);
        // esr-pre held these: what ran before, then the GPU wait for the slot's frame (Frame.Next) and the start
        var profiler = Vintagestory.Client.ScreenManager.FrameProfiler;
        profiler?.Mark(StartingMark);
        var started = renderer.Start(framebuffers, shared, out var why);
        profiler?.Mark(StartedMark);
        if (!started)
        {
            Fail("Vulkan terrain: " + why);
            return;
        }

        Distant(renderer);
        Culling(renderer, Taking[(int)Pass.Opaque] && OcclusionCulling.Active);
        IndirectDraw.Plain = true;
        if (_scene is { } scene) (scene.Framing, scene.Between) = (SceneOn, false);
        Report(waits);
        // From now on GL runs no GPU work Vulkan leaves, nor (held by lean) while ports are awaited
        (GlTap.Resting, GlTap.Lean) = (GlIdle || GlTap.Leaned > 0, LeanOn);
        VulkanWatch.Mark("Before: started");
    }

    // Most frames wait for nothing: then no array, iterator or join
    private static string Joined(string a, string b)
    {
        if (!NotNull(a) || !NotNull(b) || a.Length + b.Length == 0) return "";
        return a.Length > 0 && b.Length > 0 ? a + "; " + b : a + b;
    }

    private static string SceneWaits()
    {
        if (!Assert(Parting.Length == 5)) return "";
        if (!GlTap.Scene) return $"the rest of the frame waits: OpenTK's GL table lacks {GlTap.Missing}";
        return _scene is null ? "the rest of the frame waits: no memory for it" : "";
    }

    private static TerrainRenderer? Made(VulkanDevice device)
    {
        var made = TerrainRenderer.Create(device, Math.Max(ClientPlatformAbstract.singleIndexBufferSize / 24, 1 << 20),
            _logger, out var why);
        if (made is null) Fail("Vulkan terrain: " + why);
        else
        {
            (made.Targets.Owned, made.RecordTaps) = (OitOwned, GlTap.Taps);
            made.Frame.Counted = VulkanFrame.SectionOf(Group(EnumRenderStage.Opaque) + " " + Named(Drawn.Terrain));
        }

        Array.Clear(Built); // its programs are ported anew
        return made;
    }

    // Every pass is Vulkan's: one left to resting OpenGL would be missing from the picture
    private static void Decide(VulkanDevice device)
    {
        Array.Fill(Taking, true);
        Suspended(OcclusionCulling.Enabled && !device.IndirectCount); // OpenGL's culled draws would be missing
        _ = Assert(Taking.Length == 5);
    }

    // Vulkan-only without drawIndirectCount: OcclusionCulling rests (switch kept), the opaque pass draws unculled
    private static void Suspended(bool suspend)
    {
        if (suspend == OcclusionCulling.Suspended || !Assert(!suspend || OnlyOn)) return;
        OcclusionCulling.Suspended = suspend;
        if (suspend)
            _logger?.Notification("Komet: Vulkan-only draws the opaque terrain without OcclusionCulling: the device cannot " +
                                  "draw with a count the GPU wrote (drawIndirectCount)");
        _ = Assert(OcclusionCulling.Suspended == suspend);
    }

    private static string Unported()
    {
        List<string>? missing = null; // made only when one is missing: asked every frame
        for (var i = 0; i < Math.Min(Programs.Length, Ports.Length); i++)
            if (Needed(i, Taking) && Built[i] > 0 && Ports[i] is null && Porting[i] is null)
                (missing ??= []).Add(Programs[i].Name);
        _ = Assert(Ports.Length == Programs.Length);
        return missing is null
            ? ""
            : $"no SPIR-V port of {string.Join(", ", missing)}: Vulkan leaves them out of the picture (see the log)";
    }

    private static void Preparing()
    {
        if (State is not (Phase.Running or Phase.Waiting) || _renderer is not { } renderer ||
            _api?.World is not ClientMain game || TerrainOf(game) is not { } chunks) return;
        Guarded(static s => s.renderer.Prepare(Adoptable(s.chunks)), (renderer, chunks));
    }

    private static List<(VAO, bool)> Adoptable(ChunkRenderer chunks)
    {
        Found.Clear();
        var passes = PassPools(chunks);
        if (!NotNull(passes) || !Assert(passes.Length < 32)) return Found;
        for (var pass = 0; pass < Math.Min(passes.Length, ChunkPasses); pass++)
        {
            if (passes[pass] is not { } managers || !Takes(pass)) continue;
            for (var m = 0; m < Math.Min(managers.Length, MaxManagers); m++)
            {
                if (managers[m] is not { } manager) continue;
                var pools = Pools(manager);
                for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
                    if (ModelRef(pools[i]) is VAO vao)
                        Found.Add((vao, Classic(pass)));
            }
        }

        _ = Assert(Found.Count <= 8 * MaxManagers * MaxPools);
        return Found;
    }

    private const int MaxManagers = 64, MaxPools = 4096;
    private static readonly List<(VAO, bool)> Found = [];

    // The far cache's layer is laid in while the opaque terrain draws (FarBegin), past FarRadius at large view distances
    private static void OpaqueDrawing()
    {
        if (FarCaching && OcclusionCulling.RowsEnabled && State == Phase.Running && _renderer is not null && _scene is not null &&
            FrameSettings.ViewDistance > TerrainRenderer.FarRadius + 64 &&
            _api?.World?.Player?.Entity?.CameraPos is { } eye && _api.Render is { } render)
            Guarded(static c => _renderer?.FarBegin(c.Render.CameraMatrixOriginf, c.Render.PerspectiveProjectionMat, c.Eye,
                _scene.FarReadied), (Render: render, Eye: eye));
        _ = Assert(Taking.Length == 5);
    }

    // RenderOpaque's postfix: the far cache's layer is done
    private static void OpaqueDrawn() => Guarded(static () =>
    {
        if (_renderer is { } renderer && _scene is { } scene) renderer.FarEnd(scene.FarLay);
    });

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "revealTextureId")]
    private static extern ref int Reveal(SystemRenderOITLayers? layers);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "accumTextureId")]
    private static extern ref int Accumulation(SystemRenderOITLayers? layers);

    private static (int Reveal, int Accumulation) _oit;

    // TerrainTargets.Owned: the OIT reveal texture and accumulation layers named in SystemRenderOITLayers' statics
    private static bool OitOwned(int texture) =>
        Assert(texture > 0) && (texture == Reveal(null) || texture == Accumulation(null));

    // Right after the engine's BeforeOIT (order 0), which attaches reveal and accumulation to the transparent
    // framebuffer (anew after a resize): new ones are asked for again
    private static void OitBegins()
    {
        if (!Taking[(int)Pass.Transparent] || _renderer is not { } renderer) return;
        var now = (Reveal(null), Accumulation(null));
        if (now == _oit || !Assert(now.Item1 >= 0) || !Assert(now.Item2 >= 0)) return;
        _oit = now;
        Guarded(renderer.ForgetTargets);
    }

    // With the window's stand-in the frame runs on to the buffer swap (Swapping) and ends there instead
    private static void Ended()
    {
        if (_windowing || _renderer is not { } renderer || !Assert(renderer.Frame.Number >= 0)) return;
        VulkanWatch.Mark("Done", renderer.Frame.Number);
        _ = Finished(renderer, null);
    }

    // False when Vulkan presented the frame: the engine's SwapBuffers must not
    private static bool Swapping(OpenTK.Windowing.Desktop.NativeWindow __instance)
    {
        if (!_windowing || _renderer is not { } renderer || !NotNull(__instance)) return true;
        VulkanWatch.Mark("the buffers swap", renderer.Frame.Number);
        _ = Assert(renderer.Frame.Number >= 0);
        return !Finished(renderer, __instance);
    }

    // True once shown in the window; without one, closes the last segment. Not Guarded: a closure a frame.
    private static bool Finished(TerrainRenderer renderer, OpenTK.Windowing.Desktop.NativeWindow? window)
    {
        if (!NotNull(renderer) || !Assert(window is null || _windowing)) return false;
        if (_scene is { } scene) scene.Framing = SceneOn; // what comes between frames too
        var shown = false;
        try
        {
            if (window is null) renderer.Frame.Close("the frame's end", endsFrame: true);
            else shown = renderer.Present(window);
            if (renderer.Unshowable is { Length: > 0 } unshowable)
            {
                Fail($"Vulkan-only needs Vulkan to show its frames, but {unshowable}: OpenGL draws the world (join the " +
                     "world again to try once more)");
                return false;
            }

            if (_scene is { } next) next.Between = SceneOn && State == Phase.Running;
            if ((State is Phase.Running or Phase.Waiting) && !MemoryGuard(renderer)) Periodically(renderer);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(e);
        }

        _ = Assert(renderer.Frame.Number >= 0);
        return shown;
    }

    private static bool Takeover(MeshRef modelRef, int[] indices, int[] sizes, int count, bool useSSBOs)
    {
        if (modelRef is not VAO vao) return false;
        var pass = Taken(out var renderer);
        if (pass == Pass.None) return false;
        Mark(Drawn.Terrain);
        try // not Guarded: a closure per draw would feed the garbage collector hundreds a frame
        {
            return renderer.Take(vao, (indices, sizes, count, useSSBOs), Sampler);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail($"Vulkan failed drawing: {e.GetType().Name}: {e.Message}");
            _logger?.Error("Komet: {0}", e);
            return false;
        }
    }

    // The culling buffers are made anew with the backend they run on at the next frame
    private static void Culling(TerrainRenderer renderer, bool on)
    {
        if (!NotNull(renderer)) return;
        GpuBackends.Current = on ? renderer.Compute : GlBackend.Instance;
        OcclusionCulling.Drawer = on ? Counted : null;
        OcclusionCulling.Takes = on ? static () => Taken(out _) != Pass.None : null;
        _ = Assert(on == (OcclusionCulling.Drawer is not null));
    }

    // The pass Vulkan draws the bound program's draw of in now, Pass.None for OpenGL
    private static Pass Taken(out TerrainRenderer renderer)
    {
        renderer = _renderer!;
        if (State is not (Phase.Running or Phase.Waiting) || _renderer is null || _api?.World is not ClientMain game)
            return Pass.None;
        var pass = PassOf(GlTap.Program, Stage(game));
        return pass != Pass.None && Assert(pass >= 0) && Index((int)pass, Taking.Length) && Taking[(int)pass] &&
               renderer.Takes(GlTap.DrawFramebuffer) ? pass : Pass.None;
    }

    private static bool Runs(Pass pass) =>
        State == Phase.Running && Assert(pass != Pass.None) && Index((int)pass, Taking.Length) && Taking[(int)pass];

    private static bool Counted(VAO vao, bool ssbo, (int Buffer, int Offset) commands, (int Buffer, int Offset) count,
        int max)
    {
        VulkanWatch.Mark("culling: a counted draw");
        if (State is not (Phase.Running or Phase.Waiting) || _renderer is not { } renderer || !NotNull(vao) ||
            !Assert(commands.Offset >= 0 && count.Offset >= 0)) return false;
        Mark(Drawn.Terrain);
        try // every pool's draw: no closure for Guarded
        {
            var backend = renderer.Compute;
            var (listed, listedAt) = backend.Native(commands.Buffer);
            var (counted, countedAt) = backend.Native(count.Buffer);
            return renderer.TakeCounted(vao, ssbo,
                ((listed, listedAt + (ulong)commands.Offset), (counted, countedAt + (ulong)count.Offset), max), Sampler);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(e);
            return false;
        }
    }

    // What Vulkan holds stays, so switching on again needs no pool adopted anew
    private static void Paused(string why)
    {
        if (Strands(why.Length > 0 ? why : "was switched off"))
        {
            Fail("Vulkan-only (lean) cannot hand the world to OpenGL: join it again"); // OpenGL stays tapped and resting
            return;
        }

        if (State == Phase.Running && _renderer is { } only) HandBack(only); // once, as it stops
        Unpushed();
        if (_renderer is { } renderer)
        {
            renderer.Frame.Close("Vulkan switched off");
            renderer.Unwindowed(); // the window is OpenGL's again before GlTap lets go
            Culling(renderer, false);
        }

        Array.Clear(Taking);
        (State, Status) = (_failed ? Phase.Failed : Phase.Off, _failed ? _failure : why);
        // the record tapped at the start stays while Vulkan is on: what it hears now Vulkan needs later
        if (!GlTap.Tapped || (OnlyOn && GlTap.Recording && !VulkanCore.Failed)) return;
        GlTap.Untap();
        _watched = false;
        _ = Assert(!GlTap.Tapped);
    }

    // Set by a running renderer, cleared on any exit: GL draws (GlTap.Resting), culls, draws indirectly, owns the
    // window
    private static void Unpushed()
    {
        (_windowing, OcclusionCulling.Suspended, IndirectDraw.Plain, GlTap.Resting) = (false, false, false, false);
        if (_scene is { } scene) scene.Framing = false;
        _ = Assert(!GlTap.Resting) && Assert(!IndirectDraw.Plain);
    }

    private static void Fail(string why)
    {
        var stranded = Strands(NotNull(why) ? $"failed ({why})" : "failed");
        (_failed, _failure) = (true, NotNull(why) ? why : "?");
        Unpushed();
        (State, Status) = (Phase.Failed, _failure);
        Array.Clear(Taking);
        if (!stranded && _renderer is { } only) HandBack(only);
        try
        {
            Release();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger?.Error("Komet: Vulkan could not let go cleanly: {0}", e);
        }
        finally
        {
            _renderer = null; // abandoned: OpenGL keeps every name it had
        }

        if (!stranded) GlTap.Untap(); // stranded: tapped and resting until the world is left
        _watched = false;
        _logger?.Warning("Komet: {0}", _failure);
        _ = Assert(_failed);
    }

    // Komet's distant shadow cascade redraws its map: Vulkan drew it (no change), else its image is read back on next
    // use, unless GL rests (drew nothing more)
    private static void Distant(TerrainRenderer renderer)
    {
        if (!NotNull(renderer) ||
            !DistantShadows.Published(out var texture, out var version, out _, out var onTexel)) return;
        if (version == _distant || !Assert(onTexel.Length == 3)) return;
        _distant = version;
        if (!GlTap.Resting) renderer.Images.Changed(texture);
    }

    private static void Disposing(VAO __instance)
    {
        if (_renderer is { } renderer && NotNull(__instance) && Assert(__instance.VaoId >= 0))
            Guarded(static s => s.renderer.Release(s.__instance), (renderer, __instance));
    }

    private static void Unframing(FrameBufferRef frameBuffer)
    {
        if (_renderer is { } renderer && NotNull(frameBuffer) && Assert(frameBuffer.FboId >= 0))
            Guarded(static s => s.renderer.Release(s.frameBuffer), (renderer, frameBuffer));
    }

    private static readonly System.Func<int, Samplers.State> Sampler = SamplerObject;

    private static Samplers.State SamplerObject(int id)
    {
        if (!Assert(id > 0) || !Assert(GlSamplers.Count <= MaxGlSamplers)) return default;
        if (GlTap.SamplerOf(id) is { } recorded) return recorded; // followed as it changes: no cache
        if (GlSamplers.TryGetValue(id, out var known)) return known;
        GL.GetSamplerParameter(id, SamplerParameterName.TextureMinFilter, out int min);
        GL.GetSamplerParameter(id, SamplerParameterName.TextureMagFilter, out int mag);
        GL.GetSamplerParameter(id, SamplerParameterName.TextureWrapS, out int wrap);
        GL.GetSamplerParameter(id, SamplerParameterName.TextureCompareMode, out int compare);
        var state = Samplers.FromGl(min, mag, wrap, compare != 0);
        if (GlSamplers.Count < MaxGlSamplers) GlSamplers[id] = state;
        return state;
    }

    private sealed class Hook(EnumRenderStage stage, double order, Action work) : IRenderer
    {
        public EnumRenderStage Own { get; } = stage;
        public double RenderOrder => order;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (Assert(stage == Own) && Assert(deltaTime >= 0)) work();
        }

        public void Dispose() => _ = Assert(Renderers.Count <= 32) && Assert(RenderRange == 0);
    }
}
