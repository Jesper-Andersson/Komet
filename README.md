# Komet 2.0.0

Client-side performance mod for Vintage Story 1.22 (C#, .NET 10). Komet replaces expensive parts of the engine with code that
produces the same result, measures itself in game and shows a HUD with frame times, lows, spikes and counters. The goal is the
1 % and 0.1 % lows, not the average FPS.

- **HUD**: F7 a compact overlay (FPS, 1 % low, frame time, graph, mods, a hint naming each spike's cause), Ctrl+F7 a window
  with tabs (overview with hints that fix what they name on a click, frames, system, render, threads, mods with pinning, timing
  and patch risks, log, settings), Ctrl+F8 the debug window (captures and the protocol, see below).
- **Options menu**: replaces the in-game settings (Escape → Settings) with a menu in the style of Sodium for Minecraft; also
  via `.komet`. Details below. Komet's own settings live in `ModConfig/komet.json`.
- **Update notice**: once you agree, Komet checks GitHub at startup for a newer build of its own channel.

Komet runs on the client only (in singleplayer including the built-in server). If an engine method is missing or another mod
patches the same method, the engine does the work, and the log or the HUD says so.

## Features

Switches are defined in `Komet/Core/Features/Features.cs`; off or 0 means the engine does the work. All features are on by default.

**Rendering**
- **FrustumSweep**: vectorized chunk culling, on the worker threads from 8,192 entries; 0.97 instead of 2.3 ms per frame.
  Culling runs per render stage: the first chunk render call of a stage hands the pools of all passes of that stage
  to the workers at once, including the sync of the pool mirrors; the main thread only waits for the pass it is drawing. Together
  with GlErrorPoll in an A/B: 104.6 → 120.3 FPS, 1 % low 35.4 → 39.1 FPS, frames over 25 ms 53 → 33 per minute.
- **GlErrorPoll**: the two GL error checks per frame only ask the driver every 16th time; under Mesa's glthread each one waits for
  the driver thread (0.13 ms per frame). No error is lost, and in GL debug mode every check asks.
- **AnimatableCulling**: animated block entities outside the view and shadow volumes are not drawn; the GL state stays as a drawn
  one leaves it.
- **IdleAnimators**: the render loop no longer calls idle block entity animations (chests, doors, querns) every frame; 8,000 calls
  per frame in the test world, 0.1 ms.
- **SunOcclusion**: the sun's occlusion query only every fourth frame (under Mesa every `glGet*` is a sync).
- **ShaderUseCache**: `Use` uploads only the uniforms that changed; 208 B → 0 garbage per call.
- **DistantShadows**: shadows up to the view distance. The engine has two shadow maps, and the far one already fades out from
  40 % of its range: at the highest quality every shadow ends after about 200 blocks. A third map (up to 4096², up to 1,120
  blocks radius) covers the rest, aligned to the world's texel grid so nothing shimmers. It is only redrawn when the sun has moved
  0.3°, you have travelled 96 blocks or 20 s have passed, one sixteenth per frame. The shaders only use it where the engine's two
  maps run out; with another mod's shader pack the feature stays off.
- **IndirectDraw**: multi-draws as indirect commands from a mapped GPU buffer (needs `GL_ARB_multi_draw_indirect`).
- **WindowSizeCache**: the window size is cached instead of a dozen GLFW queries per frame.
- **MeshPool**: inserting into and removing from mesh pools without a walk over all entries; 1,000 removals 28 → 0.8 ms.
- **PoolScale** (4, 1–8): the second mesh pool of a chunk pass is twice, every further one four times the size of
  `modelDataPoolMaxVertexSize` (500,000): the same meshes in a quarter of the pools, and so of the draw calls and culling jobs.
  One run each with plain and with fourfold pools: 134 → 144 FPS, 1 % low 41 → 45, 0.1 % low 31 → 35, frames over 25 ms
  26 → 15 per minute. Applies to newly created pools, fully after re-entering the world.
- **MeshRecycle**: recycled meshes keep their extra data arrays; dropped custom part arrays are taken by the next tessellation.
- **UploadCap** (3 ms, 0–20): chunk uploads per frame are capped, priority chunks never.

**Chunks and tessellation** — ExtendedRows, VisibleFaces, FaceLight and OccludedChunks are verified bit for bit against the engine
and only install when the IL fingerprint matches (1.22.7). Together: a quarter less time per tessellation, and the world is
complete about 19 s sooner after joining.
- **ChunkLookup**: `GetChunk` from a mirror instead of under `chunksLock`.
- **DecompressScratch**: chunk layers are unpacked without a throwaway copy; about 8 MB/s less garbage while loading.
- **TessSchedule**: nearest chunk first (weighted by view direction); near chunks wait half as long.
- **WorkerThreads** (cores − 2, 1–8, at most) and **TessPriority** (25 %, 0–100): a shared pool `komet-worker-N` for culling and tessellation; how many threads tessellate Komet decides by itself, the priority sets what comes first (0 % smooth frames, 100 % fast chunk loading).
  Frame tasks come first; from 3,000 waiting chunks half of the threads tessellate, all of them from priority 50 %. TessSafety makes shared engine state per thread or
  locks it while workers tessellate.
- **ExtendedRows**: the chunk and its neighbour shell are copied row by row; 400–600 instead of 820–1,250 ns per row.
- **VisibleFaces**: a bit formula for `FaceCullMode.Default`; per chunk 370–470 → 100–120 µs underground.
- **FaceLight**: ambient light from bits, four corners at once; 90–95 → 50–57 ns per face.
- **OccludedChunks**: chunks enclosed on all sides are not tessellated.
- **LightScratch**: block light with reused objects; placing/removing a lantern 4.5 MB → 2.4 KB.
- **LightRepair** (singleplayer only): chunks saved without any light although they reach up to the sky are drawn black and count
  as caves. They appear when `FullRelight` (WorldEdit, `/debug relight`, the timeswitch, `/wgen`) runs over columns that are not
  fully loaded: it clears their light and never computes it again. It also places light sources again at doubly offset
  coordinates, so torches in the relit area lose their light. Komet replaces `FullRelight` with the same computation without both
  faults: a column loaded only in part keeps its light and is relit as soon as it is fully loaded. A column that already lost its
  light gets sun and block light back when it loads, the way the engine lights a whole column. Whatever that misses, such as
  columns the server loaded before Komet started, a sweep every 200 ms on the server tick finds; it repairs at most two columns
  per tick. Everything repaired is saved. In the test world 2,800 of 5,200 loaded columns had such chunks; afterwards not a single
  chunk arrived dark at the client (before: 2,200 permanently black).
- **ParticleLight**: particles only take the chunk lock when it is free; removes minutes-long 8–13 ms frames near beehives
  (1 % low 45.6 → 50.2 FPS, frames over 25 ms 28 → 1 per minute).

**Entities**
- **AnimationFrames**: animation frames from the cache, otherwise compiled quickly, otherwise by the engine.
- **InitOnce**: skips the player's duplicate shape initialization.
- **ShapeInitMemo**: skips repeated initialization of unchanged entity shapes; `moose` 0.5 → 0.09 ms.
- **EntityTessBudget** (4 ms, 0–50): spreads entity tessellations over several frames.

**Allocations and startup**
- **ClimateCache**: the climate map cache follows the view distance instead of a fixed 10 regions of 1 MiB.
- **ColumnNoiseScratch**: terrain noise without allocations (singleplayer world generation only).
- **CloudTileScratch**: cloud tiles without a `Vec3d` per tile; saves 33 MB/s.
- **PartitionReuse**: the built-in server's entity partitioning reuses its lists; 7 MB/s less garbage.
- **TessBlockPos**: the tessellator asks plants and crops for their sink offset without a new `BlockPos`; 1.8 MB/s.
- **CookingMatch**: firepits with a cooking pot check the cooking recipes without copies; standing still 16.9 → 12.6 MB/s garbage,
  gen0 GCs 2.2 → 1.6 per second.
- **HandlerLists**: climate and wind queries only copy their event's handlers again when the event has changed; 0.7 MB/s.
- **ChunkThreadClosure** (no switch): the built-in server's chunk thread no longer allocates a closure per request.
- **PreJit**: compiles the API, the library and the vanilla mods ahead of time on a background thread when joining a world.

**Diagnostics**: TessAccounting books every tessellation run by kind. The HUD and the benchmark read the same frame clock;
1 % low = 1000·k / (sum of the k longest frame times in ms), k = n/100.

## Options menu and API for other mods

Escape → Settings opens Komet's full-screen options menu in the style of Sodium:
- **Layout:** the search over all options at the top; on the left the sidebar with **Vintage Story** (General, Quality,
  Performance, Mouse, Controls, Accessibility, Sound, Interface, Developer), **Komet** (HUD, Tools, Rendering, Chunks, Misc) and the
  pages of other mods.
- **List:** in the middle all pages of the chosen section one below the other, on the right the description of the option under
  the mouse.
- **Apply:** changes are collected and only take effect with **Apply** or **Done** (shaders are reloaded only once); Escape
  discards them.
- **Choices:** with few options a click moves to the next one (right-click goes back); with many, such as the language (applies
  after a restart), a click on the right opens a window listing all entries.
- **Controls:** all key bindings and mouse actions of the game on one page; click, then press the new key to rebind (Escape
  cancels), right-click restores the default, keys bound twice are shown in red. Takes effect immediately, as in the game.
- **Original graphics menu:** as a button at the end of the Interface page.
- **Back to the game's menu:** with the `GraphicsMenu` switch (Misc → Options menu), and automatically when a game update changes
  one of the rebuilt tabs or another mod patches it.

A mod hooks in by referencing `Komet.dll` (`Private="false"`) and creating its page in `StartClientSide`, from a class of its own
that it only calls when `api.ModLoader.IsModEnabled("komet")`:

```csharp
KometOptions.Page("mymod", Lang.Get("mymod:title"))
    .Group(Lang.Get("mymod:rendering"))
    .Switch(Lang.Get("mymod:water"), () => config.Water, on => config.Water = on, Lang.Get("mymod:water-hint"))
    .Slider(Lang.Get("mymod:detail"), 1, 8, 1, () => config.Detail, v => config.Detail = (int)v)
    .Choice(Lang.Get("mymod:mode"), ["A", "B", "C"], () => config.Mode, i => config.Mode = i)
    .Button(Lang.Get("mymod:reset"), Lang.Get("mymod:do-reset"), config.Reset);
```

The mod saves in its setters; Komet calls them on Apply. The third parameter of `Page` is the name in the sidebar (default: the
title); if it matches the mod, the mod's version is shown below it. `Format(...)` and `EnabledWhen(...)` apply to the row created
last. `KometOptions.Applied` reports every Apply. When the world closes, Komet drops all pages and subscribers.

### Querying, holding and registering features

`KometFeatures` (same rules as `KometOptions`) knows every feature by its id: Komet's as above, other mods' as `modid:name`.
`Snapshot()` lists all of them in install order, `StateOf(id)` returns `Pending`, `Active`, `Off` (switched off by the player),
`HeldOff`, `StoodDown` (another mod patches the same method), `EngineChanged`, `NotApplicable` or `Failed`. A mod that does not get
along with a feature holds it on the game's behaviour instead of changing the player's setting:

```csharp
var hold = KometFeatures.HoldOff("ChunkBudget", "mymod", "own upload control"); // also by switch: "UploadCap"
hold.Release(); // the player's choice applies again
```

While a hold is active, the switch in the options menu is locked (showing the mod and the reason); the HUD window's Mods tab
lists all features that are not active. `KometFeatures.StateChanged` reports every change; a handler that throws is logged once and
unsubscribed.

A mod registers its own features in `Start` or `StartClientSide`; after that Komet refuses. Komet installs them after its own,
shows, holds and benchmarks their switch, and stands down while another mod patches the watched methods:

```csharp
KometFeatures.Register(new FeatureDefinition("mymod", "water", Lang.Get("mymod:water"))
{
    Page = KometFeatures.RenderPage, Group = Lang.Get("mymod:title"), Hint = Lang.Get("mymod:water-hint"),
    Knob = FeatureKnob.Switch(() => config.Water, on => config.Water = on, on => WaterPatch.Enabled = on),
    Install = (harmony, logger) => harmony.CreateClassProcessor(typeof(WaterPatch)).Patch(),
    Shaped = () => [WaterPatch.Target], Fingerprint = 0x0123_4567_89AB_CDEFUL,
    Watched = () => [WaterPatch.Target]
});
```

The first two functions of the switch are the player's saved value, the third is what the patches read (the engine value while
held). The id and the Harmony id are `mymod:water`, in the benchmark as well. `Page` is `RenderPage`, `ChunksPage`, `MiscPage` or a
`KometOptions` page of your own. If the fingerprint of the `Shaped` methods does not match, the feature stays out
(`EngineChanged`); a test of the mod pins the value with `KometFeatures.Fingerprint(...)` against the installed game. If `Install`
throws, Komet removes the patches (`Failed`). When the world closes, Komet calls `Uninstall`, removes the patches and forgets the
registration; in the next world the mod registers again.

## Debugging performance

`/komet debug` records everything Komet measures for 10 seconds (HUD shown or not) and writes a plain-text protocol: findings
with the section that proves them, system, game and Komet settings, frame statistics with a histogram, the dearest frames with
their profiler marks, memory and GC, JIT and threads, world and rendering, time per mod, every method several mods patch with its
patch chain and risk, the log's warnings and errors, and a comparison with the previous protocol. It goes to the clipboard and to
`Logs/komet-debug/<time>/` with `frames.csv` (every frame), `harmony.txt` (the whole patch registry) and `summary.txt`. Paths name
no account.

```text
/komet debug                 10 s
/komet debug 60 de           60 s, protocol in German (default English, whatever the game's language)
/komet debug now             3 s
/komet debug spike 40        armed until a frame takes over 40 ms, then the 20 s before it
/komet debug stop            ends a capture early and writes it
/komet profile <modid> [s]   times every entry point of one mod (default 10 s)
/komet profile stop
```

`/komet profile` patches the methods the game calls in that mod (overrides of the game's classes, implementations of its
interfaces, lambdas used as event and tick listeners, and the mod's own Harmony patches), measures calls, total and self time,
time per frame, the longest call and the share on the main thread, then removes the patches again. The report goes to the
clipboard and `Logs/komet-debug/profile-<modid>-<time>.txt`; a debug capture running at the same time includes it.

Mod authors can add their own timings to the protocol through `KometDebug` (same rules as `KometOptions`):

```csharp
using (KometDebug.Measure("mymod:pathfinding")) FindPath(); // costs a field read while no capture runs
```

`Measure` works on any thread; the protocol lists each name with its total time, calls and time per call. When the world closes,
Komet drops them.

## Launch settings (optional)

.NET reads some settings only at startup from environment variables. `run.sh` is overwritten by every update, so use a script of
your own:

```bash
#!/bin/bash
export DOTNET_TC_CallCountingDelayMs=0
export DOTNET_gcServer=1 DOTNET_GCHeapCount=4 DOTNET_GCDynamicAdaptationMode=0 \
       DOTNET_GCGen0MaxBudget=0x400000 DOTNET_GCNoAffinitize=1
exec /opt/vintagestory/run.sh "$@"
```

- **`DOTNET_TC_CallCountingDelayMs=0`**: hot code is optimized to tier 1 right away, also while loading. Do not use:
  `DOTNET_TieredCompilation=0`, `DOTNET_TC_QuickJitForLoops=0`, `DOTNET_TieredPGO=0`.
- **Server GC instead of workstation GC**: 1 % low 45 → 58 FPS, frames over 25 ms 13 → 1 per minute, GC pause in flight
  2.5 → 0.65 s; 4 instead of 8 MiB gen0 also halves the longest pauses. Mind the spelling of `DOTNET_GCGen0MaxBudget` (Linux
  silently ignores a misspelled one); never without a cap or with DATAS.
- **"Optimize RAM"**: do not set it to "Aggressive" (2); that requests blocking GCs including LOH compaction.

## Benchmark

`scripts/bench.sh` starts the game unattended on a copy of the save from `scripts/bench.json`, flies a fixed route (standing,
turning, flying out and back) and writes `result.json` and `frames.csv` to `~/.cache/komet-bench/runs/`. The benchmark is a mod of
its own, `Komet.Bench` (mod id `kometbench`, never in the release): `--build` lays it out in `Releases/kometbench` beside
`Releases/komet`, and the run loads both.

```bash
scripts/bench.sh --build --profile smoke         # check the measuring chain, about 4 minutes
scripts/bench.sh --profile full                  # Komet against the engine, about 25 minutes
```

Profiles: `smoke`, `aa` (spread), `full`, `tess`, `gcreg`, `hud`. Arms run alternately in the same process (A B B A) and set
switches by their names from `Features.cs` (other mods' as `modid:name`); `--mod DIR` measures another build, `--env N=V` sets
environment variables. The game must be closed and the desktop unlocked; other load on the machine distorts frame times; never
stop the game time.

## Building

.NET SDK 10 and Vintage Story in `/opt/vintagestory` (otherwise `VsInstall=…`).

```bash
./build.sh                    # release package: Releases/komet/ and Releases/komet_<version>.zip
dotnet test tests/Komet.Test  # local only, needs the game installation
```

**Layout:** `Komet/` the mod, `Komet.Bench/` the developer benchmark (a separate mod), `tests/Komet.Test` the tests, `tests/Komet.Testing` the test rigs, `tools/Komet.Rules` the analyzer,
`scripts/` the benchmark.

**Test rigs:** `Komet.Testing` bundles the test rigs without a dependency on Komet (Harmony ids, loggers, foreign patches, chunk
and light worlds, animation shapes), also for other mods' tests. `GameInstall` finds the game through `VINTAGE_STORY`, otherwise
the build's `VsInstall`, otherwise `/opt/vintagestory`; without assets `RequireAssets()` skips the test. `ResolveAssemblies()`
loads the game's dependencies (protobuf-net, cairo, …) from the installation, as the game does.

Every warning is an error (NetAnalyzers, Roslynator, Sonar). The analyzer `Komet.Rules` additionally enforces: no `while`/`do`, no
`goto`, no preprocessor directives, no recursion, bounded `for`/`foreach`, at most 60 lines per function, an assertion density
≥ 2.0 and complete, matching language files (KR0001–KR0013).

CI (`.github/workflows/build.yml`) checks the game DLLs against `SHA256SUMS` and runs `./build.sh`, without tests. `main` creates a
release draft `v<version>`, other branches a prerelease `preview-<sha>` (the newest three are kept).
