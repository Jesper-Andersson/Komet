using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Hud;

internal enum UpdateState
{
    Checking,
    Verified,
    Mismatch,
    Outdated,
    Unverified,
    Failed,
    // Nothing to compare, nothing failed
    NoRelease
}

// Replaced whole so render-thread readers never see half a result. "" = unknown.
internal sealed record UpdateReport(UpdateState State, string Detail = "", string Tag = "", string Newest = "",
    string Installed = "", string Published = "", string Released = "")
{
    public const int HashLength = SHA256.HashSizeInBytes * 2;
    public static readonly UpdateReport Pending = new(UpdateState.Checking);
    public bool Match => Installed.Length > 0 && Installed == Published;

    public (string Key, Rgba? Color) Notice() => State switch
    {
        UpdateState.Checking => ("hud-update-checking", null),
        UpdateState.Verified => ("hud-update-verified", HudCanvas.Good),
        UpdateState.Mismatch => ("hud-update-mismatch", HudCanvas.Error),
        UpdateState.Outdated => ("hud-update-outdated", HudCanvas.Warning),
        UpdateState.Unverified => ("hud-update-unverified", null),
        UpdateState.Failed => ("hud-update-failed", null),
        _ => ("hud-update-norelease", null)
    };

    public string Verdict() => State switch
    {
        UpdateState.Checking => "verify-checking",
        UpdateState.Failed => "verify-failed",
        UpdateState.NoRelease => "verify-norelease",
        _ when Match => "verify-match",
        _ when Installed.Length > 0 && Published.Length > 0 => "verify-mismatch",
        _ when Tag.Length == 0 => "verify-norelease",
        _ when Installed.Length == 0 => "verify-nofile",
        _ => "verify-nochecksum"
    };
}

internal sealed class UpdateCheck : IDisposable
{
    private const string Repo = "xtoast-dev/Komet";
    private const int MaxReleases = 30, MaxAssets = 16, TimeoutSeconds = 15, ShortHash = 7;

    private static readonly Uri Feed = new($"https://api.github.com/repos/{Repo}/releases?per_page={MaxReleases}");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
    private readonly ILogger _logger;
    private readonly bool _preview;
    private readonly string _sourcePath;
    private volatile UpdateReport _report = UpdateReport.Pending;
    private int _running;

    public UpdateCheck(ILogger logger, string version, bool preview, string commit, string sourcePath)
    {
        _logger = logger;
        (_preview, _sourcePath) = (preview, sourcePath);
        BuildTag = preview ? "preview-" + commit : "v" + version;
        if (!NotNull(logger) || !Assert(version.Length > 0) || !Assert(!preview || commit.Length > 0))
        {
            _report = new UpdateReport(UpdateState.Failed, "bad arguments");
            return;
        }

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Komet/" + version);
        Start();
    }

    public UpdateReport Report => _report;
    public string BuildTag { get; }

    public void Dispose() => _http.Dispose();

    public void Start()
    {
        if (!Assert(BuildTag.Length > 1) || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _report = UpdateReport.Pending;
        _ = Task.Run(Run).ContinueWith(
            failed => _logger.Warning("Komet update check: {0}", failed.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task Run()
    {
        try
        {
            // GitHub orders by tagged commit date (rebases scramble it): newest = published last. The stamp's DateTime
            // string is MM/dd/yyyy, so text comparison is wrong.
            var releases = JArray.Parse(await _http.GetStringAsync(Feed).ConfigureAwait(false)).Take(MaxReleases)
                .OfType<JObject>()
                .OrderByDescending(static r => (DateTime?)r["published_at"] ?? DateTime.MinValue).ToList();
            if (!Assert(releases.Count <= MaxReleases) || !Assert(BuildTag.Length > 1))
            {
                _report = new UpdateReport(UpdateState.Failed, "bad feed");
                return;
            }

            var same = releases.Find(r => Tag(r) == BuildTag);
            var newest = releases.Find(r =>
                _preview
                    ? Tag(r).StartsWith("preview-", StringComparison.Ordinal)
                    : (bool?)r["prerelease"] == false && Tag(r).StartsWith('v'));
            var installed = File.Exists(_sourcePath) ? await Hash(_sourcePath).ConfigureAwait(false) : "";
            var published = same is null ? "" : await Published(same).ConfigureAwait(false);
            var released = same is null ? "" : HudText.LocalTime((string?)same["published_at"] ?? "");
            _report = Judge(BuildTag, same is not null, newest is null ? "" : Tag(newest), installed, published,
                released);
        }
        // Newtonsoft casts throw ArgumentException, new Uri FormatException
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException
                                      or JsonException or InvalidOperationException or InvalidCastException
                                      or ArgumentException or FormatException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            _report = new UpdateReport(UpdateState.Failed, e.Message);
        }
        finally
        {
            if (_report.State == UpdateState.Checking)
                _report = new UpdateReport(UpdateState.Failed, "unexpected error"); // logged by Start
            _logger.Notification("Komet update check: {0} {1}", _report.State, _report.Detail);
            Volatile.Write(ref _running, 0);
        }
    }

    // Unlisted (local/CI, fork) or ahead of the newest: NoRelease, not a failure.
    internal static UpdateReport Judge(string tag, bool listed, string newest, string installed, string published,
        string released)
    {
        if (!Assert(tag.Length > 1) || !Assert(listed || published.Length == 0))
            return new UpdateReport(UpdateState.Failed, "bad arguments");
        var report = new UpdateReport(UpdateState.Unverified, tag, listed ? tag : "", newest, installed, published,
            released);
        var behind = Behind(tag, newest);
        if (!listed && !behind) report = report with { State = UpdateState.NoRelease };
        else if (installed.Length > 0 && published.Length > 0)
            report = report with
            {
                State = report.Match ? UpdateState.Verified : UpdateState.Mismatch,
                Detail = installed[..Math.Min(ShortHash, installed.Length)]
            };
        if (report.State != UpdateState.Mismatch && behind)
            report = report with { State = UpdateState.Outdated, Detail = newest };
        return report;
    }

    // Release tags compare as versions (v2.0.0 < v2.0.1-pre < v2.0.1); preview tags name unordered commits: any other
    // is newer.
    internal static bool Behind(string tag, string newest)
    {
        if (!Assert(tag.Length > 1) || !NotNull(newest) || newest.Length == 0 || newest == tag) return false;
        if (Version(tag) is not { } mine || Version(newest) is not { } theirs) return true;
        var order = mine.Core.CompareTo(theirs.Core);
        if (order != 0) return order < 0;
        if (mine.Pre.Length == 0) return false; // the release of a pre-release this build is ahead of
        return theirs.Pre.Length == 0 || string.CompareOrdinal(theirs.Pre, mine.Pre) > 0;
    }

    private static (Version Core, string Pre)? Version(string tag)
    {
        if (!Assert(tag.Length > 0) || !tag.StartsWith('v')) return null;
        var dash = tag.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? tag[1..] : tag[1..dash];
        if (!System.Version.TryParse(core, out var version) || !Assert(version.Major >= 0)) return null;
        return (version, dash < 0 ? "" : tag[(dash + 1)..]);
    }

    private async Task<string> Published(JObject release)
    {
        var asset = release["assets"]?.Take(MaxAssets).FirstOrDefault(a => (string?)a["name"] == "komet.sha256");
        var url = (string?)asset?["browser_download_url"];
        if (url is null || !Assert(url.StartsWith("https://", StringComparison.Ordinal))) return "";
        var line = (await _http.GetStringAsync(new Uri(url)).ConfigureAwait(false)).Split(' ', 2)[0].Trim();
        return Assert(line.Length == UpdateReport.HashLength) ? line : "";
    }

    private static async Task<string> Hash(string path)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            return Assert(hash.Length == UpdateReport.HashLength) && Assert(path.Length > 0) ? hash : "";
        }
    }

    private static string Tag(JObject release) => NotNull(release) ? (string?)release["tag_name"] ?? "" : "";
}
