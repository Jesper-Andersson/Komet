namespace Komet.Vulkan;

// Draws wait for their ports and pipelines, so they start early: ports as the engine links (Prewarm, world load), then
// an earlier start's pipelines (ShaderCache recipes) before the first draw. New builds or drivers still wait once.
internal sealed partial class SceneRenderer
{
    private const int MaxWarming = MaxScenePrograms, WarmedAFrame = 16;

    private readonly List<int> _warming = [];

    public long PipelinesStarted => _pipelines.Started;

    // Linked under the record: port begun now (a relinked name is another program)
    public void Prewarm(int id, string name)
    {
        if (!Assert(id > 0) || !NotNull(name)) return;
        if (_programs.ContainsKey(id) || _unported.ContainsKey(id) || _porting.ContainsKey(id)) Forget(id);
        if (_porting.Count >= MaxScenePrograms || _warming.Count >= MaxWarming) return;
        var porting = ProgramPorts.Started(id, name, out _);
        if (porting is null) return; // its first draw says why
        _porting[id] = porting;
        _warming.Add(id);
    }

    // Each frame start: done ports taken, known pipelines begun; allocation-free while none is done
    public void Warming()
    {
        if (_warming.Count == 0 || !Assert(_warming.Count <= MaxWarming) || !Assert(_porting.Count <= MaxScenePrograms)) return;
        var (taken, count) = (0, _warming.Count);
        for (var n = 0; n < Math.Min(count, MaxWarming) && taken < WarmedAFrame; n++)
        {
            var i = count - 1 - n; // from the back: what is taken goes without moving what is still to look at
            var id = _warming[i];
            if (_porting.TryGetValue(id, out var porting) && !porting.Done) continue;
            _warming.RemoveAt(i);
            if (!_porting.ContainsKey(id) || _programs.ContainsKey(id)) continue; // forgotten, or drawn already
            _ = ProgramFor(id, out _);
            taken++;
        }
    }

    private static string SourcesOf(int id) => Assert(id > 0) ? PipelineRecipes.SourcesOf(id) : "";

    private void Warm(Drawn program)
    {
        if (!NotNull(program) || program.Sources.Length == 0 || !Assert(program.Port > 0)) return;
        var (id, name, ported, device) = (program.Program.Id, program.Name, program.Program.Ported, _terrain.Device);
        foreach (var recipe in ShaderCache.Recipes(program.Sources).Bounded(MaxPipelines))
        {
            if (recipe.Inputs.StartsWith(PipelineRecipes.Terrain, StringComparison.Ordinal) ||
                PipelineRecipes.Unpacked(recipe) is not var (state, attributes, target)) continue; // the terrain's: its own
            // skip old recipes missing a port input (instanced matrix columns): LayoutOf would refuse and log the
            // pipeline
            if (!Covers(ported, attributes)) continue;
            var key = new PipelineKey(id, program.Port, recipe.Inputs, recipe.Target, state);
            var topology = recipe.Topology;
            _pipelines.Start(key, (name, ported),
                p => TerrainPipeline.Started(device, p, attributes, (state, target, topology)));
        }
    }

    // Each input location of the port has exactly one attribute in the recipe
    private static bool Covers(GlslPort.Ported ported, TerrainPools.Attribute[] attributes)
    {
        if (!NotNull(ported) || !NotNull(attributes)) return false;
        foreach (var input in GlslPort.Columns(ported.Attributes).Bounded(MaxInputs))
            if (attributes.Count(a => a.Location == input.Location) != 1) return false;
        return Assert(attributes.Length <= 4 * MaxInputs);
    }

    // First-time pipeline for a draw: kept for the next start
    private static void Learned(Drawn program, Formats formats, (PipelineKey Key, uint Topology, TerrainPipeline.Target Into) made)
    {
        if (!NotNull(program) || program.Sources.Length == 0 || !NotNull(formats)) return;
        var key = made.Key;
        ShaderCache.Remember(program.Sources, PipelineRecipes.Packed((key.Inputs, key.Target, made.Topology), key.State,
            formats.Attributes, made.Into));
    }
}
