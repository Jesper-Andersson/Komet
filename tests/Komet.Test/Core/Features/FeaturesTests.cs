using Komet.Api;
using Komet.Core;
using Komet.Hud;
using Komet.Options;
using Komet.Rendering;
using Komet.Vulkan;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Core;

// The built-in table pins what KometModSystem hardcoded before it: the install order (also the knobs' order in komet.json, the
// settings pages and bench.json) and the file's keys; the rest are invariants of the modules' lists.
[NonParallelizable]
public sealed class FeaturesTests
{
    private static readonly string[] InstallOrder =
    [
        "ModTimes", "RenderCost", "Hud", "GraphicsMenu", "MenuBlur", "ShaderUseCache", "DistantShadows",
        "LeanVaryings", "SharpUpscale", "SsaoLean", "LeanGodRays", "FrustumSweep", "FarShadowReuse", "NearShadowReuse", "EntityCasters", "IndirectDraw", "OcclusionCulling", "GlQueryCache", "VulkanCore", "VulkanShaderCache", "VulkanGpuTimes", "VulkanCompactFaces", "TerrainRebuild", "PoolTrim",
        "VulkanTerrain", "SunOcclusion", "AnimatableCulling", "PotCulling", "HelpCompose", "MapTileCulling", "MapTileUpload",
        "IdleAnimators", "WindowSizeCache", "PoolScale", "GlErrorPoll", "ChunkLookup", "MeshPool", "MeshRecycle",
        "FacePacking", "FaceSorting", "ClimateCache", "PartitionReuse", "HandlerLists", "CookingMatch", "AnimationFrames", "InitOnce",
        "ShapeInitMemo", "EntityTessBudget", "EntityTimes", "ChunkBudget", "ChunkLoadBudget", "RecyclerTrim", "ChunkPacking", "BlockEntityBudget", "EventBusSweep",
        "ListenerSlots", "BlockEntityCaches", "ChunkThreadClosure", "CloudTileScratch",
        "DecompressScratch", "ProtoListCache", "BushShapes", "ClutterMeshes", "CullerRays", "MapReads", "Ascii85Text", "TreeKeys", "MapPixels", "MapSaveScratch", "LightScratch", "LightRepair", "ParticleLight",
        "ColumnNoiseScratch", "TessSafety", "TessSeams", "ExtendedRows", "VisibleFaces", "FaceLight", "OwnTessellation",
        "TessBlockPos", "PartRecycling",
        "TessAccounting", "TessSchedule", "WorkerPool", "TessWorkers", "JitWarm", "OccludedChunks", "DeepCulling", "FarFoliage", "GcLatency", "GcCompact", "ServerProcess", "InventoryShapes", "WorldGenScratch", "PatchLookup", "IconBudget", "SelectionScratch", "ProfilerPool", "WeatherLock", "FramePacer", "FrameClock", "HangWatch",
        "PreJit"
    ];

    // komet.json as Komet 1.x wrote it: the display settings, then every knob under its saved name, a switch as a bool
    private static readonly string[] SettingsFile =
    [
        "Visible:Boolean", "Corner:String", "Opacity:Float", "Interval:Float", "BenchSeconds:Float", "ShowFps:Boolean",
        "ShowLows:Boolean", "ShowFrametime:Boolean", "ShowGraph:Boolean", "ShowMods:Boolean", "ShowPins:Boolean",
        "ToastMs:Float", "PinnedMods:Array", "Preset:String", "Chosen:Array", "Profiled:Integer", "WindowTab:String", "WindowX:String", "WindowY:String", "DebugLanguage:String",
        "UpdateCheck:Boolean", "UpdateAsked:Boolean", "FontScale:Float", "ShowAdvanced:Boolean", "GraphicsMenu:Boolean",
        "MenuBlur:Boolean", "ShaderUseCache:Boolean", "DistantShadows:Boolean", "LeanVaryings:Boolean", "SharpUpscale:Boolean", "SsaoLean:Boolean", "LeanGodRays:Boolean", "FrustumSweep:Boolean",
        "FarShadowReuse:Boolean", "NearShadowReuse:Boolean", "EntityCasters:Boolean", "IndirectDraw:Boolean", "OcclusionCulling:Boolean",
        "GpuTerrainCulling:Boolean", "GlQueryCache:Boolean", "VulkanShaderCache:Boolean",
        "VulkanGpuTimes:Boolean", "VulkanCompactFaces:Boolean", "PoolTrimLevel:Integer", "VulkanOnly:Boolean",
        "VulkanPacing:Boolean", "VulkanOnlyLean:Boolean", "VulkanFrameBridge:Boolean", "VulkanFarCache:Boolean", "VulkanQuietUniforms:Boolean", "SunOcclusion:Boolean", "AnimatableCulling:Boolean",
        "PotCulling:Boolean", "HelpCompose:Boolean", "MapTileCulling:Boolean", "MapTileUpload:Boolean", "IdleAnimators:Boolean",
        "WindowSizeCache:Boolean", "PoolScale:Integer", "GlErrorPoll:Boolean", "ChunkLookup:Boolean",
        "PoolFragments:Boolean", "MeshRecycle:Boolean", "FacePacking:Boolean", "FaceSorting:Boolean",
        "ClimateCache:Boolean", "PartitionReuse:Boolean", "HandlerLists:Boolean", "CookingMatch:Boolean",
        "AnimationFrames:Boolean", "InitOnce:Boolean", "ShapeInitMemo:Boolean", "EntityTessBudget:Integer",
        "UploadCap:Integer", "UploadYield:Boolean", "ChunkLoadBudget:Integer", "RecyclerTrim:Integer", "ChunkPacking:Integer",
        "BlockEntityBudget:Boolean", "EventBusSweep:Boolean", "ListenerSlots:Boolean", "BlockEntityCaches:Boolean",
        "CloudTileScratch:Boolean", "DecompressScratch:Boolean", "ProtoListCache:Boolean", "BushShapes:Boolean",
        "ClutterMeshes:Boolean", "CullerRays:Boolean", "MapReads:Boolean", "Ascii85Text:Boolean",
        "TreeKeys:Boolean", "MapPixels:Boolean", "MapSaveScratch:Boolean", "LightScratch:Boolean", "LightRepair:Boolean",
        "ParticleLight:Boolean", "ColumnNoiseScratch:Boolean", "ExtendedRows:Boolean", "VisibleFaces:Boolean",
        "FaceLight:Boolean", "OwnTessellation:Boolean", "TessBlockPos:Boolean", "PartRecycling:Boolean",
        "NearFirst:Boolean", "WorkerThreads:Integer", "TessPriority:Integer", "JitWarm:Boolean", "OccludedChunks:Boolean", "DeepCulling:Boolean", "FarFoliage:Integer",
        "GcLatency:Boolean", "GcCompact:Boolean", "ServerProcess:Boolean", "InventoryShapes:Boolean", "WorldGenScratch:Boolean", "PatchLookup:Boolean",
        "IconBudget:Boolean", "SelectionScratch:Boolean", "ProfilerPool:Boolean", "FramePacer:Boolean", "PreJit:Boolean"
    ];

    private static readonly string[] Last = ["PreJit"], Unpatched = ["TessSafety"];

    [OneTimeSetUp]
    public void LoadALanguage() => GameLang.EnsureLoaded();

    [TearDown]
    public void Close() => Features.Close();

    private static Feature[] All() => Features.All.ToArray();

    [Test]
    public void TheFeaturesInstallInTheirOrder()
    {
        Assert.That(All().Select(f => f.Id), Is.EqualTo(InstallOrder));
    }

    // The HUD switches these on while it is built, from komet.json; each one's Install clears it, so it has to come first
    [TestCase("ModTimes")]
    [TestCase("RenderCost")]
    public void WhatTheHudSwitchesOnInstallsBeforeIt(string id)
    {
        Assert.That(Array.IndexOf(InstallOrder, id), Is.LessThan(Array.IndexOf(InstallOrder, "Hud")));
    }

    [Test]
    public void EveryRequiredFeatureComesEarlier()
    {
        var ids = All().Select(f => f.Id).ToList();
        var requires = All().SelectMany((f, i) => f.Requires.Select(r => (f.Id, Required: r, At: i))).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(requires, Is.Not.Empty);
            foreach (var (id, required, at) in requires)
                Assert.That(ids.IndexOf(required), Is.InRange(0, at - 1), $"{id} requires {required}");
        });
    }

    // PreJit after the HUD's renderers; the stages install in table order
    [Test]
    public void TheLateStagesAreFew()
    {
        var all = All();
        Assert.Multiple(() =>
        {
            Assert.That(all.Where(f => f.Stage == FeatureStage.Last).Select(f => f.Id), Is.EqualTo(Last));
            Assert.That(all.Select(f => f.Stage), Is.Ordered, "the stages install in table order");
        });
    }

    // The server process (HostSystem) installs these without a client API
    [TestCaseSource(nameof(ServerOnly))]
    public void AServerFeatureInstallsWithoutTheClient(string id)
    {
        Assert.That(() => new FeatureRig(id).Dispose(), Throws.Nothing);
    }

    private static IEnumerable<string> ServerOnly() => All().Where(f => f.ServerOnly).Select(f => f.Id);

    // One key and one saved name a knob, each in its owner's list and starting at a value in its range
    [Test]
    public void EveryKnobIsOneOfItsOwners()
    {
        var knobs = Knobs.BuiltIn.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(knobs.Select(k => k.Key), Is.Unique);
            Assert.That(knobs.Select(k => k.Persisted), Is.Unique);
            Assert.That(knobs.Select((k, i) => k.Order == i && All()[k.Owner].Knobs.Any(o => o.Key == k.Key)), Is.All.True);
            Assert.That(knobs.Where(k => k.Engine < k.Min || k.Engine > k.Max).Select(k => k.Key), Is.Empty);
            Assert.That(Knobs.Count, Is.EqualTo(Knobs.BuiltInCount));
        });
    }

    // Stops run in reverse install order before UnpatchAll: PreJit's first, TessWorkers' before the pool's, GraphicsMenu's before
    // the HUD's window goes, the renderer's before the Vulkan device's; TessSafety clears after the unpatch
    [TestCase("PreJit", "HangWatch")]
    [TestCase("TessWorkers", "WorkerPool")]
    [TestCase("GraphicsMenu", "Hud")]
    [TestCase("VulkanTerrain", "VulkanCore")]
    public void AStopRunsBeforeThoseOfTheFeaturesBeforeIt(string first, string then)
    {
        var stops = Enumerable.Reverse(All()).Where(f => f.Stop is not null).Select(f => f.Id).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(stops.IndexOf(first), Is.GreaterThanOrEqualTo(0).And.LessThan(stops.IndexOf(then)));
            Assert.That(All().Where(f => f.Unpatched is not null).Select(f => f.Id), Is.EqualTo(Unpatched));
        });
    }

    [Test]
    public void AFeatureIsFoundByItsIdOrItsKnob()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Features.Find("UploadCap"), Is.EqualTo(Features.Find("ChunkBudget")).And.GreaterThan(0));
            Assert.That(Features.Find("MenuBlur"), Is.EqualTo(Array.IndexOf(InstallOrder, "MenuBlur")));
            Assert.That(Features.Find("Nothing"), Is.EqualTo(-1));
        });
    }

    // Each feature pins the bodies it reproduces, skips or replays to those of Vintage Story 1.22.7: a game update that fails here
    // needs the feature's proof checked again or its golden tests re-run (the rebuilt graphics tabs looked at again: new controls,
    // other handlers) and the constant renewed
    [TestCaseSource(nameof(Fingerprints))]
    public void TheFingerprintIsThatOfTheInstalledEngine(string id, int index)
    {
        var bodies = All()[Features.Find(id)].Shapes[index];
        var shape = EngineShape.Of(bodies.Methods());
        Assert.That(shape, Is.EqualTo(bodies.Expected), $"{id} changed: 0x{shape:X16}UL");
    }

    private static IEnumerable<TestCaseData> Fingerprints() =>
        All().SelectMany(f => f.Shapes.Select((_, i) =>
            new TestCaseData(f.Id, i).SetArgDisplayNames(i == 0 ? f.Id : f.Id + i)));

    // Write reaches the knob's static and no other knob's, and only a difference is written
    [TestCaseSource(nameof(KnobKeys))]
    public void AWriteReachesTheKnobsStaticAlone(string key)
    {
        var (index, before) = (Knobs.Find(key), Knobs.Snapshot());
        var knob = Knobs.At(index);
        var other = before[index] == knob.Engine ? Other(knob) : knob.Engine;
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(Knobs.Write(index, before[index]), Is.False, "the same value is not written");
                Assert.That(Knobs.Write(index, other), Is.True);
                Assert.That((knob.Get(), Knobs.Snapshot()[index]), Is.EqualTo((other, other)));
                Assert.That(Knobs.Snapshot().Where((v, i) => i != index && v != before[i]).Count(), Is.Zero, "no other knob moved");
            });
        }
        finally
        {
            _ = Knobs.Write(index, before[index]);
        }
    }

    private static IEnumerable<string> KnobKeys() => Knobs.BuiltIn.ToArray().Select(k => k.Key);

    // A value that is not the engine's
    private static int Other(Knob knob) => knob.Engine == knob.Max ? knob.Min : knob.Max;

    // Two holders: the knobs stay at the engine's values until the second lets go, a write meanwhile is what comes back, and the
    // settings still show what was wanted
    [TestCaseSource(nameof(Holdable))]
    public void AHoldKeepsTheEngineUntilTheLastRelease(string id)
    {
        var feature = Features.Find(id);
        var knobs = All()[feature].Knobs.Select(k => Knobs.Find(k.Key)).ToArray();
        var before = Knobs.Snapshot();
        try
        {
            foreach (var k in knobs) _ = Knobs.Write(k, Other(Knobs.At(k)));
            Assert.That((Features.Hold(feature), Features.Hold(feature)), Is.EqualTo((true, true)));
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Knobs.At(k).Engine)));
                Assert.That(knobs.Select(k => Knobs.Snapshot()[k]), Is.EqualTo(knobs.Select(k => Other(Knobs.At(k)))));
                Assert.That(knobs.Select(Features.LockText), Has.All.Not.Null);
            });
            foreach (var k in knobs)
            {
                Assert.That(Knobs.Write(k, Knobs.At(k).Engine), Is.False);
                Assert.That(Knobs.Snapshot()[k], Is.EqualTo(Knobs.At(k).Engine), "recorded");
                Assert.That(Knobs.Write(k, Later(Knobs.At(k))), Is.False);
            }

            Assert.That(Features.Release(feature), Is.True);
            Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Knobs.At(k).Engine)),
                "one holds");
            Assert.That(Features.Release(feature), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Later(Knobs.At(k)))));
                Assert.That(knobs.Select(Features.LockText), Has.All.Null);
                Assert.That(Features.Release(feature), Is.False, "nothing left to release");
            });
        }
        finally
        {
            Features.Close();
            _ = Knobs.Apply(before);
        }
    }

    private static IEnumerable<string> Holdable() => All().Where(f => f.Knobs.Length > 0).Select(f => f.Id);

    // Another value than the engine's, below the first one where the range has room
    private static int Later(Knob knob)
    {
        var other = Other(knob);
        return other - 1 > knob.Engine ? other - 1 : other;
    }

    [Test]
    public void AFeatureWithoutAKnobCannotBeHeld()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Features.Hold(Features.Find("TessSafety")), Is.False);
            Assert.That(Features.HoldsText(), Is.EqualTo("none"));
        });
    }

    [Test]
    public void ClosingEndsTheHolds()
    {
        var (feature, knob) = (Features.Find("FrustumSweep"), Knobs.Find("FrustumSweep"));
        Assert.That(FrustumSweep.Enabled, Is.True);
        _ = Features.Hold(feature);
        _ = Features.Hold(feature);
        Assert.Multiple(() =>
        {
            Assert.That((FrustumSweep.Enabled, Features.Held(knob)), Is.EqualTo((false, true)));
            Assert.That(Features.HoldsText(), Is.EqualTo("FrustumSweep (2)"));
        });
        Features.Close();
        Assert.That((FrustumSweep.Enabled, Features.Held(knob), Features.HoldsText()),
            Is.EqualTo((true, false, "none")));
    }

    // The settings page shows the player's value, dimmed, with the reason above the hint
    [Test]
    public void AHeldKnobsRowIsLocked()
    {
        var pages = new KometPages(new HudSettings { ShowAdvanced = true }, () => { }, _ => { }, () => { }, () => { }, () => { })
            .Build();
        var render = pages.Single(p => p.Id == "komet-render");
        var row = Enumerable.Range(0, render.Count).Select(i => render[i])
            .Single(r => r.Label == KometPages.T("distantshadows"));
        Assert.That((row.IsEnabled, row.Locked?.Invoke()), Is.EqualTo((true, (string?)null)));
        _ = Features.Hold(Features.Find("DistantShadows"));
        Assert.Multiple(() =>
        {
            Assert.That(row.IsEnabled, Is.False);
            Assert.That(row.Locked?.Invoke(), Is.EqualTo(HudText.Translate("settings-held")));
            Assert.That(row.Get(), Is.EqualTo(1), "the player's value");
        });
    }

    // An installed feature at its knob's engine value is off, held it is held off; the HUD lists what is not active
    [Test]
    public void TheHudListsWhatIsNotActive()
    {
        using var rig = new FeatureRig("MenuBlur");
        var before = Backdrop.Enabled;
        try
        {
            Backdrop.Enabled = true;
            Features.Poll();
            var active = Features.NotActive;
            Assert.That((rig.State, Features.StateOf(rig.Feature)), Is.EqualTo((FeatureState.Active, FeatureState.Active)));
            Backdrop.Enabled = false;
            Features.Poll();
            Assert.That((rig.State, Features.NotActive), Is.EqualTo((FeatureState.Off, active + 1)));
            _ = Features.Hold(rig.Feature);
            Features.Poll();
            Assert.That((Features.StateOf(rig.Feature), Features.FirstNotActive().Length > 0), Is.EqualTo((FeatureState.HeldOff, true)));
        }
        finally
        {
            Features.Close();
            Backdrop.Enabled = before;
        }
    }

    // A transpiler of another mod on the body the feature replaces stands it down at the recheck, and it comes back once that is gone
    [TestCase("AnimationFrames")]
    [TestCase("InitOnce")]
    [TestCase("ShapeInitMemo")]
    [TestCase("AnimatableCulling")]
    [TestCase("IdleAnimators")]
    public void AProbeSeesAnotherModOnTheFirstSeam(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var rig = new FeatureRig(id);
        Assert.That(rig.State, Is.EqualTo(FeatureState.Active));
        var seam = All()[rig.Feature].Shapes[0].Methods()[0];
        using (var foreign = new TestHarmony("komet-test-feature-foreign"))
        {
            _ = foreign.Patch(seam, transpiler: Foreign.Transpiler);
            Features.Recheck();
            Assert.That(rig.State, Is.EqualTo(FeatureState.StoodDown));
        }

        Features.Recheck();
        Assert.That(rig.State, Is.EqualTo(FeatureState.Active));
    }

    [Test]
    public void AServerFeatureDoesNotApplyOnARemoteServer()
    {
        using var harmony = new TestHarmony("komet-test-feature-remote");
        var feature = Features.Find("ColumnNoiseScratch");
        Features.InstallAt(new FeatureContext(harmony, null!, new QuietLogger(), false), feature);
        Assert.Multiple(() =>
        {
            Assert.That(Features.Evaluate(feature), Is.EqualTo(FeatureState.NotApplicable));
            Assert.That(harmony.GetPatchedMethods(), Is.Empty);
        });
    }

    // Every knob under the name it is saved as, a switch as a bool; a file read back and saved again is the same file
    [Test]
    public void TheSettingsFileKeepsItsKeys()
    {
        var json = JsonConvert.SerializeObject(new HudSettings());
        var loaded = JsonConvert.DeserializeObject<HudSettings>(json)!;
        var logger = new CapturingLogger();
        loaded.Knobs.Apply(logger, HudSettings.FileName);
        Assert.Multiple(() =>
        {
            Assert.That(JObject.Parse(json).Properties().Select(p => p.Name + ":" + p.Value.Type),
                Is.EqualTo(SettingsFile));
            Assert.That(JsonConvert.SerializeObject(loaded), Is.EqualTo(json));
            Assert.That(logger.Lines, Is.Empty);
        });
    }

    [Test]
    public void AnOldFileKeepsOnlyWhatThePlayerChanged()
    {
        var file = JObject.Parse(JsonConvert.SerializeObject(new HudSettings()));
        _ = file.Remove("Profiled");
        _ = file.Remove("Chosen");
        file["ChunkPacking"] = 3; // changed by the player (Komet's default is 1)
        var loaded = JsonConvert.DeserializeObject<HudSettings>(file.ToString())!;
        loaded.Knobs.Apply(new CapturingLogger(), HudSettings.FileName);
        var packing = Knobs.Find("ChunkPacking");
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Chosen, Is.EquivalentTo((string[])["ChunkPacking"]));
            Assert.That(loaded.Knobs.Knob(packing), Is.EqualTo(3));
            Assert.That(loaded.Profiled, Is.EqualTo(1));
        });
        loaded.Knobs.SetKnob(packing, KometSettings.KnobDefault(packing)); // back to the default: follows the computer again
        Assert.That(loaded.Chosen, Is.Empty);
    }

    // Before 3.0 the renderer was several switches: the hybrid (VulkanOnly off) becomes OpenGL, Vulkan-only stays Vulkan only
    // with all the parts it needed on
    [TestCase(true, true, 1)]
    [TestCase(false, true, 0)]
    [TestCase(true, false, 0)]
    public void AnOldRendererBecomesOpenGlOrVulkan(bool only, bool parts, int expected)
    {
        var file = JObject.Parse(JsonConvert.SerializeObject(new HudSettings()));
        foreach (var part in (string[])["VulkanCore", "VulkanTerrain", "VulkanScene"]) file[part] = parts;
        file["VulkanOnly"] = only;
        file["Chosen"] = new JArray("VulkanCore", "VulkanTerrain", "VulkanScene", "VulkanOnly");
        var loaded = JsonConvert.DeserializeObject<HudSettings>(file.ToString())!;
        try
        {
            loaded.Knobs.Apply(new CapturingLogger(), HudSettings.FileName);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.Knobs.Knob(Knobs.Find("VulkanOnly")), Is.EqualTo(expected));
                Assert.That(VulkanRenderer.Only, Is.EqualTo(expected == 1));
                Assert.That(loaded.Chosen, Is.EquivalentTo((string[])["VulkanOnly"]), "the parts are gone");
            });
        }
        finally
        {
            VulkanRenderer.Only = false;
        }
    }
}
