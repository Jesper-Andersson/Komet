using System.Globalization;

namespace Komet.Diagnostics;

// The game's VRAM per DRM client from /proc/self/fdinfo (Linux): OpenGL and Vulkan are separate clients. Duplicate fds
// count once. A few ms per protocol.
internal static class DrmMemory
{
    private const int MaxFds = 8192, MaxLines = 64;

    internal readonly record struct Client(long Id, string Driver, long VramKb, long GttKb);

    public static Client[] Read(string folder = "/proc/self/fdinfo")
    {
        if (!Assert(folder.Length > 0) || !OperatingSystem.IsLinux() || !Directory.Exists(folder)) return [];
        var clients = new Dictionary<long, Client>();
        try
        {
            var files = Directory.GetFiles(folder);
            foreach (var file in files.Bounded(MaxFds))
                if (Parse(SafeRead(file)) is { } client) clients[client.Id] = client;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return Assert(clients.Count <= MaxFds) ? [.. clients.Values.OrderByDescending(static c => c.VramKb)] : [];
    }

    private static string[] SafeRead(string file)
    {
        try
        {
            return Assert(file.Length > 0) ? File.ReadAllLines(file) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return []; // the descriptor closed meanwhile
        }
    }

    // drm-memory-vram is older, drm-resident-vram newer (total: allocated, resident: in VRAM now).
    internal static Client? Parse(string[] lines)
    {
        if (!NotNull(lines)) return null;
        var (id, driver, vram, gtt, resident, total) = (-1L, "", -1L, -1L, -1L, -1L);
        foreach (var line in lines.Bounded(MaxLines))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var (key, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
            switch (key)
            {
                case "drm-client-id": id = Number(value); break;
                case "drm-driver": driver = value; break;
                case "drm-memory-vram": vram = Kb(value); break;
                case "drm-resident-vram": resident = Kb(value); break;
                case "drm-total-vram": total = Kb(value); break;
                case "drm-memory-gtt": gtt = Kb(value); break;
                case "drm-total-gtt" when gtt < 0: gtt = Kb(value); break;
            }
        }

        var kb = vram >= 0 ? vram : Math.Max(resident, total); // vram, else resident, else total
        if (vram < 0 && resident >= 0) kb = resident;
        return Assert(kb >= -1) && id >= 0 && kb >= 0 ? new Client(id, driver, kb, Math.Max(0, gtt)) : null;
    }

    private static long Number(string text) =>
        NotNull(text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

    private static long Kb(string text)
    {
        if (!NotNull(text)) return -1;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || Number(parts[0]) is not (>= 0 and var n)) return -1;
        return parts.Length < 2 ? n / 1024 : parts[1] switch { "KiB" => n, "MiB" => n * 1024, "GiB" => n * 1024 * 1024, _ => n / 1024 };
    }

    // "OpenGL 3410 MB, Vulkan 6380 MB (GTT 210 MB)": the client near Komet's Vulkan ledger is Vulkan, the largest other
    // OpenGL; one shared Mesa client is split by the ledger.
    public static string Text(Client[] clients, double vulkanLedgerMb)
    {
        if (!NotNull(clients) || clients.Length == 0) return "";
        if (clients.Length == 1 && vulkanLedgerMb > 0)
        {
            var one = clients[0];
            var rest = Math.Max(0, one.VramKb / 1024 - (long)vulkanLedgerMb);
            return Assert(rest >= 0)
                ? $"OpenGL and Vulkan in one client {one.VramKb / 1024} MB (GTT {one.GttKb / 1024} MB), OpenGL about {rest} MB"
                : "";
        }

        var vulkan = -1;
        if (vulkanLedgerMb > 0)
        {
            var best = double.MaxValue;
            for (var i = 0; i < Math.Min(clients.Length, MaxLines); i++)
                if (Math.Abs(clients[i].VramKb / 1024.0 - vulkanLedgerMb) is var off && off < best && off < 0.35 * vulkanLedgerMb)
                    (best, vulkan) = (off, i);
        }

        var parts = new List<string>(clients.Length);
        var named = false;
        for (var i = 0; i < Math.Min(clients.Length, MaxLines); i++)
        {
            var c = clients[i];
            var name = "client " + c.Id;
            if (i == vulkan) name = "Vulkan";
            else if (!named) (name, named) = ("OpenGL", true);
            parts.Add($"{name} {c.VramKb / 1024} MB (GTT {c.GttKb / 1024} MB)");
        }

        return Assert(parts.Count > 0) ? string.Join(", ", parts) : "";
    }

    public static long TotalVramMb(Client[] clients) =>
        NotNull(clients) && Assert(clients.Length <= MaxFds) ? clients.Sum(static c => c.VramKb) / 1024 : 0;
}
