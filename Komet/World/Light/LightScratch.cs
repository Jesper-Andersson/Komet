using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Komet.World;

// Each block light worldgen places (UpdateLightAt) rewalks nearby sources, allocating a Vec3i key per block, a
// LightSourcesAtBlock per node and a QueueOfInt per source: 23 MB/s flying into new terrain. Those become per-thread
// scratch, handed out only inside a scope (prefix and finalizer on UpdateLightAt and SpreadDarkness count depth); a
// nested update gets its own queue.
// Checked at install: only the constructor, UpdateLightAt (clears, then enumerates) and Collect touch VisitedNodes, and
// keys and nodes are read only until the outermost UpdateLightAt returns. The key probe is never stored (Insert stores
// a copy), nodes come back zeroed, queues cleared and used FIFO. A foreign patch found at install leaves the engine
// alone; a later one goes unseen.
internal static class LightScratch
{
    private const string Update = "UpdateLightAt", Collect = "CollectLightValuesForLightSource";
    private const string Darkness = "SpreadDarkness", Recalc = "RecalcBlockLightAtPos", Visited = "VisitedNodes";
    private const int MaxInstructions = 2048, MaxMembers = 512, MaxIl = 1 << 20, MaxDepth = 4, FirstArena = 256;
    private const int MaxNodes = 1 << 15;
    private const int HsvBytes = 45, LookupLength = 18, UpdateBit = 1, CollectBit = 2, DarknessBit = 4, AllBits = 7;

    private static readonly Type NodeMap = typeof(Dictionary<Vec3i, LightSourcesAtBlock>),
        Entries = typeof(Dictionary<Vec3i, LightSourcesAtBlock>.Enumerator),
        Entry = typeof(KeyValuePair<Vec3i, LightSourcesAtBlock>);

    [ThreadStatic] private static Visit? _visit;
    private static int _rewritten; // Harmony reruns a transpiler whenever another mod patches the method
    private static bool _confined;
    private static long _avoided;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _confined && _rewritten == AllBits;
    public static bool StoodDown { get; private set; } // another mod patches a seam, or one is missing

    public static long Avoided => Interlocked.Read(ref _avoided);

    // Keys and nodes outlive their walk in VisitedNodes: reused only when install proved nothing reads them there
    private static bool Reuse => Enabled && _confined && (_rewritten & UpdateBit) != 0;

    // Callees first: a patched method is not inlined, so the caller's replacement calls the rewritten callee
    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        (_rewritten, _confined, StoodDown) = (0, false, false);
        var visited = AccessTools.Field(typeof(ChunkIlluminator), Visited);
        var (update, collect, darkness, recalc) = (Method(Update), Method(Collect), Method(Darkness), Method(Recalc));
        if (!NotNull(harmony) || !NotNull(visited) || !NotNull(update) || !NotNull(collect) || !NotNull(darkness) ||
            !NotNull(recalc)) return;
        MethodBase?[] seams = [update, collect, darkness, recalc];
        var at = EngineShape.FirstForeign(seams, harmony.Id);
        if (at >= 0)
        {
            StoodDown = true;
            if (at < seams.Length)
                logger?.Notification("Komet LightScratch stands down: another mod patches ChunkIlluminator.{0}; " +
                                     "light updates allocate as shipped", seams[at]?.Name);
            return;
        }

        _confined = Confined(visited, collect, update) && OnlyRead(PatchProcessor.GetOriginalInstructions(recalc));
        HarmonyMethod enter = new(Enter), leave = new(Leave);
        _ = NotNull(harmony.Patch(collect, transpiler: new HarmonyMethod(RewriteCollect)));
        _ = NotNull(harmony.Patch(update, enter, transpiler: new HarmonyMethod(CheckUpdate), finalizer: leave));
        _ = NotNull(harmony.Patch(darkness, enter, transpiler: new HarmonyMethod(RewriteDarkness), finalizer: leave));
    }

    // Named with parameters: an overload added by an update would throw AmbiguousMatchException
    internal static MethodInfo? Method(string name)
    {
        Type[] positions = [typeof(int), typeof(int), typeof(int)];
        Type[]? parameters = name switch
        {
            Update or Darkness => [typeof(int), .. positions, typeof(FastSetOfLongs)],
            Collect => [.. positions, .. positions, typeof(int)],
            Recalc => [typeof(Vec3i), typeof(LightSourcesAtBlock)],
            _ => null
        };
        if (!NotNull(parameters)) return null;
        var method = AccessTools.DeclaredMethod(typeof(ChunkIlluminator), name, parameters);
        return NotNull(method) && Assert(method.ReturnType == typeof(void)) ? method : null;
    }

    // Only the constructor, UpdateLightAt and Collect name VisitedNodes; only UpdateLightAt calls Collect (raw IL)
    private static bool Confined(FieldInfo visited, MethodInfo collect, MethodInfo update)
    {
        if (!NotNull(visited) || !NotNull(collect) || !NotNull(update) ||
            !Assert(visited.DeclaringType == typeof(ChunkIlluminator))) return false;
        Type[] types = [typeof(ChunkIlluminator), .. AccessTools.InnerTypes(typeof(ChunkIlluminator))];
        foreach (var type in types.Bounded(MaxMembers))
        {
            MethodBase[] members =
                [.. AccessTools.GetDeclaredMethods(type), .. AccessTools.GetDeclaredConstructors(type)];
            foreach (var member in members.Bounded(MaxMembers))
            {
                var il = member.GetMethodBody()?.GetILAsByteArray();
                if (il == null) continue;
                var engine = member.DeclaringType == typeof(ChunkIlluminator);
                var (isCollect, isUpdate) = (engine && member.MetadataToken == collect.MetadataToken,
                    engine && member.MetadataToken == update.MetadataToken);
                if (Mentions(il, visited.MetadataToken) && !(engine && member is ConstructorInfo { IsStatic: false }) &&
                    !isCollect && !isUpdate) return false;
                if (Mentions(il, collect.MetadataToken) && !isUpdate) return false;
            }
        }

        return true;
    }

    private static bool Mentions(byte[] il, int token)
    {
        if (!NotNull(il) || !Assert(il.Length <= MaxIl)) return true;
        for (var i = 0; i < Math.Min(il.Length - 3, MaxIl); i++)
            if (BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(i, 4)) == token) return true;
        return false;
    }

    // UpdateLightAt must clear VisitedNodes first, then only enumerate it (EntriesOnlyRead); IL returned unchanged
    internal static List<CodeInstruction> CheckUpdate(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~UpdateBit;
        var visited = AccessTools.Field(typeof(ChunkIlluminator), Visited);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(visited) ||
            !Assert(code.Count > 3)) return code;
        if (!code[0].IsLdarg(0) || !code[1].LoadsField(visited) || !IsNodesCall(code[2], "Clear") ||
            code[1].labels.Count > 0 || code[2].labels.Count > 0) return code;
        var reads = Il.Count(code, c => c.LoadsField(visited) || c.LoadsField(visited, true) || c.StoresField(visited));
        var enumerations = 0;
        for (var i = 2; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].opcode == OpCodes.Ldfld && code[i].LoadsField(visited) && Index(i + 1, code.Count) &&
                IsNodesCall(code[i + 1], "GetEnumerator")) enumerations++;
        if (Assert(reads == 2 && enumerations == 1) && EntriesOnlyRead(code)) _rewritten |= UpdateBit;
        return code;
    }

    internal static List<CodeInstruction> RewriteCollect(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~CollectBit;
        var visited = AccessTools.Field(typeof(ChunkIlluminator), Visited);
        var (key, node, insert, queue) = (Helper(nameof(Key)), Helper(nameof(Node)), Helper(nameof(Insert)),
            Helper(nameof(Queue)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(visited) || !NotNull(key) ||
            !NotNull(node) || !NotNull(insert) || !NotNull(queue)) return code;
        List<int> lookups = [];
        int keyLocal = -1, nodeLocal = -1;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (!Creates(code[i], typeof(Vec3i), 3)) continue;
            if (!IsLookup(code, i, visited, out var probe, out var value) ||
                (keyLocal >= 0 && (probe != keyLocal || value != nodeLocal)))
                return code;
            (keyLocal, nodeLocal) = (probe, value);
            lookups.Add(i);
        }

        var nodes = Il.Count(code, c => Creates(c, typeof(LightSourcesAtBlock), 0));
        if (!Assert(lookups.Count == 2 && nodes == 2) || !OnlyInLookups(code, lookups, keyLocal, nodeLocal, visited) ||
            !QueueRewritable(code, out var made)) return code;
        // Insert before Key: the probe must never reach the engine's set_Item
        var rewritten = Assert(Il.Substitute(code, made, queue));
        foreach (var at in lookups.Bounded(2))
            rewritten &= Assert(Il.Substitute(code, at + 16, insert)) && Assert(Il.Substitute(code, at + 13, node)) &&
                         Assert(Il.Substitute(code, at, key));
        if (rewritten) _rewritten |= CollectBit;
        return code;
    }

    internal static List<CodeInstruction> RewriteDarkness(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~DarknessBit;
        var queue = Helper(nameof(Queue));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(queue) ||
            !QueueRewritable(code, out var at)) return code;
        if (Assert(Il.Substitute(code, at, queue))) _rewritten |= DarknessBit;
        return code;
    }

    // One jump-free lookup: newobj Vec3i, stloc key, ldarg.0, ldfld VisitedNodes, ldloc key, ldloca node, callvirt
    // TryGetValue, pop, ldloc node, brtrue end, ldarg.0, ldfld VisitedNodes, ldloc key, newobj LightSourcesAtBlock,
    // dup, stloc node, callvirt set_Item, end: ldloc node
    private static bool IsLookup(List<CodeInstruction> code, int at, FieldInfo visited, out int key, out int node)
    {
        (key, node) = (-1, -1);
        if (!Index(at + LookupLength - 1, code.Count) || !NotNull(visited)) return false;
        for (var step = 1; step < Math.Min(code.Count - at, LookupLength - 1); step++)
            if (code[at + step].labels.Count > 0 || code[at + step].blocks.Count > 0) return false;
        key = Il.Local(code[at + 1], Il.Uses.Store);
        node = Il.Local(code[at + 5], Il.Uses.Address);
        var shape = code[at + 2].IsLdarg(0) && code[at + 3].LoadsField(visited) && IsLoad(code[at + 4], key) &&
                    IsNodesCall(code[at + 6], "TryGetValue") && code[at + 7].opcode == OpCodes.Pop &&
                    IsLoad(code[at + 8], node) && code[at + 9].operand is Label target &&
                    (code[at + 9].opcode == OpCodes.Brtrue_S || code[at + 9].opcode == OpCodes.Brtrue) &&
                    code[at + 10].IsLdarg(0) && code[at + 11].LoadsField(visited) && IsLoad(code[at + 12], key) &&
                    Creates(code[at + 13], typeof(LightSourcesAtBlock), 0) &&
                    code[at + 14].opcode == OpCodes.Dup && Il.Local(code[at + 15], Il.Uses.Store) == node &&
                    IsNodesCall(code[at + 16], "set_Item") && code[at + 17].labels.Contains(target) &&
                    IsLoad(code[at + 17], node);
        return shape && key >= 0 && node >= 0 && Assert(key != node);
    }

    // Key local and VisitedNodes appear only inside the lookups; outside them the node local is only AddHsv's `this`
    private static bool OnlyInLookups(List<CodeInstruction> code, List<int> lookups, int key, int node,
        FieldInfo visited)
    {
        var add = AccessTools.Method(typeof(LightSourcesAtBlock), nameof(LightSourcesAtBlock.AddHsv));
        if (!NotNull(lookups) || !NotNull(visited) || !NotNull(add) ||
            !Assert(key >= 0 && node >= 0 && key != node)) return false;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            var c = code[i];
            var field = c.LoadsField(visited) || c.LoadsField(visited, true) || c.StoresField(visited);
            var local = field ? -1 : Il.Local(c);
            if (!field && local != key && local != node) continue;
            var inside = false;
            foreach (var at in lookups.Bounded(2)) inside |= InLookup(i - at, field, local == key);
            if (inside) continue;
            if (local != node || !IsLoad(c, node)) return false;
            var (use, operand) = Il.Consumer(code, i);
            if (use < 0 || operand != 0 || !code[use].Calls(add)) return false;
        }

        return true;
    }

    // The offsets into a lookup (see IsLookup) at which it names VisitedNodes, the key local or the node local
    private static bool InLookup(int offset, bool field, bool key)
    {
        if (!Assert(offset > -MaxInstructions)) return false;
        if (field) return offset is 3 or 11;
        return key ? offset is 1 or 4 or 12 : offset is 5 or 8 or 15;
    }

    // Each entry's key and value feed only the key's coordinates or RecalcBlockLightAtPos
    private static bool EntriesOnlyRead(List<CodeInstruction> code)
    {
        var recalc = Method(Recalc);
        if (!NotNull(recalc) || !Assert(code.Count <= MaxInstructions)) return false;
        List<int> entries = [];
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (!EntryCall(code[i], Entries, "get_Current")) continue;
            if (!Index(i + 1, code.Count) || !code[i + 1].IsStloc()) return false;
            entries.Add(Il.Local(code[i + 1]));
        }

        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            var c = code[i];
            var (key, value) = (EntryCall(c, Entry, "get_Key"), EntryCall(c, Entry, "get_Value"));
            if (entries.Contains(Il.Local(c)) && !Stored(code, i) && !Unpacked(code, i)) return false;
            if (!key && !value) continue;
            var (use, operand) = Il.Consumer(code, i);
            if (use < 0) return false;
            var coordinate = key && operand == 0 && code[use].opcode == OpCodes.Ldfld &&
                             code[use].operand is FieldInfo { DeclaringType: var t } && t == typeof(Vec3i);
            if (!coordinate && !(code[use].Calls(recalc) && operand == (key ? 1 : 2))) return false;
        }

        return entries.Count > 0;
    }

    private static bool Stored(List<CodeInstruction> code, int at) =>
        Index(at, code.Count) && code[at].IsStloc() && at > 0 && EntryCall(code[at - 1], Entries, "get_Current");

    private static bool Unpacked(List<CodeInstruction> code, int at) =>
        Index(at, code.Count) && Index(at + 1, code.Count) && Il.Local(code[at], Il.Uses.Address) >= 0 &&
        (EntryCall(code[at + 1], Entry, "get_Key") || EntryCall(code[at + 1], Entry, "get_Value"));

    private static bool EntryCall(CodeInstruction code, Type type, string name) =>
        NotNull(code) && NotNull(type) && (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) &&
        code.operand is MethodInfo { Name: var n, DeclaringType: var t } && n == name && t == type;

    // RecalcBlockLightAtPos only reads both: field loads, hsv indexed, neither written nor addressed
    internal static bool OnlyRead(List<CodeInstruction> code)
    {
        var hsvs = AccessTools.Field(typeof(LightSourcesAtBlock), nameof(LightSourcesAtBlock.lightHsvs));
        if (!NotNull(code) || !NotNull(hsvs) || !Assert(code.Count is > 0 and <= MaxInstructions)) return false;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            var c = code[i];
            if (Il.WritesArgument(c, 1) || Il.WritesArgument(c, 2)) return false;
            var array = c.LoadsField(hsvs) || c.LoadsField(hsvs, true);
            if (!array && !c.IsLdarg(1) && !c.IsLdarg(2)) continue;
            var (use, operand) = Il.Consumer(code, i);
            if (use < 0 || operand != 0) return false;
            var read = array ? code[use].opcode == OpCodes.Ldelem_U1 : code[use].opcode == OpCodes.Ldfld;
            if (!read || c.opcode == OpCodes.Ldflda) return false;
        }

        return true;
    }

    // One new QueueOfInt(), its local stored once, never addressed, used only by Enqueue(int), Dequeue() and Count
    private static bool QueueRewritable(List<CodeInstruction> code, out int at)
    {
        at = -1;
        var (enqueue, dequeue) = (AccessTools.Method(typeof(QueueOfInt), nameof(QueueOfInt.Enqueue), [typeof(int)]),
            AccessTools.Method(typeof(QueueOfInt), nameof(QueueOfInt.Dequeue)));
        var count = AccessTools.Field(typeof(QueueOfInt), nameof(QueueOfInt.Count));
        if (!NotNull(enqueue) || !NotNull(dequeue) || !NotNull(count)) return false;
        at = Il.Single(code, c => Creates(c, typeof(QueueOfInt), 0));
        return Index(at + 1, code.Count) && code[at + 1].IsStloc() &&
               Il.Confined(code, at + 1,
                   (c, operand) => operand == 0 && (c.Calls(enqueue) || c.Calls(dequeue) || c.LoadsField(count)));
    }

    private static bool IsNodesCall(CodeInstruction code, string name) =>
        NotNull(code) && Assert(name.Length > 0) && code.opcode == OpCodes.Callvirt && EntryCall(code, NodeMap, name);

    private static bool Creates(CodeInstruction code, Type type, int parameters)
    {
        if (!NotNull(code) || !NotNull(type) || code.opcode != OpCodes.Newobj ||
            code.operand is not ConstructorInfo { DeclaringType: var t } c || t != type) return false;
        var p = c.GetParameters();
        return p.Length == parameters && Array.TrueForAll(p, q => q.ParameterType == typeof(int));
    }

    private static bool IsLoad(CodeInstruction code, int local) => local >= 0 && Il.Local(code, Il.Uses.Load) == local;

    private static MethodInfo Helper(string name)
    {
        var method = AccessTools.Method(typeof(LightScratch), name);
        _ = NotNull(method) && Assert(method.IsStatic);
        return method;
    }

    // Whatever Enabled says, so the depth is real; the outermost scope takes every key and node back
    internal static void Enter(out bool __state)
    {
        var visit = _visit ??= new Visit();
        if (visit.Depth == 0) (visit.KeyCount, visit.NodeCount) = (0, 0);
        visit.Depth++;
        __state = Assert(visit.Depth > 0) && Assert(visit.KeyCount <= MaxNodes);
        if (!__state) visit.Depth--;
    }

    // A void finalizer rethrows the original exception untouched
    internal static void Leave(bool __state)
    {
        if (!__state || _visit is not { } visit || !Assert(visit.Depth > 0)) return;
        if (--visit.Depth > 0) return;
        if (Counting.Hud && visit.Saved > 0) _ = Interlocked.Add(ref _avoided, visit.Saved);
        visit.Saved = 0;
    }

    // Replaces new Vec3i(x, y, z): inside a scope the thread's probe, which TryGetValue only compares
    internal static Vec3i Key(int x, int y, int z)
    {
        if (_visit is not { Depth: > 0 } visit || !Reuse) return new Vec3i(x, y, z);
        var probe = visit.Probe;
        (probe.X, probe.Y, probe.Z) = (x, y, z);
        visit.Saved++;
        return Assert(probe.X == x) ? probe : new Vec3i(x, y, z);
    }

    // Replaces VisitedNodes[key] = node: a copy of the probe goes in, never the probe
    internal static void Insert(Dictionary<Vec3i, LightSourcesAtBlock> nodes, Vec3i key, LightSourcesAtBlock node)
    {
        if (_visit is not { } visit || !ReferenceEquals(key, visit.Probe))
            nodes[key] = node; // null throws there as the engine's call would
        else nodes[Copy(visit, key)] = node;
        _ = Assert(nodes.Count > 0) && NotNull(node);
    }

    private static Vec3i Copy(Visit visit, Vec3i probe)
    {
        var at = visit.KeyCount;
        if (!Assert(ReferenceEquals(probe, visit.Probe)) || visit.Depth <= 0 || !Room(ref visit.Keys, at))
        {
            visit.Saved--; // Key counted the lookup's Vec3i as saved, but the inserted one is new after all
            return new Vec3i(probe.X, probe.Y, probe.Z);
        }

        var key = visit.Keys[at] ??= new Vec3i();
        (key.X, key.Y, key.Z) = (probe.X, probe.Y, probe.Z);
        visit.KeyCount = at + 1;
        return key;
    }

    internal static LightSourcesAtBlock Node()
    {
        if (_visit is not { Depth: > 0 } visit || !Reuse || !Room(ref visit.Nodes, visit.NodeCount))
            return new LightSourcesAtBlock();
        var node = visit.Nodes[visit.NodeCount++] ??= new LightSourcesAtBlock();
        if (node.lightHsvs is { Length: HsvBytes } hsvs) Array.Clear(hsvs);
        else node.lightHsvs = new byte[HsvBytes];
        node.lightCount = 0;
        visit.Saved += 2; // the node and its byte[45]
        return Assert(node.lightHsvs.Length == HsvBytes) ? node : new LightSourcesAtBlock();
    }

    internal static QueueOfInt Queue()
    {
        if (_visit is not { Depth: > 0 and <= MaxDepth } visit || !Enabled) return new QueueOfInt();
        var queue = visit.Queues[visit.Depth - 1] ??= new QueueOfInt();
        queue.Clear();
        visit.Saved += 2; // the queue and its int[27]
        return Assert(queue.Count == 0) ? queue : new QueueOfInt();
    }

    private static bool Room<T>(ref T?[] arena, int at) where T : class
    {
        if (!NotNull(arena) || !Assert(at >= 0)) return false;
        if (at < arena.Length) return true;
        if (at >= MaxNodes) return false;
        Array.Resize(ref arena, Math.Min(Math.Max(arena.Length * 2, FirstArena), MaxNodes));
        return Index(at, arena.Length);
    }

    private sealed class Visit
    {
        public readonly Vec3i Probe = new();
        public readonly QueueOfInt?[] Queues = new QueueOfInt?[MaxDepth];
        public int Depth, KeyCount, NodeCount;
        public Vec3i?[] Keys = new Vec3i?[FirstArena];
        public LightSourcesAtBlock?[] Nodes = new LightSourcesAtBlock?[FirstArena];
        public long Saved;
    }
}
