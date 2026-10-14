namespace Komet.Vulkan;

// Pipelines by key, built on a builder (TerrainPipeline.Started); meanwhile the draw is missing, or with wait (draw
// would be lost, as under Vulkan-only) blocks. Finished builds count from the next frame's first ask (Poll): no frame
// switches mid-way between OpenGL and Vulkan draws of a pipeline (depth tests could differ). Forgotten ones go on their
// frames' fences (VulkanFrame.Done). Engine's thread only; builders hand over through Build.
internal sealed class PipelineSet<TKey>(VulkanDevice device, VulkanFrame frame, ILogger? logger, int max) : IDisposable
    where TKey : notnull
{
    private const int MaxRetired = 1 << 16;

    private readonly Dictionary<TKey, TerrainPipeline?> _made = [];
    private readonly Queue<TKey> _order = []; // _made's keys as they came, forgotten ones too until Evict meets them
    private readonly Dictionary<TKey, (Build<TerrainPipeline> Build, string Name)> _building = [];
    private readonly Dictionary<TKey, System.Func<GlslPort.Ported, Build<TerrainPipeline>>> _recipes = [];
    private readonly List<(TerrainPipeline Pipeline, long Frame)> _retired = [];
    private long _polled = -1;

    public int Count => _made.Count;
    public int Building => _building.Count;
    public int Retired => _retired.Count;

    public long Waits { get; private set; }

    // No recipe lambda: one per draw was garbage on every pool draw
    public bool TryMade(TKey key, out TerrainPipeline? made) => _made.TryGetValue(key, out made) && Assert(_made.Count <= max);

    // null: cannot be made, or waiting. start: built for a port, kept so Warm can redo it for another
    public TerrainPipeline? Get(TKey key, (string Name, GlslPort.Ported Ported) program,
        System.Func<GlslPort.Ported, Build<TerrainPipeline>> start, bool wait, out bool waiting)
    {
        waiting = false;
        if (_made.TryGetValue(key, out var made)) return made;
        if (!NotNull(start) || !Assert(_made.Count <= max)) return null;
        Poll();
        if (_made.TryGetValue(key, out made)) return made;
        if (!_building.TryGetValue(key, out var building))
        {
            _building[key] = building = (start(program.Ported), program.Name);
            if (_recipes.Count < 2 * max) _recipes[key] = start;
            if (Builds.Waited && building.Build.Done) return Taken(key, building); // made in place: no draw waited for it
        }

        if (wait && building.Build.Awaited(Hitches.Kind.Pipeline)) return Taken(key, building);

        (waiting, Waits) = (true, Waits + 1);
        return null;
    }

    // Made, refused or building
    public bool Knows(TKey key) => (_made.ContainsKey(key) || _building.ContainsKey(key)) && Assert(_made.Count <= max);

    public long Started { get; private set; }

    // Vulkan-only: begins a pipeline an earlier start used before any draw asks; a full set starts none.
    public void Start(TKey key, (string Name, GlslPort.Ported Ported) program,
        System.Func<GlslPort.Ported, Build<TerrainPipeline>> start)
    {
        if (!NotNull(start) || !Assert(_building.Count <= max) || Knows(key) || _building.Count + _made.Count >= max) return;
        var building = _building[key] = (start(program.Ported), program.Name);
        if (_recipes.Count < 2 * max) _recipes[key] = start;
        Started++;
        if (Builds.Waited && building.Build.Done) _ = Taken(key, building);
    }

    // Builds done by now count as made, all at once, at the frame's first ask
    private void Poll()
    {
        if (_polled == frame.Number || !Assert(_building.Count <= max) || !Assert(frame.Number > _polled)) return;
        _polled = frame.Number;
        Collect();
        if (_building.Count == 0) return;
        foreach (var (key, building) in _building.Where(b => b.Value.Build.Done).ToArray().Bounded(max))
            _ = Taken(key, building);
        if (_building.Count == 0) device.KeepCache(); // what was asked for is made: on disk before a crash can lose it
    }

    private TerrainPipeline? Taken(TKey key, (Build<TerrainPipeline> Build, string Name) building)
    {
        _ = _building.Remove(key) && Assert(building.Build.Done) && Assert(!_made.ContainsKey(key));
        var made = building.Build.Take(out var why);
        if (made is null) logger?.Warning("Komet: no Vulkan pipeline for {0}: {1}", building.Name, why);
        if (_made.Count >= max) Evict();
        if (_order.Count >= 4 * max) Compact();
        _made[key] = made;
        _order.Enqueue(key);
        return made;
    }

    // Full: the oldest goes, rather than unkept keys rebuilding every frame
    private void Evict()
    {
        if (!Assert(max > 0) || !Assert(_order.Count >= _made.Count)) return;
        var count = _order.Count;
        for (var i = 0; i < Math.Min(count, MaxRetired); i++)
        {
            if (!_made.Remove(_order.Dequeue(), out var made)) continue; // forgotten
            if (made is not null) _retired.Add((made, frame.Number)); // the open frame may have recorded it
            return;
        }
    }

    // Drops forgotten keys from the order; made ones stay, oldest first
    private void Compact()
    {
        var kept = _order.Where(_made.ContainsKey).Distinct().ToArray();
        _order.Clear();
        foreach (var key in kept.Bounded(MaxRetired)) _order.Enqueue(key);
        _ = Assert(_order.Count <= _made.Count) && Assert(_order.Count <= MaxRetired);
    }

    // Rebuilds keys matching which for another port under again's keys, so the program switches with all made. Returns
    // the keys started.
    public TKey[] Warm(System.Func<TKey, bool> which, System.Func<TKey, TKey> again,
        (string Name, GlslPort.Ported Ported) program)
    {
        if (!NotNull(which) || !NotNull(again) || !Assert(_recipes.Count <= 2 * max)) return [];
        var started = new List<TKey>();
        foreach (var (key, recipe) in _recipes.Where(r => which(r.Key)).ToArray().Bounded(2 * max))
        {
            var next = again(key);
            if (_made.ContainsKey(next) || _building.ContainsKey(next) || !Assert(!which(next))) continue;
            var building = _building[next] = (recipe(program.Ported), program.Name);
            if (_recipes.Count < 2 * max) _recipes[next] = recipe;
            if (Builds.Waited && building.Build.Done) _ = Taken(next, building); // made in place
            started.Add(next);
        }

        return [.. started];
    }

    // All made (or refused), as this frame counts them
    public bool Made(TKey[] keys)
    {
        if (!NotNull(keys)) return false;
        Poll();
        foreach (var key in keys.Bounded(2 * max))
            if (_building.ContainsKey(key))
                return false;
        return Assert(_building.Count <= max);
    }

    // Pipelines go once no frame in flight may use them; builds dropped
    public void Forget(System.Func<TKey, bool> which)
    {
        if (!NotNull(which) || !Assert(_made.Count <= max)) return;
        foreach (var (key, made) in _made.Where(p => which(p.Key)).ToArray().Bounded(max))
        {
            _ = _made.Remove(key);
            if (made is not null) _retired.Add((made, frame.Number));
        }

        foreach (var (key, (build, _)) in _building.Where(p => which(p.Key)).ToArray().Bounded(max))
        {
            build.Drop();
            _ = _building.Remove(key);
        }

        foreach (var key in _recipes.Keys.Where(which).ToArray().Bounded(2 * max)) _ = _recipes.Remove(key);
        Collect();
    }

    private void Collect()
    {
        var done = frame.Done;
        var kept = 0;
        var count = Math.Min(_retired.Count, MaxRetired);
        for (var i = 0; i < Math.Min(count, MaxRetired); i++)
            if (_retired[i].Frame <= done) _retired[i].Pipeline.Dispose();
            else _retired[kept++] = _retired[i];
        _retired.RemoveRange(kept, count - kept);
        _ = Assert(_retired.Count <= MaxRetired) && Assert(kept <= count);
    }

    // After GPU idle; running builds destroy what they make
    public void Dispose()
    {
        _ = Assert(_made.Count <= max) && Assert(_retired.Count <= MaxRetired);
        foreach (var (build, _) in _building.Values.ToArray().Bounded(max)) build.Drop();
        foreach (var made in _made.Values.ToArray().Bounded(max)) made?.Dispose();
        foreach (var (retired, _) in _retired.Bounded(MaxRetired)) retired.Dispose();
        _building.Clear();
        _made.Clear();
        _order.Clear();
        _recipes.Clear();
        _retired.Clear();
    }
}
