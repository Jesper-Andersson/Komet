# Changelog

## 3.0.0-pre1

### New

- **Shader ports begin as Vulkan only starts**: programs linked before (sky, particles, entities) were ported at their
  first draw on the engine's thread (30 ports, the longest 27 ms); they begin off the thread now.
- **Server as its own process, measured**: flying into new terrain and landing there, allocation halves (80 → 33 MB/s)
  and GC pauses fall from 7.6 % to 3.1 % of the time; the option's hint says so.
- **Less garbage as terrain streams in** (exact): `PatchLookup` drops a mod-system lookup every vegetation patch made and
  threw away (2 % of all allocations while flying into new terrain); Ascii85 decoding of structures' block entities writes
  its bytes once instead of through a growing stream (half of 36 MB in 40 s).
- **Bench**: `landSeconds` (down to the ground after the laps and that long standing there), `route.back: false` (laps fly
  out only, so the landing comes down where the terrain just arrived), `spawning: true` (creatures appear as for a player).
- **Block selection ray without garbage** (`SelectionScratch`, exact): the ray that finds the block looked at made a new
  list per block stepped over (4 % of all allocations); it gets one list per thread, cleared.
- **Frame profiler without garbage** (`ProfilerPool`, exact): the game's frame profiler, on for Komet's HUD rows, made a
  dictionary and an entry per mark anew every frame, 12 % of all allocations in a steady-state trace. Komet keeps two sets
  and zeroes them as the frame begins.
- **Reuse the near shadow map between frames** (`NearShadowReuse`, Rendering page; presets Balanced and Speed on, Quality
  off): the near map is kept for up to 17 ms while the sun, the terrain in it and the player's position stay and the view
  turns less than about two degrees; it is drawn two blocks wider for that (a twentieth less resolution). Below 60 fps every
  frame draws it. Bench at ground level in a forest: near shadow GPU time 2.14 → 0.56 ms a frame, the GPU's frame 5.7 → 3.7 ms.
- **Pipeline recipes shipped**: the mod carries the pipeline recipes of a 1.22.7 session (`assets/komet/config/pipelines.json`);
  a first start with Vulkan warms them off the engine's thread instead of building each pipeline as a draw first needs it
  (bench, cold cache: 71 pipelines built on the engine's thread, the longest 148 ms).
- **HUD**: the engine profiler's marks per frame are counted (each costs a dictionary lookup and a clock read).
- **Block interaction help composed only on change** (`HelpCompose`, exact): the game redraws the interaction help's
  Cairo lines at every block looked at, every third of a second at a creature and at every block change nearby (5-15 ms of
  the main thread each); Komet skips the redraw while the actions, keys and items shown stay the same. The HUD counts the
  redraws skipped.
- **Bench**: `joinSeconds` (a wait in the world before the setup, with a screenshot), `captureSeconds` (a Komet debug
  capture as the world is up) and `gui: "help"` (the camera sweeps the ground blocks during the join wait).
- **Frame bridge only for hitches**: it steps in only when a frame takes 1.5 times the engine's usual frame (median of the last 16), no longer on every frame of a steady rate below the monitor's, which mixed bridged and real frames all the time (jitter at 107 fps on 144 Hz). Bench, VSync on: bridged frames 996 → 280, steps back 74 → 19.
- **Frame bridge without jitter**: a bridged frame keeps the lag real frames have (their camera is read as the frame
  begins), never turns past the camera the next real frame will show, and shows 70 % of the predicted turn. Frames shown
  stepping back against a turn (bench, 100 fps on 144 Hz): 1417 → 30. The log names the count when the bridge closes.
- **Fix: crash in deep culling** when the game's network thread added map chunks while Komet copied their keys.
- **Settings file renamed** to `ModConfig/komet.json`; a `komet-hud.json` from before is renamed on the first start.
- **Settings in three tiers**: the Overview page holds the preset (Automatic, Quality, Balanced, Speed; "Adjusted" once an
  option it sets was changed), this computer's facts with a reset, the renderer and three preferences. The Rendering, Chunks,
  Misc and Vulkan pages list the trade-offs singly (budgets, foliage, SSAO, shadows, culling, memory, threads). "Show advanced
  options" adds to each page its exact optimisations, each as its own switch under a master switch: every value of komet.json
  is in the menu. The sidebar scrolls (compact rows, the chosen page kept in view, a thumb at its edge), long labels, group
  titles and hints are cut with an ellipsis instead of running out of their box.
- **Settings for this computer**: Komet reads the cores, RAM, video memory and the fastest monitor's refresh rate at each
  start and sets every option the player has not changed to what suits them (chunk packing by RAM, pool trimming and distant
  foliage by video memory, the Vulkan far cache and frame bridge by video memory and monitor). Options changed in the menu keep
  the player's value (saved as `Chosen`); setting one back to its default lets it follow the computer again. The Tools page shows
  what was found and gives every option back to the detected value. Settings files from before keep every value that differs
  from Komet's defaults.
- **Frame bridge follows the monitor**: the refresh rate is looked up twice a second, so moving the window to another monitor
  or changing its mode retunes the bridge at once.

- **Far terrain cache with Vulkan only** (`VulkanFarCache`, experimental, needs GPU terrain culling and a view distance from 448
  blocks): the terrain past 384 blocks is drawn into a layer of its own every 12th frame (or after a step of a block or a new
  field of view) and laid into the frames in between by one full-screen pass, turned to the camera and corrected for the eye's
  step; far terrain coming into view is drawn at once. Opaque terrain GPU time about halved at view distance 1536
  (2.8 → 1.5 ms flying, 5.1 → 2.2 ms standing, bench at 1440p).
- **Bridge late frames with Vulkan only** (`VulkanFrameBridge`, experimental): when the game misses a refresh of the monitor,
  the last frame is shown again, turned to where the camera is going, from a present thread of its own.
- **Uniforms kept from resting OpenGL** (`VulkanQuietUniforms`, advanced, on): with Vulkan only, the shader values the game sets
  every frame are no longer sent to the idle OpenGL as well; OpenGL gets them all from Komet's mirror should it draw again.
  About 0.14 ms of the main thread a frame.
- **Bench**: frames.csv carries the GPU time and the opaque terrain's GPU time of each frame; a screenshot mid-turn per lap.

- **Slimmer settings**: only the options worth weighing are shown (budgets, threads, visuals, experimental features, Vulkan mode);
  every engine-exact optimisation stays on and appears with "Show advanced options" (Tools page).

- **Debug protocol** (`/komet debug`): records every collector for a while, a fixed time, or until a frame crosses a threshold,
  and writes one plain-text protocol (English or German) with findings, frame statistics, spikes, GC, threads, rendering, mods,
  Harmony patch conflicts with their risk and the log, compared with the previous protocol. Copied to the clipboard and written to
  `Logs/komet-debug/` with every frame as CSV and the full patch registry.
- **Protocol comparison knows when scenes differ**: when the previous protocol was taken in another mode, a short capture
  (under 10 s), at another view distance, window size or renderer, or with 30 % more or fewer triangles drawn, the comparison
  says so ("not comparable: ...") with a finding and a line in the debug window, instead of showing a Komet regression.
- **Simplify distant foliage** (Rendering page, default 1 px): past the distance where a block covers less than a pixel, every other leaf
  block is left out in a checkerboard (the two-block-wide leaf quads of its neighbours cover it) and grass, flowers, ferns and crops
  are left out; chunks crossing
  the line are retessellated as the player moves. 2 px starts it at half the distance, 0 turns it off.
- **Less video memory with Vulkan only (lean)**: a new chunk pool's buffers are kept out of OpenGL from 1 MiB on (its 8 MB
  colour buffer used to stay in OpenGL as well, about 0.9 GB at view distance 1536), and Vulkan gives empty 64 MB blocks of its
  buffer heap back to the driver instead of keeping the high-water mark. The debug protocol shows the game's video memory per
  driver client (OpenGL, Vulkan) beside Komet's own Vulkan ledger and what the rest of the device holds (Linux).
  Textures OpenGL got before lean began (the screen targets and shadow maps made as the game starts, the block atlases) are now
  given lean's stand-in too once Vulkan holds them: about 0.7 GB less at 1440p with shadow quality 4.
- **Compact terrain data with Vulkan only** (`VulkanCompactFaces`, advanced, on): the terrain's face data is kept in 52 bytes a
  quad instead of 64 (edges as half floats; corner, textures, flags, colours unchanged), about 15 % less video memory for the
  terrain. Packed with SIMD in the copy every chunk upload makes anyway (about 7 ns a quad), unpacked by the
  chunk shaders and when OpenGL is given the pools back. Exact for blocks on the 1/16 grid. Switching it rebuilds the terrain at once.
- **Rebuild the terrain in the world** (Tools page, and on switching compact terrain data or lean): every chunk pool is freed in
  OpenGL and Vulkan, a fresh chunk renderer is made as the engine does when block textures load, and all loaded chunks are
  tessellated again into new pools, which take the current switches. No restart or rejoin needed.
- **Empty chunk pools are freed** (`PoolTrim`, level 2): the game never gives a chunk pool back, so flying across the map left
  gigabytes of empty pools in video memory (measured 5.4 GB held for 0.5 GB of terrain). A pool without a chunk for 2 s now goes
  (OpenGL's buffers and Vulkan's copy), and the sparsest old pool of a pass is drained: it takes no new chunks and its chunks
  are tessellated again into the others until it is empty. Level 1 only frees empty pools (after 10 s), 3 drains up to 50 % use,
  4 up to 75 %, 5 up to 90 %. Saved as `PoolTrimLevel` (the old switch value 1 no longer pins the mildest level).
- **Chunks packed in RAM sooner** (`ChunkPacking`, Chunks page, level 1): the game packs a chunk's block and light data only after
  8 s untouched and at most 50 chunks a second, so flying left thousands unpacked. Level 1 packs 200 a second, 2 400 after half
  the time, 3 800 after a quarter and keeps at most 500 spare chunk datas. The in-process server's chunks too (singleplayer),
  whose scan also stops sparing a cross of three chunk columns through the whole loaded area (the engine's `||` where `&&` is
  meant). The protocol shows how many chunks lie packed.
- **Less RAM held by meshes and the heap** (from a heap dump of a VD 1536 world): `RecyclerTrim` (Chunks page, level 1) keeps the
  engine's mesh recycler to half its caps (a quarter at 2) and drops entries unused for 8 s (4 s) instead of one at a time (it sat at
  210 MB), and at 2 chiselled blocks farther than 6 chunks drop their mesh once it is in the chunk (174 MB in the dump); with it
  on, the tesselators' scratch meshes, grown to the largest chunk and never shrunk (324 MB over nine), are made
  anew at their start size once nothing waited for tessellation for 10 s. `GcCompact` (on) compacts the heap once when the game is
  paused or out of focus for 10 s with over 1 GB fragmented, giving that memory back (the low-latency collector never compacts:
  2.9 GB lay free inside the heap after packing). Pool draining stops once a pass wastes no more space (it unpacked chunk data).
- **Deep underground chunks without geometry** (`DeepCulling`, Rendering page, on): a heap dump at view distance 1536 had 31 % of
  all quads below block 96, cave walls nobody sees from the surface. Chunks whose top lies 16 blocks below the surface of all their
  neighbouring columns (the lower of rain and terrain height, so sea floors and pits count) and farther than 96 blocks (256 when you
  are under the surface) are tessellated without geometry and come back as you approach; chunks loaded before the surfaces were
  known are swept and tessellated again. Pool draining only drains a pool whose chunks fit into the room the others have left.
  While chunks wait for tessellation or upload, the retessellation that only saves memory (culling deep chunks, the leaner far
  foliage band, draining) waits, so a world loads and flights stream as fast as before.
- **Mod profiler** (`/komet profile <modid>`): times one mod's entry points on demand (overrides, interface implementations,
  listeners and its Harmony patches) with total and self time per method, then removes its patches. Per method also the bytes it
  allocated itself (memory churn), its draw calls and its peak time inside one frame; for the mod its main-thread time per frame as
  a graph with p99, worst frame and frames over 2 ms, its share of everything allocated, the collections meanwhile, and the static
  collections its types hold. Compiler names read as the method they are in (`SmokeSystem.Register (lambda)`).
- **`KometDebug` API**: other mods add named timings (`Measure`) to the debug protocol.
- **New HUD**: the floating panels are gone. F7 shows a compact overlay (FPS, lows, frame time, graph, the mods' time with up to
  three pinned mods, a hint for each spike with its cause); Ctrl+F7 opens a window with tabs for everything the panels showed
  (overview with one-click hints, frames, system, render, threads, mods, log, settings); Ctrl+F8 opens the debug window, which
  starts captures and shows the protocol with a scrollbar. In the Mods tab a click pins a mod to the overlay, a right-click times
  its methods for 10 s.
- **Edition and version in the HUD**: overlay and window name the build as Release, Pre-Build or Development with its version.
  The build decides it from `modinfo.json` (a version with a suffix such as `3.0.0-pre1` is a Pre-Build, a Debug build is
  Development) and fails on a version that is not `major.minor.patch[-suffix]`. A click on the badge opens the Version page: commit,
  build time, the update check against GitHub and the installed file's SHA-256 beside the published one, digit by digit. It
  replaces the old checksum window. The overview warns when a newer build is out or the checksum differs.

- **Experimental: Vulkan only** (Settings → Vulkan, "Renderer": "Vulkan (experimental)", or the `VulkanOnly` switch; off by
  default): Vulkan draws everything in the world by itself and OpenGL rests. OpenGL
  keeps only the menu, the loading screens and the game's object names; Komet follows from the mod's start what the game tells
  OpenGL (shaders, framebuffers, textures, buffers), keeps every texture, render target and buffer in Vulkan memory of its own,
  takes every stage's draws, clears, blits and copies, answers screenshots from Vulkan's images and shows the frames through its
  own swapchain. It needs no OpenGL interop: no `GL_EXT_memory_object` or `GL_EXT_semaphore`, nothing shared (without them
  the GPU is picked by OpenGL's renderer name). Uploads still reach OpenGL as well, so on any
  error OpenGL takes over at once; what only Vulkan drew (map tiles, item icons, mods' framebuffers, the distant shadow map) is
  written back into OpenGL's textures first. A draw whose shader port or pipeline is still being built waits for it instead of
  going missing; shaders are ported as the game links them while the world loads, and the pipelines a shader drew with are
  remembered and built ahead at the next start. Mods' raw OpenGL is drawn too where Vulkan can express it: the stencil test
  with depth-and-stencil textures, renderbuffers, glClearTexImage, clears through colour or stencil masks, points, line
  loops, 8-bit indices, instance divisors, indirect draws, layered framebuffers and depths at a mipmap level or layer.
  Whatever Vulkan cannot draw (mods' compute dispatches and geometry shaders among it) is missing from the picture rather
  than drawn by OpenGL: the log warns once for each kind and the Vulkan report lists what is missing and how often. With the
  extra `VulkanOnlyLean` switch ("Vulkan only: lean", off by default) OpenGL gets no copy of the world's large textures and
  buffers, which saves the video memory of that second copy (atlases, shadow maps, screen targets and non-persistent chunk
  pools, usually several hundred MB; the Vulkan report says how much). Switched off, or failing while the GPU still answers,
  Vulkan-only first gives OpenGL those textures and buffers back from its own (a short pause) and OpenGL takes over; only
  after a GPU fault does OpenGL show just the menus and dialogs over a black frame, so the world can be left and joined again.

- **Experimental: singleplayer server in its own process** (Settings → Other → Server, off; only for those who want it): a singleplayer world's server runs as
  a child process of the game's own server program on 127.0.0.1 and the game joins it, so the server's allocations no longer
  trigger the game's garbage collections. Nothing to set up: Komet finds a free port, sets a one-off password, gives the child
  server GC, keeps `serverconfig.json` as it was, pauses the child with the escape menu and stops it (saving) when the player
  leaves or the game ends. The first world after the game start is handed over before any chunk loads; a new world, less than
  16 GB of RAM or a child that does not come up start the usual way. Komet's server patches (LightRepair, ChunkThreadClosure,
  ColumnNoiseScratch, WorldGenScratch) run in the child. Flying (Vulkan, view distance 1536): collections 9.8 → 5.2/s, GC share
  6.7 → 3.8 %, frames over 25 ms 134 → 79–117/min, longest pause 37 → 20 ms, 1 % low 29.4 → 31–32 fps, the game's heap
  5.9 → 4.1 GB; loading the first world takes about 15 s longer and the child holds about 3 GB of RAM; the average frame time is unchanged. `modinfo.json` now says `Universal` (required on neither side)
  so the integrated server loads Komet for the handover.

### Faster

- **GPU terrain culling on by default, in slabs** (OcclusionCulling): each direction's faces of a chunk part are cut
  into four slabs by their plane, and every slab is tested on its own, for facing the camera and against the depth pyramid (the
  part's box cut to the slab's planes); a slab the old pyramid hid is tested again against this frame's, as whole parts are, so
  the picture stays the same. View distance 1024, Vulkan, uncapped, old and new build in turns: opaque terrain 5.5-5.8 →
  5.0-5.2 ms GPU, culling 0.12 → 0.30 ms, frames 0.25-0.3 ms faster.
- **Fewer terrain shader outputs** (LeanVaryings): the terrain's vertex shaders handed about 56 values a vertex to the
  fragment shaders, and that export was most of what a vertex cost. What follows from the block's flags, the uniforms or the
  world position - normal, glow, the normal brightness, the SSAO normal, the fog colour, the camera position, the shadow maps'
  coordinates (their weight still from the vertices) - is worked out per pixel now, the same numbers up to the last bit of
  rounding. View distance 1024, old and new build in turns: opaque terrain 5.1-5.2 → 3.9-4.0 ms GPU, frames 0.9 ms faster
  standing and 0.75 ms turning (about 15 % more frames a second). Screenshots differ no more than two runs of one build.
  The world position goes as three values (its w is always 1), and the climate colour map's coordinates and the temperature as
  the face's one colour map int: another 0.2 ms of opaque terrain.
- **Sharp upscaling** (SharpUpscale): below 100 % resolution the scene is upscaled to the window as AMD FSR 1 does (EASU,
  edge-aware, plus a contrast-limited sharpening) instead of stretched bilinearly; at 100 % nothing changes. The resolution
  setting goes in steps of 5 %. At 85 %: frames about 0.3 ms faster than at 100 % (view distance 1024, 1080p), as fast as the
  bilinear stretch but closer to the full-resolution picture.
- **Leaner SSAO** (SsaoLean, a setting, off): at the high SSAO quality 16 sample points instead of 24 and one blur pass instead
  of three; post-processing 0.57 → 0.48 ms at 1080p.
- **Leaner god rays** (LeanGodRays): each of the god rays' up to 172 steps a pixel read the scene's colour even behind the
  terrain, where it adds exactly nothing; it is read only where the glow lets it through. The same picture; the god-ray pass
  0.29 → 0.21 ms (Vulkan, 1080p), frames 0.03-0.05 ms faster while turning.
- **Far shadow map reused** (FarShadowReuse, a setting, on): while the camera holds still the far shadow map is kept for up to
  33 ms and only shifted with the camera; redrawn at once when terrain in its range changes, the light turns or the camera
  moves or turns. Standing still: OpenGL 0.7-0.9 ms, Vulkan 0.3-0.6 ms a frame faster. Creature shadows beyond the near map
  update at about 30 Hz then.
- **Less garbage a frame from the Vulkan renderer**: buffer clears go to the recording thread without a closure each, and the
  terrain's draw context is reused.
- **Shadow maps stay in Komet's depth** (Vulkan): every engine depth has a compressed depth image of Komet's own, and what
  samples a shadow map - terrain, entities, the culling's depth pyramid - samples Komet's image while it holds the newer depth.
  Both 4096² maps went back into the engine's every frame before (two 64 MB copies); now only an OpenGL call that reads or
  writes one, a segment closing before the frame's end or a draw into the engine's depth sends it back. Depth copies a frame
  4.0 → 2.1; view distance 1024, old and new build in turns: frames 0.3 ms faster standing, 0.4 ms turning. Screenshots match
  (fewer differing pixels than two runs of one build). The screen's depth goes back as the opaque terrain ends, as before.
- **Entity shadows culled by the view** (EntityCasters): the shadow maps leave out creatures, players and items whose shadow
  cannot reach the view, by the rule the terrain's casters already follow; about nine in ten entity shadows in the bench world.
- **Fewer stalls from the GPU culling**: a pool whose list changed out of sight is brought up to date row by row instead of
  written and uploaded whole (up to 6 MB), and a sorted mesh's direction data is moved in place at upload instead of copied.

- **Occlusion culling rays** (CullerRays): the engine's culling thread walks its rays without vector objects, about 11 % of all CPU
  work while a world loads.
- **World map** (MapReads): a map piece locks each chunk once instead of once per block read.
- **Less lock contention while loading**: weather regions, climate maps and the mesh recycler are read without waiting.
- **Less garbage**: decor meshes are built per tessellation in a reused buffer (ClutterMeshes), world-gen structure data is
  encoded without temporary buffers (Ascii85Text).

- **Own tessellator** (OwnTessellation, on): cubes, soil, block layers, tall grass and simple models, leaves included, are drawn
  by Komet's own code that writes straight into the mesh arrays; the engine keeps every other block. Byte-identical with the
  engine (golden test). In the bench world it draws about 97 % of those blocks.
- **Smaller tessellation garbage** (PartRecycling): small chunk parts, decor lookups and upload lists reuse their buffers.
- **Block entities on chunk load** (BlockEntityBudget, BlockEntityCaches, ListenerSlots): far chunks initialise their block
  entities within the chunk load budget over a few frames; ground storage, firepits and pots on the fire work out their settings,
  meshes and shapes once; tick listener and renderer registration no longer search their lists.
- **Idle animated block entities skipped** in every render stage, not only the opaque one (IdleAnimators): about 12 000 calls a
  frame in the bench world.
- **Less garbage while flying**: the singleplayer server's chunk thread reuses its request list instead of making one every tick
  (about 11 MB/s, WorldGenScratch); particles keep their block walk callback and the room registry its set of chunks to drop
  for changed blocks (HandlerLists).
- **Creative inventory scrolls smoothly**: a row of creature items (deer, moose, traders) parsed shape files of up to 1.7 MB
  with every animation the first time it scrolled into view, up to 2 s and 800 MB of garbage in one frame. Creature items and
  clothing now build their inventory mesh from the shape without its animations, read from a pooled buffer (InventoryShapes),
  and the creative inventory draws items it never drew before within 6 ms a frame, the rest a few frames later (IconBudget).
  Scrolling through the whole "Everything" tab: worst frame 2139 → 60 ms, 1 % low 5.5 → 22.7 fps.
- **The GUI's item icons in Vulkan without a uniform block a draw**: a program whose blocks still pack anew in a quarter of its
  draws after Komet settled its hot uniforms is watched again (the GUI's first draws are the hotbar's, its item icons change
  other uniforms), and the push constants are budgeted as std430 lays them out.
- **Less garbage from world generation** (WorldGenScratch, singleplayer): sunlight spreading reuses its block positions, rock
  strata reuse their weight array and patch placement caches block code parts. Allocation while flying 89 → 82 MB/s, 9 % fewer
  gen0 collections.
- **Tessellation 0.25 ms faster per pass**: FaceSorting only sorts faces while the GPU culls rows of them, its only reader.
- **Less garbage from the map while exploring**: the pool of map piece arrays keeps up to 4096 instead of 512; pieces arrive in
  bursts of hundreds (900 a second flying at 20), and one in eight went to the collector.
- **Far shadows 1 ms cheaper** (FrustumSweep): the shadow passes leave out casters whose shadow cannot reach the camera's view
  (outside one of its sides, moved out by 8 blocks, with the light not crossing that side inward), the rule terrain culling on the
  GPU already used. Shadows are unchanged (screenshots compared pixel by pixel). Flight benchmark: 1.15 ms faster a frame (t −13.7),
  far shadow pass 1.4 → 0.4 ms GPU.
- **Slow frames leave their uploads to the next** (UploadYield): a frame that reaches its chunk uploads more than 6 ms later than
  usual (a collection, a burst of block entities) uploads only one chunk, never two frames in a row; edits and chunks next to the
  player still go at once. 1 % low +0.5 to +0.9 fps, pop-in unchanged.
- **No main thread waiting behind world generation**: chunk uploads' background packs ran on .NET's shared pool behind the
  integrated server's world generation while the main thread and Vulkan's recording thread spun for them; whoever waits now packs
  what nobody has started.
- **Vulkan: less work every frame**: pipeline statistics are queried only while the HUD shows them (every fourth frame around the
  opaque terrain otherwise), an unchanged glDrawBuffers no longer rebuilds the target's outputs, the frame's shared images are
  listed anew only when a texture copy is made or let go, and the scene's hooks are no longer wired anew every frame.
  Flight benchmark against before: 1 % low 35.6 → 38.8 fps, 0.1 % low 27.6 → 33.1 fps, GC 5.4 → 4.7 % of the time.
- **Less garbage from Vulkan**: dispatches and buffer copies reuse their recorded state instead of a closure and two descriptor
  arrays each, and every switch between compute and drawing its barrier and rendering state. Main thread 10.1 → 9.4 MB/s, with
  terrain culling fully on the GPU 15.1 → 10.8 MB/s.
- **No more multi-second freezes from full collections** (GcLatency): the garbage collector keeps full collections in the
  background instead of stopping the game to compact the whole heap (2.7 s at view distance 1536). Under real memory shortage
  the runtime still stops.
- **Chunk uploads off the main thread's critical path**: an unbind after every chunk part held the main thread in the driver for
  9 % of the frame while flying, and the colour and custom attribute copies went across the bus into VRAM; they now go to host
  staging with one GPU copy each, the colours copied by the background pack. Main thread share of uploads 10 % -> 2 %.
- **Less garbage per frame in the Vulkan renderer**: texture sets of up to eight samplers are cached (the chunk shaders bind
  more than four, so every pool draw built a dictionary and a descriptor array), and no closure per draw or tessellation pass.
- **HUD window and debug window without frame spikes**: they drew their whole page with Cairo on the main thread every 250-400 ms
  (5-20 ms frames in debug protocols). The page is now recorded on the main thread and drawn on a worker; the main thread only
  uploads it. View distance 512, both windows open in turns, old and new build: 1 % low 176 → 219 fps, 0.1 % low 107 → 141 fps.

### Fixed

- **No hitch when an empty chunk pool goes** (Vulkan): the pool's buffers are freed once the frames still drawing from it are done,
  instead of waiting for the whole GPU on the main thread (PoolTrim deletes empty pools every few seconds).
- **Microstutter with an fps cap** (`FramePacer`, on): the game's cap sleeps whole milliseconds, so capped frames alternated
  between ~6 and ~7 ms at 144 fps and overshot the cap. Komet waits the exact interval: frame-to-frame change 0.36 → 0.03 ms.

- **Vulkan: uneven frames when the GPU is the limit** (`VulkanPacing`, on): the wait for the GPU moved from the middle of the frame
  to its very start, before the game reads its clock, so frames start as evenly as the GPU finishes them: frame-to-frame change
  0.70 → 0.19 ms (as OpenGL), frames over 1.5x the median 2.1 → 0.9 %.

- **Settings pages repeated group headings** (culling, drawing, culling, …); each group now appears once.

- **Trees without leaves with terrain culling fully on the GPU** (Vulkan): uploads that did not fit the frame's 32 MB staging were
  dropped, so whole pools drew stale rows. Every upload of Vulkan's compute work now takes a buffer of its own past the staging,
  kept until the GPU has done the frame.
- **Features stepping aside for Komet itself**: a Komet patch on another feature's engine method counted as another mod's
  (OccludedChunks never ran); Komet's own patches no longer count.
- **A crash while joining** ("Collection was modified" in WeatherSystemClient.OnAssetsPacket): the weather's region walk now
  holds the lock its writers take (WeatherLock). More tessellation threads made the engine's race likelier.
- **Crash on macOS a few seconds into a world** (#4): SunOcclusion read its query through GL 4.5 buffers without asking for them;
  below 4.5 (macOS stops at 4.1) it reads the query directly, as 2.0 did. A hang report that dotnet-stack could not fill now says
  why instead of staying empty.
- **Update check across a year boundary** named an older release as the newest (dates compared as text).
- **`komet-vulkan-watch.txt` grew without end** (about 0.7 MB per hour with Vulkan); it is rotated at 4 MB now.
- **HUD window off-screen** after a lower resolution or another monitor; its saved place is kept inside the game window. A
  `komet-hud.json` that does not parse is kept as `komet-hud.json.broken` before the defaults replace it.
- **Garbled Log tab** when it was opened while the HUD sampled the log; **clutter bookshelves** cloned their mesh on every
  re-tessellation; fewer GL stalls when Vulkan takes over a framebuffer.
- **A 40-130 ms hitch some seconds into Vulkan** (and again a minute later): the black-screen probe read hundreds of texels
  back, each waiting for the GPU, at Vulkan frames 300 and 3000; it runs once right after the start and when a debug capture
  begins now.
- **The game froze when Escape closed the debug window or the HUD window** (also during a debug capture): the game's Escape loop
  asks a dialog again while it says it closed, and Komet's windows fade out first. Escape in the settings no longer closes the
  whole screen when it should only close a list, the search or a slider's focus.
- **Empty creative inventory** with the HUD off: IconBudget's per-frame budget only renewed while the HUD measured frames, so after
  the first few icons no slot drew again. HangWatch reported a hang after every world join for the same reason.
- **JitWarm warmed nothing from the second world on** in one game run: Harmony keeps an empty record of a method unpatched when a
  world is left, which counted as another mod's patch (TessBlockPos likewise).
- **Plants and leaves no longer serialize** when another mod patches `Block.OnJsonTesselation`: only overrides that call it
  (BlockFernTree's) take the block's lock. **The left world is freed in the main menu** (ClimateCache, WeatherLock, CookingMatch,
  ShadowView held it).
- **Clutter with a LOD2 shape drew its LOD2 mesh twice** (ClutterMeshes): both meshes of one tessellation shared the thread's scratch.
- **Vulkan could end the game**: an exception inside a GL tap or on the recording thread now stops Vulkan (OpenGL takes over)
  instead of the process; a full pipeline cache no longer rebuilds the same pipeline every frame; failed Vulkan/GL allocations no
  longer leak device memory, images, pipelines or GL objects.
- **Tessellation of blocks another mod patches** (`OnJsonTesselation`) runs under the block's lock again; a chunk replaced while
  ChunkLookup was off is no longer handed out stale; a frame batch left open when a world closed no longer breaks the next world.
- **Face sorting kept meshes alive** when FacePacking did not upload them itself; the HUD kept listening to settings after
  closing; bench configs with a misspelt `hudWindow`/`gui` now fail at load.
- **Debug protocol**: an English protocol showed the renderer and the GC configuration in the HUD's language and the Vulkan report
  with decimal commas; empty "spikes by cause" and "methods several owners patch" headings are left out.

### Changed

- **Benchmark out of the release**: the developer benchmark is its own mod, `Komet.Bench` (`kometbench`), which
  `scripts/bench.sh --build` lays out beside the build under test; the player's `Komet.dll` no longer carries it.
- **"Vulkan prepares shaders" card**: while Vulkan builds shader ports and pipelines (first start after an update, a world join),
  a small card at the top of the screen shows the progress, HUD shown or not, and fades out when they are ready.
- **Vulkan settings trimmed**: the page shows the active renderer and one choice, OpenGL or Vulkan (Vulkan only,
  experimental). The remaining Vulkan switches (lean Vulkan-only, pacing, GPU times, shader cache and the like) are listed under
  advanced and stay settable in komet.json and for the benchmark.
- **Sliders in the settings**: the track is always shown and thicker, grows under the mouse, the knob glides to a new value and
  floats it while dragged, a changed value's bar grows in. Keyboard: a click or Tab focuses a slider, ← → move it one step
  (Shift: ten), Home / End to its ends; Komet's own sliders mark their default on the track and take it back with Delete, a right
  click or the ↺ beside the value (suggested by a player).
- **Terrain culling fully on the GPU is on by default** and takes effect with occlusion culling (still experimental and off): with
  Vulkan 1.5 ms faster a frame on average in the flight benchmark, its 1 % lows from equal to 1.3 fps lower.
- **Benchmark**: `"gui": "creative"` scrolls the creative inventory during still segments; `--build` no longer leaves build
  servers holding the GPU lock, and a crashed run's crash reporter is closed.

### Removed

- The old panels' drawing code, the "Paint the HUD off the main thread" switch and 111 unused texts. The report rows stay as data
  for the bench and the debug protocol; `/komet dump` is gone, the options' "copy the values" writes a debug protocol of right
  now instead (it was empty since the new HUD).
- Trimmed what measured useless or went unused: the occlusion measurement (the HUD's "would hide" rows; the pipeline rows stay,
  from Vulkan's statistics), GPU terrain culling under OpenGL (it cost up to 2.1 ms a frame or was at parity: occlusion culling
  now only runs while Vulkan draws the terrain, OpenGL keeps the engine's culling), InsideTextures (no measurable effect), the
  `VsModInterop` and `KometDebug.AddSection`/`RemoveSections` APIs (unused). The switches FrustumStages, ShadowCasters, FaceSplits
  and UploadCeiling are always on now (the priority chunks' ceiling stays at 6 ms).
- **The hybrid renderer** (Vulkan drawing into OpenGL's frame through shared memory and semaphores): the renderer is OpenGL or
  Vulkan only. Gone with it are the switches that only chose or compared hybrid variants (`VulkanCore`, `VulkanTerrain`, the
  pass switches `VulkanOpaque`/`Shadows`/`LiquidDepth`/`Transparent`/`WaterPlants`, the scene switches `VulkanScene`/`Sky`/
  `Entities`/`Particles`/`Post`/`Other`, `VulkanWindow`, `VulkanPresent`, `VulkanLazyHandoff`, `VulkanOwnDepth`);
  `VulkanPoolBirths` is always on. A komet.json with the hybrid chosen starts with OpenGL, one with Vulkan only keeps it.

## 2.0.0 — 2026-09-27

Komet 2.0.0 is a complete rewrite for Vintage Story 1.22 (verified against 1.22.7). Every feature produces the same result as the
engine, checks the engine code it replaces by fingerprint and steps aside when the game changed or another mod patches the same
method. The focus is on stable frame times: the 1 % and 0.1 % lows.

### New

- **Options menu**: a full-screen menu in the style of Sodium replaces the in-game settings (Escape → Settings, or `.komet`):
  search over all options, sidebar with the game's pages, Komet's pages and other mods' pages, changes applied at once with a
  single shader reload, a controls page with rebinding and conflict highlighting. The original menu stays one click away.
- **Distant shadows**: terrain casts shadows up to the view distance. The engine's shadows end after about 200 blocks; a third,
  texel-stable shadow map covers the rest and is redrawn only when the sun has moved or you travelled far, one sixteenth per frame,
  so it costs no measurable frame time.
- **Light repair** (singleplayer): chunks saved without light - drawn pitch black, often whole areas or rivers - are relit
  automatically when they load and by a background sweep, and saved repaired. The engine's `FullRelight` (WorldEdit,
  `/debug relight`, the timeswitch, `/wgen`), which caused them, now runs without its two faults: it no longer wipes the light of
  partly loaded columns and no longer loses torch light.
- **Worker pool**: one pool of worker threads for frustum culling and chunk tessellation, frame work first.
- **Rendering**: vectorized and parallel frustum culling per render stage (FrustumSweep, FrustumStages), larger mesh pools with a
  quarter of the draw calls (PoolScale), culling of animated block entities outside the view and shadow volumes
  (AnimatableCulling), idle block entity animations skipped (IdleAnimators), GL error checks only every 16th frame (GlErrorPoll).
- **Less garbage**: entity partitioning (PartitionReuse), cooking recipe checks (CookingMatch), climate and wind handler lists
  (HandlerLists), plant sink offsets in the tessellator (TessBlockPos).
- **HUD** (F7): frame time graph, 1 % / 0.1 % lows, spikes with their cause, render passes, mod times, mods & patches, Komet
  counters, main and debug log.
- **Update check** (opt-in): looks for a newer build of the installed channel on GitHub and compares the checksum of the installed
  zip with the published one.
- **Mod API**: `KometOptions` lets other mods add their pages to the options menu; `KometFeatures` lets them query Komet's features,
  hold one on the engine's behaviour, or register features of their own. `Komet.Testing` provides test rigs for other mods.
- **Benchmark**: `scripts/bench.sh` flies a fixed route unattended and compares builds or switches in one process.

### Changed

- Rebuilt from the ground up: code organized by area (rendering, chunks, tessellation, shapes, world, HUD, diagnostics,
  benchmark), a feature registry that drives install order, settings, HUD, options menu and benchmark, and a Roslyn analyzer that
  enforces the project's rules. The code was trimmed by about 900 lines without any change in behaviour.
- Server-side patches (light repair, world generation, chunk thread) only install when the server runs in the same process, i.e.
  in singleplayer; on a dedicated server Komet only works on the client.
- All features are on by default and can be switched off one by one; off means the engine does the work.

### Fixed

- Black chunks and black rivers in worlds that were relit with a partly loaded area (see Light repair).
- Torches and other light sources losing their light after a relight.
- Minutes-long 8–13 ms frames near beehives (ParticleLight).

### Compatibility

- Vintage Story 1.22.x, client side; features whose engine code differs from 1.22.7 stay off and say so in the log and the HUD.
- If another mod patches a method a feature replaces, that feature steps aside; with another mod's shader pack, distant shadows stay
  off.
- Remove older Komet zips from the `Mods` folder: two mods with the id `komet` and the same version load only one of them.
