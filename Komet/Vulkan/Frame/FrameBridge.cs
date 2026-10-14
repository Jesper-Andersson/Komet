using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// Vulkan-only, a refresh the engine misses (a GC pause, a spike, or just a monitor faster than the frames): a thread of its
// own shows the last frame turned by where the camera is going, extrapolated from the last two frames' rotation, so turning
// stays smooth. Each frame keeps three copies: the window before the GUI (scene), after it (final) and the scene's depth.
// A pixel the GUI changed, or one within HandBlocks of the eye (the first-person hand: it turns with the camera), is shown as
// the engine drew it; every other one is the scene seen from the extrapolated rotation. Rotation only, so no depth is needed
// for the warp itself. A frame is read only once its present published it, from three slots the engine never writes while
// the bridge reads one.
internal sealed unsafe class FrameBridge : IDisposable
{
    private const int Slots = 3, Group = 16, PushBytes = 96;
    private const double Late = 1.25, MaxAheadMs = 100, MaxStretch = 4;

    // Of the predicted turn a bridged frame shows this share: the next real frame's camera is never known, and one that
    // turned less than predicted must not step back (bench at 100 fps on 144 Hz: steps back 186 at 1.0, 30 at 0.7)
    private const double Damping = 0.7;

    // Bridged only where the engine is late against its own pace (a hitch: Hitch times the median of its last Usual
    // frames), not on every frame of a steady rate below the monitor's: mixing bridged and real frames all the time was
    // jitter (user, 107 fps on 144 Hz)
    private const int Usual = 16;
    private const double Hitch = 1.5;

    // The median of the intervals copied out under the gate (0 while fewer than half the ring)
    private static double UsualMs(Span<double> sorted, int count)
    {
        if (count < Usual / 2 || !Assert(count <= Usual) || !Assert(sorted.Length >= count)) return 0;
        sorted[..count].Sort();
        return sorted[count / 2];
    }
    private const float HandBlocks = 1f, Still = 1e-5f;
    private const uint SlotUsage = Vk.Sampled | Vk.TransferDst, OutputUsage = Vk.Storage | Vk.Sampled | Vk.TransferSrc;

    private const string Source = """
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(binding = 0) uniform sampler2D scene;
        layout(binding = 1) uniform sampler2D final;
        layout(binding = 2) uniform sampler2D depth;
        layout(binding = 3, rgba8) uniform writeonly image2D target;
        layout(push_constant) uniform Warp { mat4 rotation; vec4 lens; vec4 depthLens; } warp;

        // The eye's distance at uv from the scene's depth (GL's [0, 1]); far when the frame has no depth copy
        float eye(vec2 uv)
        {
            if (warp.depthLens.w == 0.0) return 1e9;
            return warp.depthLens.y / (texture(depth, uv).r * 2.0 - 1.0 + warp.depthLens.x);
        }

        void main()
        {
            ivec2 size = imageSize(target);
            ivec2 at = ivec2(gl_GlobalInvocationID.xy);
            if (at.x >= size.x || at.y >= size.y) return;
            vec2 uv = (vec2(at) + 0.5) / vec2(size);
            vec4 over = texelFetch(final, at, 0);
            if (any(greaterThan(abs(over - texelFetch(scene, at, 0)), vec4(0.001))) || eye(uv) < warp.depthLens.z)
            {
                imageStore(target, at, over); // the GUI, or what turns with the camera: as the engine drew it
                return;
            }

            vec2 ndc = uv * 2.0 - 1.0;
            vec3 look = mat3(warp.rotation) * vec3((ndc.x + warp.lens.z) / warp.lens.x, (ndc.y + warp.lens.w) / warp.lens.y, -1.0);
            if (look.z > -1e-4)
            {
                imageStore(target, at, over);
                return;
            }

            vec2 from = vec2(warp.lens.x * look.x + warp.lens.z * look.z, warp.lens.y * look.y + warp.lens.w * look.z) / -look.z;
            from = from * 0.5 + 0.5;
            if (eye(from) < warp.depthLens.z) from = uv; // the hand stood there: what lies behind it is not known
            imageStore(target, at, vec4(texture(scene, from).rgb, 1.0));
        }
        """;

    // A frame as published: its slot, the view's rotation (world to view), the projection's terms and when it was taken
    // At: when its camera was read; ShownAt: when the engine presented it (the lag between is what a bridged frame keeps)
    public readonly record struct Shot(int Slot, Matrix4x4 View, Vector4 Lens, Vector4 DepthLens, long At, long Presents,
        long ShownAt = 0);

    private sealed class Slot(SharedImage scene, SharedImage final, SharedImage? depth) : IDisposable
    {
        public SharedImage Scene { get; } = scene;
        public SharedImage Final { get; } = final;
        public SharedImage? Depth { get; } = depth;

        public void Dispose()
        {
            _ = NotNull(Scene) && NotNull(Final);
            Scene.Dispose();
            Final.Dispose();
            Depth?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public Matrix4x4 Rotation; // its sixteen floats in GLSL's column order
        public Vector4 Lens, DepthLens;
    }

    private readonly VulkanDevice _device;
    private readonly WindowSwapchain _chain;
    private double _period; // ms between the monitor's refreshes; Retuned from the engine thread
    private readonly Slot?[] _slots = new Slot?[Slots];
    private readonly Lock _gate = new(); // _last, _previous, _leased
    private readonly Lock _work = new(); // a bridged frame being made, against the engine thread remaking the images it reads
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly System.Func<(ulong Image, int Width, int Height, ulong Rendered), bool> _record;
    private SharedImage? _output;
    private ulong _pipeline, _layout, _setLayout, _linear, _nearest, _pool, _done, _acquired;
    private IntPtr _commands;
    private Thread? _thread;
    private volatile bool _stop;
    private Shot? _last, _previous;
    private int _leased = -1;
    private long _due; // Stopwatch ticks when the next bridged frame is due unless the engine presents first
    private readonly double[] _intervals = new double[Usual]; // the engine's last present intervals (ms), a ring
    private int _interval;
    private long _lastReal;
    private Push _push;

    public long Bridged { get; private set; } // frames the bridge showed

    // Shown frames, real or bridged, whose yaw stepped against the turn before it (a turn of over 0.1° a frame): what a
    // player sees as jitter. Steady turning shows none.
    public long Reversed { get; private set; }

    private float _shownYaw = float.NaN, _shownStep;

    private void Showed(Matrix4x4 view)
    {
        var yaw = MathF.Atan2(view.M31, view.M33);
        if (!float.IsNaN(_shownYaw))
        {
            var step = MathF.IEEERemainder(yaw - _shownYaw, MathF.Tau);
            if (MathF.Abs(step) > 1e-4f)
            {
                if (MathF.Abs(_shownStep) > 1.7e-3f && MathF.Sign(step) != MathF.Sign(_shownStep)) Reversed++;
                _shownStep = step;
            }
        }

        _shownYaw = yaw;
        _ = Assert(Reversed >= 0) && Assert(float.IsFinite(yaw));
    }

    private FrameBridge(VulkanDevice device, WindowSwapchain chain, double periodMs)
    {
        (_device, _chain, _period) = (device, chain, periodMs);
        _record = Recorded;
        _ = NotNull(chain) && Assert(periodMs > 0);
    }

    private double PeriodMs => Volatile.Read(ref _period);

    // The window's monitor runs at another rate now (moved to another monitor, mode changed)
    public void Retuned(int refreshHz)
    {
        if (!Assert(refreshHz > 0) || _stop) return;
        Volatile.Write(ref _period, Period(refreshHz));
        _wake.Set();
        _ = Assert(PeriodMs > 0);
    }

    private static double Period(int refreshHz) => 1000.0 / Math.Clamp(refreshHz <= 0 ? 60 : refreshHz, 24, 1000);

    // The bridge for a monitor of refreshHz, its thread started; null where the device cannot make it
    public static FrameBridge? Create(VulkanDevice device, WindowSwapchain chain, int refreshHz, out string why)
    {
        if (!NotNull(device) || !NotNull(chain) || VkApi.CmdPushDescriptors == null) return Refused(out why, "no push descriptors");
        var bridge = new FrameBridge(device, chain, Period(refreshHz));
        if (bridge.Made(out why)) return bridge.Started();
        bridge.Dispose();
        return null;
    }

    private static FrameBridge? Refused(out string why, string reason)
    {
        why = reason;
        _ = Assert(why.Length > 0);
        return null;
    }

    private FrameBridge Started()
    {
        _due = long.MaxValue; // none before the engine's first present
        _thread = new Thread(Run) { IsBackground = true, Name = "komet-frame-bridge", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ = Assert(_thread.IsAlive || _stop);
        return this;
    }

    private bool Made(out string why)
    {
        why = "";
        var words = Shaderc.Compile(Source, Shaderc.Compute, "frame-bridge", out var error);
        if (words is null) return Failed(out why, $"the bridge's shader does not compile: {error}");
        var handle = _device.Handle;
        var bindings = stackalloc Vk.LayoutBinding[4];
        for (var i = 0; i < 4; i++)
            bindings[i] = new Vk.LayoutBinding
            {
                Binding = (uint)i, Type = i == 3 ? Spirv.StorageImage : Spirv.SampledImage, Count = 1, Stages = Vk.StageCompute
            };
        var set = new Vk.SetLayoutInfo
        {
            SType = Vk.SetLayoutCreateInfo, Flags = Vk.PushDescriptorLayout, BindingCount = 4, Bindings = bindings
        };
        ulong setLayout, layout, module, pipeline;
        if (VkApi.CreateSetLayout(handle, &set, null, &setLayout) != Vk.Success) return Failed(out why, "no descriptor layout");
        _setLayout = setLayout;
        var range = new Vk.PushRange { Stages = Vk.StageCompute, Size = PushBytes };
        var info = new Vk.PipelineLayoutInfo
        {
            SType = Vk.PipelineLayoutCreateInfo, SetLayoutCount = 1, SetLayouts = &setLayout, PushRangeCount = 1, PushRanges = &range
        };
        if (VkApi.CreatePipelineLayout(handle, &info, null, &layout) != Vk.Success) return Failed(out why, "no pipeline layout");
        _layout = layout;
        fixed (uint* code = words)
        {
            var moduleInfo = new Vk.ModuleInfo { SType = Vk.ShaderModuleCreateInfo, CodeSize = (nuint)(4 * words.Length), Code = code };
            if (VkApi.CreateShaderModule(handle, &moduleInfo, null, &module) != Vk.Success) return Failed(out why, "no shader module");
        }

        var main = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var compute = new Vk.ComputeInfo
        {
            SType = Vk.ComputePipelineCreateInfo, Layout = layout,
            Stage = new Vk.StageInfo { SType = Vk.StageCreateInfo, Stage = Vk.StageCompute, Module = module, Name = main }
        };
        var made = VkApi.CreateComputePipelines(handle, _device.PipelineCache, 1, &compute, null, &pipeline) == Vk.Success;
        VkApi.DestroyShaderModule(handle, module, null);
        if (!made) return Failed(out why, "no compute pipeline");
        (_pipeline, _linear, _nearest) = (pipeline, Sampler(1), Sampler(0));
        (_pool, _done, _acquired) = (_device.CommandPool(), _device.Fence(true), _device.Fence(false));
        _commands = _pool == 0 ? IntPtr.Zero : _device.Allocate(_pool);
        return _linear != 0 && _nearest != 0 && _commands != IntPtr.Zero && _done != 0 && _acquired != 0 ||
               Failed(out why, "no sampler, command buffer or fence");
    }

    // filter 1: linear (the warp), 0: nearest (depth); clamped to the edge
    private ulong Sampler(uint filter)
    {
        var info = new Vk.SamplerInfo
        {
            SType = Vk.SamplerCreateInfo, MagFilter = filter, MinFilter = filter, AddressU = 2, AddressV = 2, AddressW = 2
        };
        ulong sampler;
        return Assert(filter <= 1) && VkApi.CreateSampler(_device.Handle, &info, null, &sampler) == Vk.Success ? sampler : 0;
    }

    private static bool Failed(out string why, string reason)
    {
        why = reason;
        return !Assert(why.Length > 0);
    }

    // Engine thread, at the GUI's start: a slot neither published last nor read by the bridge, made for the window's and
    // depth's size and format (a new size waits for the device once); -1 where none can be had
    public int Take(SharedImage window, SharedImage? depth)
    {
        if (!NotNull(window) || _stop) return -1;
        int at;
        lock (_gate)
        {
            at = -1;
            for (var i = 0; i < Slots && at < 0; i++)
                if (i != _last?.Slot && i != _leased)
                    at = i;
        }

        if (!Index(at, Slots)) return -1;
        var wanted = depth is { } d && d.Format == SharedFormat.Depth32F ? d : null;
        if (_slots[at] is { } held && Fits(held.Scene, window) && DepthFits(held.Depth, wanted)) return at;
        lock (_work) // the bridge thread makes no frame meanwhile
        {
            _ = _device.WaitIdle(); // the old copies may be read by a bridged frame on the GPU still
            _slots[at]?.Dispose();
            _slots[at] = Copies(window, wanted);
            return _slots[at] is null ? -1 : at;
        }
    }

    private static bool Fits(SharedImage made, SharedImage like) =>
        Assert(made.Image != 0) && Assert(like.Image != 0) && made.Width == like.Width && made.Height == like.Height &&
        made.Format == like.Format;

    // A depth copy for wanted, or none for none
    private static bool DepthFits(SharedImage? made, SharedImage? wanted) =>
        made is null || wanted is null ? made is null && wanted is null : Fits(made, wanted);

    private Slot? Copies(SharedImage window, SharedImage? depth)
    {
        _ = NotNull(window) && Assert(window.Width > 0);
        var size = (window.Width, window.Height, 1, 1);
        var scene = Rested(SharedImage.Private(_device, size, window.Format, SlotUsage, out _));
        var final = Rested(SharedImage.Private(_device, size, window.Format, SlotUsage, out _));
        var depths = depth is null ? null : Rested(SharedImage.Private(_device, (depth.Width, depth.Height, 1, 1), depth.Format, SlotUsage, out _));
        if (scene is null || final is null || (depth is not null && depths is null))
        {
            scene?.Dispose();
            final?.Dispose();
            depths?.Dispose();
            return null;
        }

        if (_output is null || !Fits(_output, window))
        {
            _output?.Dispose();
            _output = Rested(SharedImage.Private(_device, size, SharedFormat.Rgba8, OutputUsage, out _));
        }

        return _output is null ? null : new Slot(scene, final, depths);
    }

    // At rest in GENERAL for good: the frame's copies and the bridge's reads move it no further
    private SharedImage? Rested(SharedImage? image)
    {
        _ = Assert(_device.Handle != IntPtr.Zero) && Assert(!_stop);
        if (image is null || image.Rest(_device, Vk.LayoutUndefined, Vk.LayoutGeneral)) return image;
        image.Dispose();
        return null;
    }

    public ulong Scene(int slot) => Index(slot, Slots) && _slots[slot] is { } s && Assert(s.Scene.Image != 0) ? s.Scene.Image : 0;
    public ulong Final(int slot) => Index(slot, Slots) && _slots[slot] is { } s && Assert(s.Final.Image != 0) ? s.Final.Image : 0;
    public ulong Depth(int slot) => Index(slot, Slots) && _slots[slot] is { Depth: { } d } && Assert(d.Image != 0) ? d.Image : 0;

    // Engine thread: the camera of the frame being drawn (view: world to view, column-major; the perspective projection)
    public static Shot Shoot(int slot, float[] view, double[] projection, bool depth)
    {
        if (!NotNull(view) || !NotNull(projection) || view.Length < 16 || projection.Length < 16) return default;
        var rotation = new Matrix4x4(view[0], view[4], view[8], 0, view[1], view[5], view[9], 0, view[2], view[6], view[10], 0,
            0, 0, 0, 1);
        var lens = new Vector4((float)projection[0], (float)projection[5], (float)projection[8], (float)projection[9]);
        var depthLens = new Vector4((float)projection[10], (float)projection[14], HandBlocks, depth ? 1 : 0);
        return new Shot(slot, rotation, lens, depthLens, Stopwatch.GetTimestamp(), 0);
    }

    // Recording thread, right after the engine's present: shown is the frame it showed (null: one without copies)
    public void Shown(Shot? shown, long presents)
    {
        _ = Assert(presents >= 0) && NotNull(_wake);
        var now = Stopwatch.GetTimestamp();
        Span<double> sorted = stackalloc double[Usual];
        int count;
        lock (_gate) // one section: the shot, the interval and the ring's copy for the median
        {
            if (shown is { } s && Index(s.Slot, Slots) && _slots[s.Slot] is not null)
            {
                (_previous, _last) = (_last, s with { Presents = presents, ShownAt = now });
                Showed(s.View);
            }
            else (_previous, _last) = (null, null); // nothing to bridge from until two frames came with copies

            if (_lastReal > 0) _intervals[_interval++ % Usual] = (now - _lastReal) * 1000.0 / Stopwatch.Frequency;
            _lastReal = now;
            count = Math.Min(_interval, Usual);
            _intervals.AsSpan(0, count).CopyTo(sorted);
        }

        Volatile.Write(ref _due,
            now + (long)(Math.Max(PeriodMs * Late, Hitch * UsualMs(sorted, count)) * Stopwatch.Frequency / 1000));
        _wake.Set();
    }

    private void Run()
    {
        var tick = Stopwatch.Frequency / 1000.0;
        _ = Assert(tick > 0) && Assert(PeriodMs > 0);
        for (var i = 0L; i < long.MaxValue && !_stop; i++)
        {
            var due = Volatile.Read(ref _due);
            var waitMs = (due - Stopwatch.GetTimestamp()) / tick;
            if (waitMs <= 0.2 && VulkanRenderer.Bridging && Bridge())
                Volatile.Write(ref _due, Stopwatch.GetTimestamp() + (long)(PeriodMs * tick)); // the next a refresh on
            else _ = _wake.Wait(TimeSpan.FromMilliseconds(waitMs > 0.2 ? Math.Min(waitMs, PeriodMs) : PeriodMs)); // or a publish
            _wake.Reset();
        }
    }

    // A frame turned to where the camera looks, presented; false when there is nothing new to show. It keeps the engine's
    // lag: a real frame shows its camera a frame's drawing after reading it, so the bridged one shows the last camera moved
    // on by the time since that frame was shown, not to the moment it is shown (which jumped ahead, and the next real frame
    // back, by the drawing time: jitter when turning fast)
    private bool Bridge()
    {
        Shot last, previous;
        _ = Assert(_thread is null || Thread.CurrentThread == _thread) && Assert(_leased < 0);
        lock (_gate)
        {
            if (_last is not { } l || _previous is not { } p || _output is null) return false;
            (last, previous, _leased) = (l, p, l.Slot);
        }

        try
        {
            // last's camera was read as its frame began, about when previous was shown; the next real frame's will be read
            // about when last was: keep last's lag (its drawing), never pass the next real frame's camera
            var lag = Math.Max(0, last.ShownAt - previous.ShownAt);
            var target = Math.Min(Stopwatch.GetTimestamp() - lag, last.ShownAt);
            var turned = Extrapolated(previous, last, (long)(Math.Max(0, target - previous.ShownAt) * Damping));
            if (Angle(Matrix4x4.Multiply(turned, Matrix4x4.Transpose(last.View))) < Still) return false; // the screen shows it
            var warp = Matrix4x4.Multiply(last.View, Matrix4x4.Transpose(turned)); // the new view's directions into last's
            _push = new Push { Rotation = Matrix4x4.Transpose(warp), Lens = last.Lens, DepthLens = last.DepthLens }; // GLSL's columns
            var done = _done;
            lock (_work)
            {
                // the last bridged frame's commands are done with (reset only as the next is submitted)
                if (VkApi.WaitForFences(_device.Handle, 1, &done, 1, 50_000_000) != Vk.Success) return false;
                _slot = last.Slot;
                var shown = _chain.Bridge(_acquired, last.Presents, _record);
                if (shown) Bridged++;
                if (shown) lock (_gate) Showed(turned);
                return shown;
            }
        }
        finally
        {
            lock (_gate) _leased = -1;
        }
    }

    private int _slot;

    // The rotation the camera has ahead of last's: the last turn from previous to last, continued at its pace for at most
    // MaxAheadMs (a stall longer than that holds the last view)
    private static Matrix4x4 Extrapolated(Shot previous, Shot last, long ahead)
    {
        var span = last.At - previous.At;
        if (span <= 0 || !Assert(MaxStretch > 0) || !Assert(ahead >= 0)) return last.View;
        ahead = Math.Min(ahead, (long)(MaxAheadMs * Stopwatch.Frequency / 1000));
        var stretch = Math.Clamp((double)ahead / span, 0, MaxStretch);
        var turn = Matrix4x4.Multiply(last.View, Matrix4x4.Transpose(previous.View)); // previous's view to last's
        return Matrix4x4.Multiply(Power(turn, (float)stretch), last.View);
    }

    private static float Angle(Matrix4x4 r) =>
        Finite(r.M11 + r.M22 + r.M33) && Assert(MathF.Abs(r.M44 - 1) < 1e-3f) ? MathF.Acos(Math.Clamp((r.M11 + r.M22 + r.M33 - 1) / 2, -1f, 1f)) : 0;

    // The rotation r to the power f (its axis, f times its angle; Rodrigues)
    private static Matrix4x4 Power(Matrix4x4 r, float f)
    {
        var angle = Angle(r);
        if (angle < Still || !Finite(angle) || !Finite(f)) return Matrix4x4.Identity;
        var axis = Vector3.Normalize(new Vector3(r.M32 - r.M23, r.M13 - r.M31, r.M21 - r.M12));
        if (!float.IsFinite(axis.X)) return Matrix4x4.Identity;
        var (s, c) = MathF.SinCos(angle * f);
        var k = new Matrix4x4(0, -axis.Z, axis.Y, 0, axis.Z, 0, -axis.X, 0, -axis.Y, axis.X, 0, 0, 0, 0, 0, 0);
        var k2 = Matrix4x4.Multiply(k, k);
        var result = Matrix4x4.Identity + k * s + k2 * (1 - c);
        result.M44 = 1;
        return result;
    }

    // Into the acquired image: the warp, then the output blitted upright (the window's rows are GL's, bottom first)
    private bool Recorded((ulong Image, int Width, int Height, ulong Rendered) into)
    {
        if (_slots[_slot] is not { } slot || _output is not { } output || !Assert(into.Image != 0) || !VulkanDevice.Begin(_commands))
            return false;
        var commands = _commands;
        _ = Assert(output.Width > 0) && Assert(_pipeline != 0);
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        var memory = new Vk.GlobalBarrier { SType = Vk.MemoryBarrier, SrcAccess = all, DstAccess = all };
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 1, &memory, 0, null, 0, null);
        VkApi.CmdBindPipeline(commands, Vk.BindCompute, _pipeline);
        var images = stackalloc Vk.ImageDescriptor[4];
        images[0] = new Vk.ImageDescriptor { Sampler = _linear, View = slot.Scene.View, Layout = Vk.LayoutGeneral };
        images[1] = new Vk.ImageDescriptor { Sampler = _nearest, View = slot.Final.View, Layout = Vk.LayoutGeneral };
        images[2] = new Vk.ImageDescriptor { Sampler = _nearest, View = (slot.Depth ?? slot.Scene).Sampled, Layout = Vk.LayoutGeneral };
        images[3] = new Vk.ImageDescriptor { View = output.View, Layout = Vk.LayoutGeneral };
        var writes = stackalloc Vk.WriteInfo[4];
        for (var i = 0; i < 4; i++)
            writes[i] = new Vk.WriteInfo
            {
                SType = Vk.WriteDescriptorSet, Binding = (uint)i, Count = 1, Type = i == 3 ? Spirv.StorageImage : Spirv.SampledImage,
                Images = images + i
            };
        VkApi.CmdPushDescriptors(commands, Vk.BindCompute, _layout, 0, 4, writes);
        var push = _push;
        if (slot.Depth is null) push.DepthLens.W = 0;
        VkApi.CmdPushConstants(commands, _layout, Vk.StageCompute, 0, PushBytes, &push);
        VkApi.CmdDispatch(commands, (uint)((output.Width + Group - 1) / Group), (uint)((output.Height + Group - 1) / Group), 1);
        var barriers = stackalloc Vk.ImageBarrier[2];
        barriers[0] = output.Barrier((Vk.LayoutGeneral, Vk.LayoutTransferSrc));
        barriers[1] = Swapped(into.Image, (Vk.LayoutUndefined, Vk.LayoutTransferDst));
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 2, barriers);
        var layer = new Vk.Layers { Aspect = Vk.AspectColor, Count = 1 };
        var blit = new Vk.ImageBlit
        {
            Source = layer, SourceX1 = output.Width, SourceY1 = output.Height, SourceZ1 = 1, Target = layer, TargetY0 = into.Height,
            TargetX1 = into.Width, TargetZ1 = 1
        };
        var linear = (output.Width, output.Height) != (into.Width, into.Height);
        VkApi.CmdBlitImage(commands, output.Image, Vk.LayoutTransferSrc, into.Image, Vk.LayoutTransferDst, 1, &blit,
            linear ? Vk.FilterLinear : Vk.FilterNearest);
        barriers[0] = output.Barrier((Vk.LayoutTransferSrc, Vk.LayoutGeneral));
        barriers[1] = Swapped(into.Image, (Vk.LayoutTransferDst, Vk.LayoutPresentSrc));
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 2, barriers);
        var done = _done;
        return VkApi.ResetFences(_device.Handle, 1, &done) == Vk.Success && _device.SubmitAlone(commands, into.Rendered, _done);
    }

    private static Vk.ImageBarrier Swapped(ulong image, (int From, int To) layouts)
    {
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        _ = Assert(image != 0) && Assert(layouts.From != layouts.To);
        return new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = all, DstAccess = all, OldLayout = layouts.From, NewLayout = layouts.To,
            SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored, Image = image,
            Range = new Vk.ColorRange { Aspect = Vk.AspectColor, Levels = 1, Layers = 1 }
        };
    }

    // The thread stopped first, then the device idle before anything it used goes
    public void Dispose()
    {
        _ = Assert(_device.Handle != IntPtr.Zero) && Assert(Thread.CurrentThread != _thread);
        _stop = true;
        _wake.Set();
        _ = _thread?.Join(1000);
        _ = _device.WaitIdle();
        var handle = _device.Handle;
        foreach (var slot in _slots.Bounded(Slots)) slot?.Dispose();
        Array.Clear(_slots);
        _output?.Dispose();
        if (_pipeline != 0) VkApi.DestroyPipeline(handle, _pipeline, null);
        if (_layout != 0) VkApi.DestroyPipelineLayout(handle, _layout, null);
        if (_setLayout != 0) VkApi.DestroySetLayout(handle, _setLayout, null);
        foreach (var sampler in (ReadOnlySpan<ulong>)[_linear, _nearest])
            if (sampler != 0) VkApi.DestroySampler(handle, sampler, null);
        _device.Destroy(_done);
        _device.Destroy(_acquired);
        _device.DestroyPool(_pool);
        (_output, _pipeline, _layout, _setLayout, _thread) = (null, 0, 0, 0, null);
        _wake.Dispose();
    }
}
