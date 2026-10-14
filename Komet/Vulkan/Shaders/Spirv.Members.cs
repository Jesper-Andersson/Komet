using System.Runtime.InteropServices;
using System.Text;

namespace Komet.Vulkan;

// Offsets and strides as compiled, so a filler writes where the shader reads
internal static partial class Spirv
{
    private const int OpName = 5, OpMemberName = 6, OpMemberDecorate = 72;
    private const int DecorationOffset = 35, DecorationArrayStride = 6, MaxMembers = 1024;

    public readonly record struct Member(string Name, int Offset, int Stride);

    private sealed class Layout
    {
        public Dictionary<int, string> Names { get; } = [];
        public Dictionary<(int, int), string> MemberNames { get; } = [];
        public Dictionary<(int, int), int> Offsets { get; } = [];
        public Dictionary<int, int> Strides { get; } = [];
        public Dictionary<int, uint[]> Structs { get; } = [];
    }

    public static Member[] Members(ReadOnlySpan<uint> words, string block)
    {
        if (words.Length < Header || words[0] != Magic || !Assert(block.Length > 0)) return [];
        var layout = new Layout();
        var at = Header;
        for (var step = 0; step < MaxWords && at < words.Length; step++)
        {
            var (count, op) = ((int)(words[at] >> 16), (int)(words[at] & 0xFFFF));
            if (count == 0 || at + count > words.Length) break;
            Note(words.Slice(at, count), op, layout);
            at += count;
        }

        var id = -1;
        foreach (var (key, name) in layout.Names.Bounded(MaxIds))
            if (name == block && layout.Structs.ContainsKey(key))
                id = key;
        if (!layout.Structs.TryGetValue(id, out var types)) return [];
        var members = new Member[Math.Min(types.Length, MaxMembers)];
        for (var i = 0; i < Math.Min(members.Length, MaxMembers); i++)
            members[i] = new Member(layout.MemberNames.GetValueOrDefault((id, i), ""),
                layout.Offsets.GetValueOrDefault((id, i), -1), layout.Strides.GetValueOrDefault((int)types[i]));
        return members;
    }

    private static void Note(ReadOnlySpan<uint> op, int code, Layout layout)
    {
        if (!Assert(op.Length > 0) || !NotNull(layout)) return;
        switch (code)
        {
            case OpName when op.Length >= 3:
                layout.Names[(int)op[1]] = Text(op[2..]);
                break;
            case OpMemberName when op.Length >= 4:
                layout.MemberNames[((int)op[1], (int)op[2])] = Text(op[3..]);
                break;
            case OpMemberDecorate when op.Length >= 5 && op[3] == DecorationOffset:
                layout.Offsets[((int)op[1], (int)op[2])] = (int)op[4];
                break;
            case OpDecorate when op.Length >= 4 && op[2] == DecorationArrayStride:
                layout.Strides[(int)op[1]] = (int)op[3];
                break;
            case OpTypeStruct when op.Length >= 2:
                layout.Structs[(int)op[1]] = op[2..].ToArray();
                break;
        }
    }

    // UTF-8, zero-terminated, four bytes a word
    private static string Text(ReadOnlySpan<uint> words)
    {
        var bytes = MemoryMarshal.AsBytes(words);
        var end = bytes.IndexOf((byte)0);
        if (!Assert(end >= 0) || !Assert(bytes.Length > 0)) return "";
        return Encoding.UTF8.GetString(bytes[..end]);
    }
}
