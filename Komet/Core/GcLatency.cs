using System.Runtime;

namespace Komet.Core;

// Under Interactive a fragmented gen2 compacted with all threads stopped (2.75 s at 5.2 GB, view distance 1536),
// SustainedLowLatency keeps it in the background. The one GC setting changeable at runtime; Off restores.
internal static class GcLatency
{
    private static GCLatencyMode? _before;
    private static bool _installed;

    public static bool Enabled
    {
        get;
        set
        {
            field = value;
            Apply();
        }
    } = true;

    public static void Install()
    {
        _installed = true;
        Apply();
        _ = Assert(_installed) && Assert(Enum.IsDefined(GCSettings.LatencyMode));
    }

    public static void Stop()
    {
        _installed = false;
        Apply();
        _ = Assert(_before is null) && Assert(!_installed);
    }

    private static void Apply()
    {
        if (_installed && Enabled)
        {
            if (_before is null && GCSettings.LatencyMode != GCLatencyMode.SustainedLowLatency) _before = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        else if (_before is { } before)
        {
            GCSettings.LatencyMode = before;
            _before = null;
        }

        _ = Assert(Enum.IsDefined(GCSettings.LatencyMode)) && Assert(!_installed || !Enabled || _before is not null ||
                                                                      GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency);
    }
}
