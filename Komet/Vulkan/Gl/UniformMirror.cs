using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// Unset values are the initializer, else zero, as in GL. Once Laid out, each block's image is written as uniforms
// arrive, so a draw copies it whole. GlTap keeps each watched location's Entry: no hot-path name lookups.
internal sealed class UniformMirror
{
    private const int MaxWords = 4096, MaxNames = 512;

    // Touched: set since the last draw
    public sealed class Entry(string name)
    {
        public string Name { get; } = name;
        public uint[] Held { get; set; } = [];
        public (int Block, GlslPort.Uniform Uniform, (int Columns, int Rows) Shape)[] Places { get; set; } = [];
        public bool Sampler { get; set; }
        public bool Touched { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private GlslPort.Block?[] _layout = [];
    private uint[][] _images = [];

    // How often each uniform changed between draws, over how many draws (while Measuring)
    private readonly Dictionary<string, int> _changed = new(StringComparer.Ordinal);
    private readonly List<Entry> _touched = [];
    public int Draws { get; private set; }

    // Whether the uniforms set between draws are counted (Drawn, Hot), until the hot ones are settled
    public bool Measuring { get; set; }

    public int[] Changes { get; private set; } = [];

    // Block images packed into the uploads (TerrainDraw.Block): a draw that reused the last one packed nothing
    public long Packs { get; set; }
    public int Samplers { get; private set; }

    public (HostBuffer? Uploads, long Epoch, long At, int Changes)[] Uploaded { get; private set; } = [];

    // Made on first ask; null past MaxNames
    public Entry? EntryOf(string name)
    {
        if (!NotNull(name) || !Assert(_entries.Count <= MaxNames)) return null;
        if (_entries.TryGetValue(name, out var known)) return known;
        if (_entries.Count >= MaxNames) return null;
        var made = new Entry(name);
        _entries[name] = made;
        return made;
    }

    public void SetAt(Entry entry, int start, ReadOnlySpan<uint> words)
    {
        if (!Assert(start >= 0 && start + words.Length <= MaxWords) || words.IsEmpty || !NotNull(entry)) return;
        var held = entry.Held;
        if (held.Length < start + words.Length)
        {
            var grown = new uint[start + words.Length];
            held.CopyTo(grown, 0);
            entry.Held = held = grown;
        }
        else if (held.AsSpan(start, words.Length).SequenceEqual(words))
            return; // the engine sets many uniforms again unchanged: a value already held changes nothing

        words.CopyTo(held.AsSpan(start));
        if (Measuring && !entry.Touched && _touched.Count < MaxNames)
        {
            entry.Touched = true;
            _touched.Add(entry);
        }

        if (entry.Sampler) Samplers++;
        foreach (var (block, u, shape) in entry.Places.Bounded(3))
        {
            Write(u, shape, words, _images[block], start);
            Changes[block]++;
        }
    }

    // The uniforms set since the last draw count as changed once
    public void Drawn()
    {
        Draws++;
        foreach (var entry in _touched.Bounded(MaxNames))
        {
            _changed[entry.Name] = _changed.GetValueOrDefault(entry.Name) + 1;
            entry.Touched = false;
        }

        _touched.Clear();
        _ = Assert(Draws > 0);
    }

    // Counting again from nothing: the hot uniforms are settled anew
    public void Remeasure()
    {
        foreach (var entry in _touched.Bounded(MaxNames)) entry.Touched = false;
        _touched.Clear();
        _changed.Clear();
        (Draws, Measuring) = (0, true);
        _ = Assert(_changed.Count == 0) && Assert(_entries.Count <= MaxNames);
    }

    public IReadOnlyList<string> Hot(double share)
    {
        _ = Assert(share is > 0 and <= 1) && Assert(Draws >= 0);
        return [.. _changed.Where(c => c.Value >= share * Draws).OrderByDescending(c => c.Value).Select(c => c.Key)];
    }

    // The program's blocks and samplers: images of the blocks from here on, from what the mirror holds now
    public void Lay(GlslPort.Block vertex, GlslPort.Block fragment, GlslPort.Block hot, IEnumerable<string> samplers)
    {
        if (!NotNull(vertex) || !NotNull(fragment) || !NotNull(hot) || !NotNull(samplers)) return;
        _layout = [vertex, fragment, hot];
        _images = [new uint[vertex.Size / 4], new uint[fragment.Size / 4], new uint[hot.Size / 4]];
        (Changes, Uploaded) = (new int[3], new (HostBuffer?, long, long, int)[3]);
        foreach (var entry in _entries.Values.ToArray().Bounded(MaxNames)) (entry.Places, entry.Sampler) = ([], false);
        foreach (var name in samplers.ToArray().Bounded(MaxNames))
            if (EntryOf(name) is { } sampler)
                sampler.Sampler = true;
        for (var b = 0; b < 3; b++)
            foreach (var u in _layout[b]!.Members.Bounded(MaxNames))
            {
                if (EntryOf(u.Name) is not { } entry) continue;
                entry.Places = [.. entry.Places, (b, u, GlslPort.Shape(u.Type))]; // the shape parsed once, not per write
                var source = entry.Held.Length > 0 ? entry.Held : Initial(u);
                if (source.Length > 0) Write(u, source, _images[b], 0);
            }

        _ = Assert(_entries.Count <= MaxNames);
    }

    // Empty before Lay
    public ReadOnlySpan<uint> Image(int block) =>
        Index(block, 3) && Assert(_images.Length is 0 or 3) && _images.Length == 3 ? _images[block] : [];

    public int Int(string name) =>
        NotNull(name) && _entries.TryGetValue(name, out var entry) && entry.Held.Length > 0 ? (int)entry.Held[0] : 0;

    public uint[] Held(GlslPort.Uniform u)
    {
        if (!NotNull(u) || !Assert(_entries.Count <= MaxNames)) return [];
        return _entries.TryGetValue(u.Name, out var entry) && entry.Held.Length > 0 ? entry.Held : Initial(u);
    }

    // Columns of a matrix 16 bytes apart, as std140 has them
    public void Pack(GlslPort.Block block, Span<byte> into)
    {
        if (!NotNull(block) || !Assert(into.Length >= block.Size)) return;
        var words = MemoryMarshal.Cast<byte, uint>(into[..(block.Size / 4 * 4)]);
        var laid = Array.IndexOf(_layout, block);
        if (laid >= 0 && _images[laid].Length == words.Length)
        {
            _images[laid].CopyTo(words);
            return;
        }

        into[..block.Size].Clear();
        foreach (var u in block.Members.Bounded(MaxNames))
        {
            var source = _entries.TryGetValue(u.Name, out var entry) && entry.Held.Length > 0 ? entry.Held : Initial(u);
            if (source.Length > 0) Write(u, source, words, 0);
        }
    }

    // A uniform's words from word start at std140 places: one block where std140 packs elements gaplessly (vector, vec4
    // array, mat4), a word per element where each has a slot (float array), else word by word
    internal static void Write(GlslPort.Uniform u, ReadOnlySpan<uint> source, Span<uint> words, int start) =>
        Write(u, NotNull(u) ? GlslPort.Shape(u.Type) : default, source, words, start);

    private static void Write(GlslPort.Uniform u, (int Columns, int Rows) shape, ReadOnlySpan<uint> source, Span<uint> words,
        int start)
    {
        var (columns, rows) = shape;
        if (!Assert(u.Offset >= 0 && u.Offset % 4 == 0) || !Assert(start >= 0) || columns * rows == 0) return;
        var element = columns * rows;
        var packed = columns == 1 ? u.Count == 1 || u.Stride == 4 * rows : rows == 4 && (u.Count == 1 || u.Stride == 16 * columns);
        if (packed)
        {
            var (at, n) = (u.Offset / 4 + start, Math.Min(source.Length, u.Count * element - start));
            if (n > 0 && at < words.Length) source[..Math.Min(n, words.Length - at)].CopyTo(words[at..]);
            return;
        }

        var stride = u.Stride / 4;
        if (element != 1 || stride <= 0)
        {
            WriteEach(u, source, words, start); // a float array without a stride too: as it lays out
            return;
        }

        for (var i = 0; i < Math.Min(source.Length, MaxWords); i++)
        {
            var e = start + i;
            var at = u.Offset / 4 + e * stride;
            if (e >= u.Count || at >= words.Length) break;
            words[at] = source[i];
        }
    }

    internal static void WriteEach(GlslPort.Uniform u, ReadOnlySpan<uint> source, Span<uint> words, int start)
    {
        var (columns, rows) = GlslPort.Shape(u.Type);
        if (!Assert(u.Offset >= 0 && u.Offset % 4 == 0) || !Assert(start >= 0) || columns * rows == 0) return;
        var element = columns * rows;
        for (var i = 0; i < Math.Min(source.Length, MaxWords); i++)
        {
            var word = start + i;
            var (e, c, r) = (word / element, word % element / rows, word % rows);
            if (e >= u.Count) break;
            var at = (u.Offset + e * u.Stride + c * (columns > 1 ? 16 : 0) + r * 4) / 4;
            if (at < words.Length) words[at] = source[i];
        }
    }

    // An initializer as OpenGL would hold it: integer types as integers, the rest as floats
    private static uint[] Initial(GlslPort.Uniform u)
    {
        if (!NotNull(u) || u.Default is not { } values || !Assert(values.Length <= 16)) return [];
        var integer = u.Type is "int" or "uint" or "bool" || (u.Type.Length == 5 && u.Type[1..4] == "vec" &&
                                                              u.Type[0] is 'i' or 'u' or 'b');
        return [.. values.Select(v => integer ? (uint)(int)v : BitConverter.SingleToUInt32Bits(v))];
    }
}
