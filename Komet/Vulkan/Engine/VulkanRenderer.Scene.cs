using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Vulkan;

// The scene: every GL draw, clear and blit outside the terrain passes, judged as GL is called (GlTap's Touch group).
internal static partial class VulkanRenderer
{
    public enum Part
    {
        Sky,
        Entities,
        Particles,
        Post,
        Other
    }

    private static readonly Dictionary<string, Part> Parts = new(StringComparer.Ordinal)
    {
        ["sky"] = Part.Sky, ["nightsky"] = Part.Sky, ["celestialobject"] = Part.Sky, ["aurora"] = Part.Sky,
        ["cloudvolumetric"] = Part.Sky, ["cloudmap"] = Part.Sky, ["entityanimated"] = Part.Entities,
        ["shadowmapentityanimated"] = Part.Entities, ["helditem"] = Part.Entities, ["instanced"] = Part.Entities,
        ["particlescube"] = Part.Particles, ["particlesquad"] = Part.Particles, ["particlesquad2d"] = Part.Particles,
        ["findbright"] = Part.Post, ["blur"] = Part.Post, ["godrays"] = Part.Post, ["ssao"] = Part.Post,
        ["bilateralblur"] = Part.Post, ["luma"] = Part.Post, ["blit"] = Part.Post, ["final"] = Part.Post,
        ["transparentcompose"] = Part.Post, ["colorgrade"] = Part.Post
    };

    private static SceneRenderer? _scene;
    private static readonly bool[] Parting = new bool[5]; // the parts the scene takes this frame
    private static readonly Dictionary<int, Part> PartOf = []; // by GL program, worked out once

    private static bool AnyPart => Array.IndexOf(Parting, true) >= 0;

    private static bool SceneOn => _scene is not null && GlTap.Scene && AnyPart;

    internal static Part PartNamed(string name) =>
        NotNull(name) && Parts.TryGetValue(name, out var part) && Assert(part <= Part.Other) ? part : Part.Other;

    private static bool Takes(int program, string name)
    {
        if (!Assert(program > 0) || _api?.World is not ClientMain game) return false;
        if (!PartOf.TryGetValue(program, out var part))
        {
            if (PartOf.Count >= 1024) PartOf.Clear();
            PartOf[program] = part = PartNamed(name);
        }

        var takes = Parting[(int)part] && Assert(Stage(game) >= EnumRenderStage.Before);
        if (takes) Mark(part); // its draw is recorded next
        return takes;
    }

    // Every stage's work and what comes between frames: OpenGL's picture is not shown
    private static bool ClearsTaken() => _api?.World is ClientMain && AnyPart && Assert(Parting.Length == 5);

    // Every part: one left to OpenGL would be missing from the picture
    private static void DecideScene()
    {
        Array.Fill(Parting, OnlyOn);
        _ = Assert(Parting.Length == 5) && Assert(!AnyPart || OnlyOn);
    }

    // A tap with a gap rebuilds all mirrors (writes went unheard); the record is always tapped
    private static bool Tapped()
    {
        if (GlTap.Tapped && GlTap.Scene && GlTap.Recording) return Assert(GlTap.Tapped);
        GlTap.Untap();
        _watched = false;
        _scene?.Mirrors.Forget();
        if (GlTap.Tap()) return Assert(GlTap.Tapped);
        _logger?.Warning("Komet: OpenTK's GL table lacks {0}", GlTap.Missing);
        return false;
    }

    // What Hooked wired last: unchanged, it runs every frame without remaking the scene methods' dozen delegates
    private static (SceneRenderer? Scene, TerrainRenderer? Renderer) _wired;

    private static void Hooked(TerrainRenderer renderer, bool on)
    {
        if (!NotNull(renderer)) return;
        if (on && _scene is null) _scene = SceneRenderer.Create(renderer, _logger);
        if (on && _scene is { } wired && _wired == (wired, renderer) && ReferenceEquals(GlTap.Writes, wired)) return;
        if (on && _scene is { } scene)
        {
            _wired = (scene, renderer);
            (scene.Takes, scene.Clearing, scene.SamplerObject) = (Takes, ClearsTaken, Sampler);
            (GlTap.Drawing, GlTap.Clearing, GlTap.Writes, GlTap.Blitting) = (scene.Draw, scene.Clear, scene, scene.Blit);
            (GlTap.Querying, GlTap.Answering, GlTap.Touching) = (scene.Querying, scene.Answer, Touched);
            GlTap.Reading = renderer.Answer; // OpenGL's reads answered from Vulkan's images
            GlTap.TextureClearing = renderer.TextureCleared; // and its images cleared by Vulkan
            (renderer.Frame.Opened, renderer.Frame.Ending) = (scene.Opened, FrameEnding);
            // the engine's own pool uploads, which OpenGL makes too, are copied by Vulkan (PoolWritten)
            Rendering.FacePacking.Through = OnlyFaces;
        }
        else
        {
            _wired = default;
            _scene?.FlushEarly(); // OpenGL's again, before it hears nothing more of the scene
            if (_scene is { } off) off.Between = false;
            (GlTap.Drawing, GlTap.Clearing, GlTap.Writes, GlTap.Querying, GlTap.Answering) = (null, null, null, null, null);
            (GlTap.Blitting, GlTap.Reading, GlTap.TextureClearing) = (null, null, null);
            (GlTap.Touching, renderer.Frame.Opened, renderer.Frame.Ending) = (null, null, null);
            Rendering.FacePacking.Through = null;
        }

        (GlTap.ProgramGone, GlTap.Linking) = (Deleted, Linked);
        _ = Assert(!on || GlTap.Drawing is not null || _scene is null);
    }

    // Runs with no segment open
    private static void FrameEnding()
    {
        if (_scene is not { } scene || !Assert(_renderer is not null)) return;
        scene.Mirrors.Ending();
        scene.Resolve();
        _renderer!.Ending();
    }

    // A lean pool buffer (OpenGL holds a stand-in and drops the write): the faces go to Vulkan straight, without the two
    // glBindBuffer calls and the tap the GL upload would take (3 % of the main thread at a join)
    private static unsafe bool OnlyFaces(VAO vao, int buffer, long offset, ReadOnlySpan<byte> faces)
    {
        if (_renderer is not { } renderer || buffer <= 0 || !GlTap.LeanBuffer((uint)buffer) || !NotNull(vao)) return false;
        fixed (byte* data = faces) return Assert(offset >= 0) && renderer.PoolWritten((uint)buffer, offset, faces.Length, (IntPtr)data);
    }

    // GPU work no scene judge saw is missing from the picture (SceneRenderer.Touching)
    private static void Touched(GlTap.Touch kind, uint id)
    {
        if (_renderer is null || !Assert(kind <= GlTap.Touch.Mipmap)) return;
        _scene?.Touching(kind, id);
    }

    // GL program names are reused: the port and mirror go with the program
    private static void Deleted(uint program)
    {
        if (!Assert(program > 0) || !Assert(program < int.MaxValue)) return;
        _ = PartOf.Remove((int)program);
        ProgramPorts.Forget((int)program);
        var at = Array.IndexOf(Built, (int)program);
        if (at >= 0) Built[at] = 0; // a rebuilt terrain program with the same name is ported anew
        Guarded(static p =>
        {
            _scene?.Forget(p);
            _renderer?.Forget(p);
        }, (int)program);
    }

    private static void SceneGone()
    {
        _scene?.FlushEarly(); // the clears kept between frames are OpenGL's again
        _scene?.Dispose();
        _scene = null;
        PartOf.Clear();
        Array.Clear(Parting);
        _ = Assert(PartOf.Count == 0) && Assert(!AnyPart);
    }

    private static string SceneParts()
    {
        if (!SceneOn || !Assert(Parting.Length == 5)) return "";
        string[] names = ["sky", "entities", "particles", "post-processing", "the rest"];
        var on = names.Where((_, i) => Parting[i]).ToArray();
        _ = Assert(on.Length <= names.Length);
        return string.Join(", ", on);
    }
}
