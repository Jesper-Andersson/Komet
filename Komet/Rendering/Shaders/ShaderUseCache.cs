using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// ShaderProgramBase.Use uploads every default uniform per call (per entity, held item, block entity). Each program
// keeps its last values and sends only changes; Fill runs every Use since values change mid-frame
// (SystemRenderShadowMap rewrites shadow ranges and matrices between passes); a frame's first Use sends all. Uniforms
// engine code writes outside Use with values Fill wouldn't produce go up every Use (External), as Use resets them:
// SunMoon zeroes shadowIntensity, PreparedStandardShader relies on the reset.
internal static class ShaderUseCache
{
    // Uniforms follow from the program's shader includes; the GUI program gets its own light
    private const int FogLightF = 1, FogLightV = 2, ShadowCoords = 4, VertexWarp = 8, SkyColor = 16, ColorMap = 32;
    private const int Underwater = 64, Gui = 128;

    private const int MaxSlots = 64, MaxUbos = 16, MaxIncludes = 8;
    private const bool External = true;

    private static readonly (string File, int Bit)[] Includes = [("fogandlight.fsh", FogLightF),
        ("fogandlight.vsh", FogLightV), ("shadowcoords.vsh", ShadowCoords), ("vertexwarp.vsh", VertexWarp),
        ("skycolor.fsh", SkyColor), ("colormap.vsh", ColorMap), ("underwatereffects.fsh", Underwater)];

    private static readonly Vec3f GuiLight = new(0.7071068f, -0.7071068f, 0f);
    private static readonly State?[] States = new State[128];
    private static int _frame, _settingsFrame = -1;
    private static float _viewDistance, _viewDistanceLod0;
    private static MethodBase? _use;
    private static bool _foreign;

    public static bool Enabled
    {
        get;
        set
        {
            field = value;
            if (value) Array.Clear(States);
        }
    } = true;

    public static long Calls { get; private set; } // totals while Counting.Hud, main thread
    public static long Uploads { get; private set; }
    public static long Skips { get; private set; }

    public static void Install(Harmony harmony)
    {
        Array.Clear(States); // the last world's programs are disposed
        _settingsFrame = -1; // settings may have changed in the menu
        var update = AccessTools.Method(typeof(DefaultShaderUniforms), nameof(DefaultShaderUniforms.Update));
        var use = AccessTools.Method(typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use));
        if (!NotNull(update) || !NotNull(use)) return;
        _use = use;
        if (!NotNull(harmony.Patch(update, postfix: new HarmonyMethod(NextFrame)))) return;
        _ = NotNull(harmony.Patch(use, new HarmonyMethod(Use)));
    }

    internal static void NextFrame()
    {
        _frame++;
        if (Enabled) ReadSettings();
        // Another mod's transpiler on Use (e.g. snowcapmeltfix uploading its own uniform) would be skipped with the
        // body: the engine's Use runs then. Checked per frame, as client mods patch after Install.
        var foreign = _use is not null && EngineShape.Foreign([_use], PatchKinds.Body, null);
        if (foreign == _foreign) return;
        _foreign = foreign;
        Array.Clear(States); // the engine's uploads bypass the cached values
    }

    // SettingsBase lower-cases keys per read: 3 reads per Use at 118 Uses a frame were 1.8 MB/s. Applies next frame.
    private static void ReadSettings()
    {
        var distance = ClientSettings.ViewDistance;
        (_viewDistance, _viewDistanceLod0, _settingsFrame) =
            (distance, Math.Min(640, distance) * ClientSettings.LodBias, _frame);
        _ = Assert(distance > 0) && Finite(_viewDistanceLod0);
    }

    private static bool Use(ShaderProgramBase __instance)
    {
        if (!Enabled || _foreign || (uint)__instance.PassId >= (uint)States.Length) return true;
        if (!Assert(__instance.ProgramId > 0) || !NotNull(__instance.uniformLocations)) return true;
        if (ShaderProgramBase.CurrentShaderProgram is { } active && active != __instance)
            throw new InvalidOperationException("Already a different shader (" + active.PassName + ") in use!");
        if (__instance.Disposed) throw new InvalidOperationException("Can't use a disposed shader!");

        GL.UseProgram(__instance.ProgramId);
        ShaderProgramBase.CurrentShaderProgram = __instance;
        if (Counting.Hud) Calls++;
        if (Stage(__instance) is not { } s) return true;
        Upload(s, Changed(s, s.Frame != _frame));
        s.Frame = _frame;
        BindUbos(__instance);
        return false;
    }

    // null after overflow: state dropped, this Use left to the engine
    internal static State? Stage(ShaderProgramBase p)
    {
        if (!Index(p.PassId, States.Length) || !Assert(p.ProgramId > 0)) return null;
        var s = States[p.PassId];
        if (s?.Program != p || s.ProgramId != p.ProgramId || s.ShadowQuality != ShaderProgramBase.shadowmapQuality)
            States[p.PassId] = s = Build(p);
        var w = new Writer(s, p, null);
        Fill(ref w, s.Mask);
        if (!w.Overflow) return s;
        States[p.PassId] = null;
        return null;
    }

    // By index: OrderedDictionary's enumerator boxes as IEnumerator on every Use
    internal static void BindUbos(ShaderProgramBase p)
    {
        var ubos = p.ubos;
        if (!NotNull(ubos) || ubos.Count == 0 || !Assert(ubos.Count <= MaxUbos)) return;
        for (var i = 0; i < Math.Min(ubos.Count, MaxUbos); i++) ubos.GetValueAtIndex(i).Bind();
    }

    private static State Build(ShaderProgramBase p)
    {
        var mask = p == ShaderPrograms.Gui ? Gui : 0;
        foreach (var (file, bit) in Includes.Bounded(MaxIncludes))
            if (p.includes.Contains(file)) mask |= bit;
        var s = new State
        { Program = p, ProgramId = p.ProgramId, ShadowQuality = ShaderProgramBase.shadowmapQuality, Mask = mask };
        var layout = new List<Slot>();
        var w = new Writer(s, p, layout);
        Fill(ref w, mask);
        if (!Assert(!w.Overflow) || !Assert(w.Total >= layout.Count) || !Assert(layout.Count <= MaxSlots))
            return s;
        s.Slots = [.. layout];
        (s.Sent, s.Cur, s.Sends) = (new float[w.Total], new float[w.Total], new int[layout.Count]);
        return s;
    }

    private static void Fill(ref Writer w, int mask)
    {
        var platform = ScreenManager.Platform;
        var u = platform.ShaderUniforms;
        if (!NotNull(u) || !Index(mask, 256) || !Assert(platform.FrameBuffers.Count > 12))
        {
            w.Overflow = true;
            return;
        }

        if (_settingsFrame != _frame) ReadSettings(); // a world's first Use can precede its first update
        if ((mask & FogLightF) != 0)
        {
            w.F("zNear", u.ZNear);
            w.F("zFar", u.ZFar);
            w.V3("lightPosition", u.LightPosition3D);
            w.F("shadowIntensity", u.DropShadowIntensity, External); // SystemRenderSunMoon
            w.F("glitchStrength", u.GlitchStrength);
            w.F("psychedelicStrength", u.PsychedelicStrength);
            if (ShaderProgramBase.shadowmapQuality > 0)
            {
                var far = platform.FrameBuffers[11];
                w.Tex("shadowMapFar", far.DepthTextureId);
                w.Tex("shadowMapNear", platform.FrameBuffers[12].DepthTextureId);
                w.F("shadowMapWidthInv", 1f / far.Width);
                w.F("shadowMapHeightInv", 1f / far.Height);
                w.F("viewDistance", _viewDistance);
                w.F("viewDistanceLod0", _viewDistanceLod0);
            }
        }

        if ((mask & FogLightV) != 0)
        {
            w.I("fogSphereQuantity", u.FogSphereQuantity);
            w.Arr("fogSpheres", 1, u.FogSpheres, u.FogSphereQuantity * 8);
            w.I("pointLightQuantity", u.PointLightsCount);
            w.Arr("pointLights", 3, u.PointLights3, u.PointLightsCount);
            w.Arr("pointLightColors", 3, u.PointLightColors3, u.PointLightsCount);
            w.F("flatFogDensity", u.FlagFogDensity, External); // AuroraRenderer, CloudRenderer
            w.F("flatFogStart", u.FlatFogStartYPos - u.PlayerPos.Y, External);
            w.F("glitchStrengthFL", u.GlitchStrength);
            w.F("viewDistance", _viewDistance);
            w.F("viewDistanceLod0", _viewDistanceLod0);
            w.F("nightVisionStrength", u.NightVisionStrength);
        }

        if ((mask & ShadowCoords) != 0)
        {
            w.F("shadowRangeNear", u.ShadowRangeNear);
            w.F("shadowRangeFar", u.ShadowRangeFar);
            w.Arr("toShadowMapSpaceMatrixNear", 16, u.ToShadowMapSpaceMatrixNear, 1);
            w.Arr("toShadowMapSpaceMatrixFar", 16, u.ToShadowMapSpaceMatrixFar, 1);
        }

        FillWorld(ref w, u, platform, mask);
    }

    private static void FillWorld(ref Writer w, DefaultShaderUniforms u, ClientPlatformAbstract platform, int mask)
    {
        if ((mask & VertexWarp) != 0)
        {
            w.F("timeCounter", u.TimeCounter);
            w.F("windWaveCounter", u.WindWaveCounter);
            w.F("windWaveCounterHighFreq", u.WindWaveCounterHighFreq);
            w.F("windSpeed", u.WindSpeed);
            w.F("waterWaveCounter", u.WaterWaveCounter, External); // EchoChamberRenderer
            w.V3("playerpos", u.PlayerPos);
            w.F("globalWarpIntensity", u.GlobalWorldWarp, External); // AnimatableRenderer
            w.F("glitchWaviness", u.GlitchWaviness, External); // AnimatableRenderer
            // AnimatableRenderer, EntityShapeRenderer, EchoChamberRenderer
            w.F("windWaveIntensity", u.WindWaveIntensity, External);
            w.F("waterWaveIntensity", u.WaterWaveIntensity);
            w.I("perceptionEffectId", u.PerceptionEffectId);
            w.F("perceptionEffectIntensity", u.PerceptionEffectIntensity);
        }

        if ((mask & SkyColor) != 0)
        {
            w.F("fogWaveCounter", u.FogWaveCounter);
            w.Tex("sky", u.SkyTextureId);
            w.Tex("glow", u.GlowTextureId);
            w.F("sunsetMod", u.SunsetMod);
            w.I("ditherSeed", u.DitherSeed, External); // SystemRenderSunMoon, one seed on from the sky's
            w.I("horizontalResolution", u.FrameWidth);
            // SystemRenderSkyColor/SunMoon, after player physics
            w.F("playerToSealevelOffset", u.PlayerToSealevelOffset, External);
        }

        if ((mask & ColorMap) != 0)
        {
            w.Arr("colorMapRects", 4, u.ColorMapRects4, 40);
            w.F("seasonRel", u.SeasonRel);
            w.F("seaLevel", u.SeaLevel);
            w.F("atlasHeight", u.BlockAtlasHeight);
            w.F("seasonTemperature", u.SeasonTemperature);
        }

        if ((mask & Underwater) != 0)
        {
            w.Tex("liquidDepth", platform.FrameBuffers[5].DepthTextureId);
            w.F("cameraUnderwater", u.CameraUnderwater);
            w.V4("waterMurkColor", u.WaterMurkColor);
            var primary = platform.FrameBuffers[0];
            w.V2("frameSize", primary.Width, primary.Height);
        }

        if ((mask & Gui) != 0)
            w.V3("lightPosition", GuiLight, External); // GuiDialogCharacter, GuiDialogCreateCharacter
    }

    internal static int Changed(State s, bool force)
    {
        if (!Assert(s.Cur.Length == s.Sent.Length) || !Assert(s.Sends.Length == s.Slots.Length)) return 0;
        var cur = MemoryMarshal.Cast<float, int>(s.Cur.AsSpan());
        var sent = MemoryMarshal.Cast<float, int>(s.Sent.AsSpan());
        var n = 0;
        for (var i = 0; i < Math.Min(s.Slots.Length, MaxSlots); i++)
        {
            ref var slot = ref s.Slots[i];
            var floats = slot.Count * Math.Max(slot.Kind, 1);
            if (floats == 0 || !Assert(slot.Offset + floats <= cur.Length)) continue;
            var now = cur.Slice(slot.Offset, floats);
            var last = sent.Slice(slot.Offset, floats);
            if (!force && !slot.Dirty && !slot.Always && now.SequenceEqual(last))
            {
                if (Counting.Hud) Skips++;
                continue;
            }

            now.CopyTo(last);
            s.Sends[n++] = i;
        }

        if (Counting.Hud) Uploads += n;
        return n;
    }

    private static void Upload(State s, int n)
    {
        if (!Assert(n <= s.Sends.Length)) return;
        for (var k = 0; k < Math.Min(n, MaxSlots); k++)
        {
            ref var slot = ref s.Slots[s.Sends[k]];
            ref var v = ref s.Sent[slot.Offset];
            switch (slot.Kind)
            {
                case 0: GL.Uniform1(slot.Loc, slot.Count, ref Unsafe.As<float, int>(ref v)); break;
                case 1: GL.Uniform1(slot.Loc, slot.Count, ref v); break;
                case 2: GL.Uniform2(slot.Loc, slot.Count, ref v); break;
                case 3: GL.Uniform3(slot.Loc, slot.Count, ref v); break;
                case 4: GL.Uniform4(slot.Loc, slot.Count, ref v); break;
                default: GL.UniformMatrix4(slot.Loc, slot.Count, false, ref v); break;
            }
        }
    }

    internal sealed class State
    {
        public required ShaderProgramBase Program;
        public int ProgramId, ShadowQuality, Mask, Frame = -1;
        public int[] Sends = []; // the slots Changed picked
        public float[] Sent = [], Cur = []; // what each slot last sent, and this Use's values
        public Slot[] Slots = [];
    }

    // Kind = floats per element (1-4 vecN, 16 mat4), 0 int
    internal record struct Slot(int Loc, int Kind, int Offset, int Reserved, bool Always)
    {
        public int Count;
        public bool Dirty;
    }

    // First Fill (with layout) records each slot's location, kind, offset, reserve; later ones write values into Cur
    // and flag slots whose element count changed.
    private ref struct Writer(State state, ShaderProgramBase program, List<Slot>? layout)
    {
        private int _i;
        public int Total;
        public bool Overflow;

        // True with the slot's Cur offset when there is a value to write; false while laying out or after overflow
        private bool Reserve(string name, int kind, int count, int reserve, bool external, out int offset)
        {
            offset = -1;
            if (!Assert(count >= 0) || !Assert(reserve >= count * Math.Max(kind, 1)))
            {
                Overflow = true;
                return false;
            }

            if (layout != null)
            {
                layout.Add(new Slot(program.uniformLocations.GetValueOrDefault(name, -1), kind, Total, reserve,
                    external));
                Total += reserve;
                return false;
            }

            if (Assert(_i < state.Slots.Length))
            {
                ref var slot = ref state.Slots[_i++];
                if (count * Math.Max(kind, 1) <= slot.Reserved)
                {
                    (slot.Dirty, slot.Count, offset) = (slot.Count != count, count, slot.Offset);
                    return true;
                }
            }

            Overflow = true;
            return false;
        }

        public void F(string name, float v, bool external = false)
        {
            if (Reserve(name, 1, 1, 1, external, out var o)) state.Cur[o] = v;
        }

        public void I(string name, int v, bool external = false)
        {
            if (Reserve(name, 0, 1, 1, external, out var o)) state.Cur[o] = BitConverter.Int32BitsToSingle(v);
        }

        public void V2(string name, float x, float y)
        {
            if (Reserve(name, 2, 1, 2, false, out var o)) (state.Cur[o], state.Cur[o + 1]) = (x, y);
        }

        public void V3(string name, Vec3f v, bool external = false)
        {
            if (Reserve(name, 3, 1, 3, external, out var o))
                (state.Cur[o], state.Cur[o + 1], state.Cur[o + 2]) = (v.X, v.Y, v.Z);
        }

        public void V4(string name, Vec4f v)
        {
            if (Reserve(name, 4, 1, 4, false, out var o))
                (state.Cur[o], state.Cur[o + 1], state.Cur[o + 2], state.Cur[o + 3]) = (v.X, v.Y, v.Z, v.W);
        }

        public void Arr(string name, int kind, float[]? src, int count)
        {
            if (src is null) return; // the engine fills some late (ColorMapRects4 as a world closes)
            if (!Assert(kind > 0) || !Assert(count * kind <= src.Length))
            {
                Overflow = true;
                return;
            }

            if (Reserve(name, kind, count, src.Length, false, out var o))
                Array.Copy(src, 0, state.Cur, o, count * kind);
        }

        public readonly void Tex(string name, int textureId)
        {
            if (Assert(name.Length > 0) && layout == null) program.BindTexture2D(name, textureId);
        }
    }
}
