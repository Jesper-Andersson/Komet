using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Komet.Vulkan;

// Built programs cost a file read instead of shaderc plus driver compile. A folder per Komet build. Settled hot
// uniforms are kept per program, so the first port already pushes them. Writes run off the render thread, one at a
// time; ports build on builder threads, so shared state is guarded by State.
internal static class ShaderCache
{
    private const int MaxPorts = 1024, MaxSettled = 1024, MaxFile = 64 << 20, KeyBytes = 16, MaxParts = 4;
    private const string Format = "1", HotFile = "hot.json", PortsFolder = "ports", RecipesFile = "pipelines.json";
    private const int MaxRecipes = 256, MaxRecipePrograms = 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals // NaN or infinite uniform defaults
    };

    private static readonly Lock Writing = new(), State = new();
    private static readonly string Build =
        typeof(ShaderCache).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    private static Dictionary<string, string[]>? _settled;
    private static Dictionary<string, List<Recipe>>? _recipes;
    private static bool _recipesQueued;
    private static Task _writes = Task.CompletedTask;
    private static int _kept;

    public static bool Enabled { get; set; } = true;

    // "" until Install, so tests never touch the game's folders
    public static string Folder { get; private set; } = "";
    public static bool Active => Enabled && Folder.Length > 0;

    public static int Loaded { get; private set; }
    public static long PipelineBytes { get; set; }

    public static void Install(ILogger logger, ICoreClientAPI? api = null)
    {
        if (!NotNull(logger)) return;
        Open(Path.Join(Vintagestory.API.Config.GamePaths.Cache, "komet-vulkan"));
        Seeded(api);
        var folder = Folder;
        lock (State) _writes = _writes.ContinueWith(_ => Dropped(folder, logger), TaskScheduler.Default);
        _ = Assert(Active || !Enabled);
    }

    // Pipeline recipes the mod ships (assets/komet/config/pipelines.json, recorded on a 1.22.7 session): a first start
    // warms those pipelines off the engine's thread instead of making each as a draw first needs it
    private static Dictionary<string, List<Recipe>>? _seed;

    private static void Seeded(ICoreClientAPI? api)
    {
        _seed = null;
        if (api?.Assets is not { } assets || !Assert(Folder.Length > 0 || !Enabled)) return;
        try
        {
            var text = assets.TryGet(new AssetLocation("komet", "config/pipelines.json"))?.ToText();
            if (text is null || text.Length > MaxFile) return;
            if (JsonSerializer.Deserialize<Dictionary<string, List<Recipe>>>(text, Json) is { } read)
                lock (State) _seed = read;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            // no seed: recipes come as they are made
        }
    }

    private static void Dropped(string folder, ILogger logger)
    {
        var ports = Path.Join(folder, PortsFolder);
        try
        {
            if (!Assert(folder.Length > 0) || !NotNull(logger) || !Directory.Exists(ports)) return;
            foreach (var old in Directory.GetDirectories(ports).Bounded(MaxPorts))
                if (Path.GetFileName(old) != Build)
                    Directory.Delete(old, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.Warning("Komet: the old Vulkan shader cache stays: {0}", e.Message);
        }
    }

    public static void Open(string folder)
    {
        if (!NotNull(folder)) return;
        lock (State) (Folder, _settled, _recipes, Loaded, _kept, PipelineBytes) = (folder, null, null, 0, 0, 0);
        _ = Assert(Folder == folder);
    }

    public static string Sources(string vertex, string fragment, IReadOnlyDictionary<string, int> attributes)
    {
        if (!NotNull(vertex) || !NotNull(fragment) || !NotNull(attributes)) return "";
        var bound = string.Join(";", attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => $"{a.Key}={a.Value}"));
        return Hash(vertex, fragment, bound);
    }

    public static GlslPort.Ported? Port(string sources, IReadOnlySet<string>? hot, string name)
    {
        if (!Active || !Assert(sources.Length > 0) || !Assert(name.Length > 0)) return null;
        var path = PortPath(sources, hot);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFile) return null;
            var ported = JsonSerializer.Deserialize<GlslPort.Ported>(File.ReadAllBytes(path), Json);
            if (ported is not // every part there and SPIR-V in both stages
                {
                    Vertex.Length: > 5, Fragment.Length: > 5, VertexUniforms: not null, FragmentUniforms: not null,
                    HotUniforms: not null, Samplers: not null, Blocks: not null, Attributes: not null, Outputs: not null
                } || ported.Vertex[0] != Spirv.Magic || ported.Fragment[0] != Spirv.Magic) return null;
            _ = Assert(ported.Vertex.Length < MaxFile / 4 && ported.Fragment.Length < MaxFile / 4);
            lock (State) Loaded++;
            return ported with { Name = name };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                      NotSupportedException)
        {
            return null; // ported anew, and written over
        }
    }

    public static void Keep(string sources, IReadOnlySet<string>? hot, GlslPort.Ported ported)
    {
        if (!Active || !Assert(sources.Length > 0) || !NotNull(ported)) return;
        var path = PortPath(sources, hot);
        lock (State)
        {
            if (_kept >= MaxPorts) return;
            _kept++;
            _writes = _writes.ContinueWith(_ => Write(path, JsonSerializer.SerializeToUtf8Bytes(ported, Json)),
                TaskScheduler.Default);
        }
    }

    public static IReadOnlySet<string>? Settled(string sources)
    {
        if (!Active || !NotNull(sources) || sources.Length == 0) return null;
        lock (State)
        {
            var settled = _settled ??= ReadSettled();
            _ = Assert(settled.Count <= MaxSettled);
            return settled.TryGetValue(sources, out var hot) && hot.Length > 0
                ? new HashSet<string>(hot, StringComparer.Ordinal)
                : null;
        }
    }

    public static void Settle(string sources, IReadOnlySet<string> hot)
    {
        if (!Active || !Assert(sources.Length > 0) || !NotNull(hot) || hot.Count == 0) return;
        string[] names = [.. hot.Order(StringComparer.Ordinal)];
        lock (State)
        {
            var settled = _settled ??= ReadSettled();
            if (settled.TryGetValue(sources, out var known) && known.SequenceEqual(names)) return;
            if (settled.Count >= MaxSettled && !settled.ContainsKey(sources)) return;
            settled[sources] = names;
            var (path, bytes) = (Path.Join(Folder, HotFile), JsonSerializer.SerializeToUtf8Bytes(settled, Json));
            _writes = _writes.ContinueWith(_ => Write(path, bytes), TaskScheduler.Default);
        }
    }

    // A pipeline a port drew with, keyed by the program's sources, so a later start builds it before the first draw.
    // Fixed: see PipelineRecipes.
    public sealed record Recipe(string Inputs, string Target, uint Topology, uint[] Colors, uint Depth, uint[] Fixed,
        long[] Attributes);

    public static Recipe[] Recipes(string sources)
    {
        if (!Active || !NotNull(sources) || sources.Length == 0) return [];
        lock (State)
        {
            var recipes = _recipes ??= ReadRecipes();
            _ = Assert(recipes.Count <= MaxRecipePrograms);
            return recipes.TryGetValue(sources, out var known) ? [.. known] : [];
        }
    }

    // Written off-thread, one at a time: a burst of new pipelines writes the file once or twice
    public static void Remember(string sources, Recipe recipe)
    {
        if (!Active || !Assert(sources.Length > 0) || !NotNull(recipe)) return;
        lock (State)
        {
            var recipes = _recipes ??= ReadRecipes();
            if (!recipes.TryGetValue(sources, out var known))
            {
                if (recipes.Count >= MaxRecipePrograms) return;
                recipes[sources] = known = [];
            }

            if (known.Count >= MaxRecipes || known.Exists(r => Same(r, recipe))) return;
            known.Add(recipe);
            if (_recipesQueued) return;
            _recipesQueued = true;
            var path = Path.Join(Folder, RecipesFile);
            _writes = _writes.ContinueWith(_ => Write(path, RecipeBytes()), TaskScheduler.Default);
        }
    }

    private static bool Same(Recipe a, Recipe b) =>
        NotNull(a) && NotNull(b) && a.Inputs == b.Inputs && a.Target == b.Target && a.Fixed.AsSpan().SequenceEqual(b.Fixed);

    private static byte[] RecipeBytes()
    {
        lock (State)
        {
            _recipesQueued = false;
            _ = Assert(_recipes is null || _recipes.Count <= MaxRecipePrograms);
            return JsonSerializer.SerializeToUtf8Bytes(_recipes ?? [], Json);
        }
    }

    private static Dictionary<string, List<Recipe>> ReadRecipes()
    {
        var path = Path.Join(Folder, RecipesFile);
        var read = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
        try
        {
            if (Assert(Folder.Length > 0) && File.Exists(path) && new FileInfo(path).Length <= MaxFile &&
                JsonSerializer.Deserialize<Dictionary<string, List<Recipe>>>(File.ReadAllBytes(path), Json) is { } file)
                foreach (var (key, recipes) in file.Take(MaxRecipePrograms).ToArray().Bounded(MaxRecipePrograms))
                    if (recipes is not null)
                        read[key] = [.. recipes.Where(Whole).Take(MaxRecipes)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                      NotSupportedException)
        {
            // started anew
        }

        if (_seed is { } seed) // what the file lacks, the mod's seed gives
            foreach (var (key, recipes) in seed.Take(MaxRecipePrograms).ToArray().Bounded(MaxRecipePrograms))
                if (recipes is not null && !read.ContainsKey(key) && read.Count < MaxRecipePrograms)
                    read[key] = [.. recipes.Where(Whole).Take(MaxRecipes)];
        _ = Assert(read.Count <= MaxRecipePrograms);
        return read;
    }

    private static bool Whole(Recipe r) =>
        r is { Inputs: not null, Target: not null, Colors: not null, Fixed.Length: 12 or 13, Attributes: not null } &&
        r.Attributes.Length % 9 == 0 && Assert(r.Colors.Length < 1 << 16);

    // The device checks the header before handing it to the driver
    public static byte[]? Pipelines(uint vendor, uint model)
    {
        if (!Active) return null;
        var path = PipelinePath(vendor, model);
        try
        {
            var data = File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxFile
                ? File.ReadAllBytes(path)
                : null;
            return Assert(data is null || data.Length <= MaxFile) ? data : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Written at once: the process may end right after
    public static void KeepPipelines(uint vendor, uint model, byte[] data)
    {
        if (!Active || !NotNull(data) || data.Length is 0 or > MaxFile) return; // a driver may keep more: not kept then, not wrong
        Write(PipelinePath(vendor, model), data);
    }

    public static string Report()
    {
        if (!Active) return "";
        int read, kept;
        lock (State) (read, kept) = (Loaded, _kept);
        _ = Assert(read >= 0) && Assert(kept <= MaxPorts);
        return $"; shader cache: {read} ports read, {kept} written, pipeline cache {PipelineBytes >> 10} KB at the start";
    }

    private static Dictionary<string, string[]> ReadSettled()
    {
        var path = Path.Join(Folder, HotFile);
        var read = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            if (Assert(Folder.Length > 0) && File.Exists(path) && new FileInfo(path).Length <= MaxFile &&
                JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllBytes(path), Json) is { } file)
                read = new Dictionary<string, string[]>(file.Where(e => e.Value is not null).Take(MaxSettled),
                    StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                      NotSupportedException)
        {
            // started anew
        }

        _ = Assert(read.Count <= MaxSettled);
        return read;
    }

    // Via a side file moved over it: a reader never sees half a file
    private static void Write(string path, byte[] bytes)
    {
        if (!Assert(path.Length > 0) || !NotNull(bytes)) return;
        lock (Writing)
        {
            var part = path + ".part";
            try
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Folder);
                File.WriteAllBytes(part, bytes);
                File.Move(part, path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // kept next time
            }
        }
    }

    private static string PortPath(string sources, IReadOnlySet<string>? hot)
    {
        _ = Assert(Folder.Length > 0) && Assert(sources.Length == 2 * KeyBytes);
        var hotNames = hot is null ? "" : string.Join(";", hot.Order(StringComparer.Ordinal));
        var format = GlslPort.PointSized ? Format + "p" : Format; // Vulkan-only's ports write gl_PointSize
        return Path.Join(Folder, PortsFolder, Build, Hash(format, sources, hotNames) + ".json");
    }

    private static string PipelinePath(uint vendor, uint model)
    {
        _ = Assert(Folder.Length > 0) && Assert(Build.Length > 0);
        return Path.Join(Folder, $"pipelines-{vendor:x4}-{model:x4}.bin");
    }

    private static string Hash(params ReadOnlySpan<string> parts)
    {
        if (!Assert(parts.Length <= MaxParts)) return "";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < Math.Min(parts.Length, MaxParts); i++)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(parts[i]));
            hash.AppendData([0]);
        }

        var key = Convert.ToHexStringLower(hash.GetHashAndReset(), 0, KeyBytes);
        return Assert(key.Length == 2 * KeyBytes) ? key : "";
    }
}
