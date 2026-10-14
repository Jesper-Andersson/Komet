using System.Runtime.CompilerServices;
using Cairo;
using HarmonyLib;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;

namespace Komet.Hud;

internal sealed partial class HudOverlay
{
    private const int MaxPrepared = 1024, MaxNested = 64, MaxWarmed = 32;

    private const string Glyphs =
        "0123456789 abcdefghijklmnopqrstuvwxyzäöüß ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÜ .,:;%()[]/+-–×·Ø~";

    private static readonly Type[] Warmed =
    [
        typeof(HudOverlay), typeof(HudUi), typeof(HudUiFonts), typeof(HudCanvas), typeof(HudMotion), typeof(HudLive),
        typeof(FrameStats), typeof(RenderPassStats), typeof(SpikeLedger), typeof(GpuStats), typeof(ModTimes),
        typeof(HudSettings), typeof(FrameClock), typeof(OptionsScreen), typeof(HudPanel), typeof(HudLine),
        typeof(HudText), typeof(HudWindow), typeof(DebugWindow), typeof(Growth), typeof(Features)
    ];

    private static void Warm(ILogger logger, HudUiFonts fonts)
    {
        if (!NotNull(logger) || !NotNull(fonts)) return;
        _ = Task.Run(() => Prepare(fonts)).ContinueWith(failed => logger.Warning("Komet HUD: warm-up failed ({0})",
                failed.Exception?.GetBaseException().Message), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    // First-call JIT and font loading cost the frame after F7 60-80 ms; warmed, the cold first frame drops ~46 -> ~12
    // ms.
    private static void Prepare(HudUiFonts fonts)
    {
        WarmFonts(fonts);
        var prepared = 0;
        foreach (var type in Warmed.Bounded(MaxWarmed))
        {
            prepared += PrepareType(type);
            foreach (var nested in AccessTools.InnerTypes(type).Bounded(MaxNested)) prepared += PrepareType(nested);
        }

        _ = Assert(prepared > 0);
    }

    // cairo's font caches are process-wide and locked: a private surface on a pool thread loads the faces. SetupContext
    // (writes options) stays on the main thread.
    private static void WarmFonts(HudUiFonts fonts)
    {
        if (!NotNull(fonts) || !Assert(fonts.Body.UnscaledFontsize > 0)) return;
        using var surface = new ImageSurface(Format.Argb32, 64, 64);
        using var context = new Context(surface);
        using var options = new FontOptions();
        options.Antialias = Antialias.Subpixel; // what SetupContext sets, part of the scaled font's cache key
        context.FontOptions = options;
        foreach (var font in (ReadOnlySpan<CairoFont>)[fonts.Small, fonts.Body, fonts.Strong, fonts.Stat, fonts.Big, fonts.Huge, fonts.Mono])
        {
            context.SelectFontFace(font.Fontname, font.Slant, font.FontWeight);
            context.SetFontSize(scaled(font.UnscaledFontsize));
            _ = context.TextExtents(Glyphs);
            context.MoveTo(0, 32);
            context.ShowText(Glyphs);
        }
    }

    private static int PrepareType(Type type)
    {
        if (!NotNull(type) || type.ContainsGenericParameters) return 0;
        var prepared = 0;
        foreach (var method in AccessTools.GetDeclaredMethods(type).Bounded(MaxPrepared))
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) continue;
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                prepared++;
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                                          or MissingMemberException)
            {
                // the first call compiles it as before
            }
        }

        return Assert(prepared >= 0) ? prepared : 0;
    }
}
