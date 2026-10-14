using System.Runtime.CompilerServices;

namespace Komet.Shapes;

// Non-recursive pre-order over a ShapeElement tree, the order of the engine's recursive walks; allocation-free.
internal ref struct ElementWalk
{
    public const int MaxDepth = 64, MaxElements = 1 << 14;
    private const int MaxSteps = MaxDepth + 2; // pops through every level down to the end, then the answer
    private Levels _items; // one level past MaxDepth: children that deep fail the walk only once Next would enter them
    private Numbers _next, _owners;
    private int _depth, _count;

    public ElementWalk(ShapeElement[] roots)
    {
        (_items, _next, _owners) = (new Levels(), new Numbers(), new Numbers());
        (_items[0], _owners[0]) = (roots, -1);
        if (!NotNull(roots)) _ = Fail();
    }

    public int Depth { get; private set; } // of the element Next returned, 0 for a root
    public readonly int Parent => _owners[Depth]; // visit index of its parent, -1 for a root
    public bool Failed { get; private set; } // deeper than MaxDepth or more than MaxElements: the walk stopped short

    // The next element, null included; false at the end or once Failed. Children are pushed at once; Skip undoes that.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Next(out ShapeElement? element)
    {
        element = null;
        for (var step = 0; step < MaxSteps; step++)
        {
            var depth = _depth;
            if (depth < 0) return false;
            if (depth >= MaxDepth) return Fail();
            var (items, i) = (_items[depth]!, _next[depth]);
            if (i >= items.Length)
            {
                _depth = depth - 1;
                continue;
            }

            if (_count >= MaxElements) return Fail();
            (element, _next[depth], Depth, _count) = (items[i], i + 1, depth, _count + 1);
            if (element?.Children is not { Length: > 0 } children) return true;
            (_depth, _items[depth + 1], _next[depth + 1], _owners[depth + 1]) = (depth + 1, children, 0, _count - 1);
            return true;
        }

        return !Assert(false); // each step leaves a level, and there are at most MaxDepth + 1 of them
    }

    // The children of the element Next returned last are not visited
    public void Skip()
    {
        if (_depth > Depth) _depth = Depth;
        _ = Assert(_count > 0 || Failed);
    }

    private bool Fail()
    {
        (Failed, _depth) = (true, -1);
        return false;
    }

    [InlineArray(MaxDepth + 1)]
    private struct Levels
    {
        private ShapeElement[]? _level;
    }

    [InlineArray(MaxDepth + 1)]
    private struct Numbers
    {
        private int _number;
    }
}
