using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Komet;

// Checks for must-never-happen conditions: logged once per call site, returned for explicit recovery; one branch.
internal static class Contracts
{
    private static readonly HashSet<(string File, string Member, int Line)> Reported = [];
    private static readonly Lock Gate = new();
    private static ILogger? _logger;

    // null detaches: failures go to standard error
    public static void Attach(ILogger? logger)
    {
        lock (Gate)
        {
            Reported.Clear(); // otherwise a failure from the previous world silences this one
        }

        _logger = logger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Assert(bool condition, [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (condition) return true;
        Report(expression, file, member, line);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NotNull<T>([NotNullWhen(true)] T? value,
        [CallerArgumentExpression(nameof(value))] string expression = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "") where T : class
    {
        if (value is not null) return true;
        Report(expression + " is null", file, member, line);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Finite(double value, [CallerArgumentExpression(nameof(value))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (double.IsFinite(value)) return true;
        Report(expression + " is not finite", file, member, line);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Index(int value, int count, [CallerArgumentExpression(nameof(value))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if ((uint)value < (uint)count) return true;
        Report(expression + " out of range", file, member, line);
        return false;
    }

    // At most max items, reported when more. Spans don't allocate; the IEnumerable iterator stays out of frame code.
    public static ReadOnlySpan<T> Bounded<T>(this List<T> items, int max,
        [CallerArgumentExpression(nameof(items))] string expression = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (items.Count > max) Report($"{expression} exceeds {max}", file, member, line);
        return CollectionsMarshal.AsSpan(items)[..Math.Min(items.Count, max)];
    }

    public static ReadOnlySpan<T> Bounded<T>(this T[] items, int max,
        [CallerArgumentExpression(nameof(items))] string expression = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (items.Length > max) Report($"{expression} exceeds {max}", file, member, line);
        return items.AsSpan(0, Math.Min(items.Length, max));
    }

    public static ReadOnlySpan<T> Bounded<T>(this ReadOnlySpan<T> items, int max,
        [CallerArgumentExpression(nameof(items))] string expression = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (items.Length > max) Report($"{expression} exceeds {max}", file, member, line);
        return items[..Math.Min(items.Length, max)];
    }

    public static IEnumerable<T> Bounded<T>(this IEnumerable<T> items, int max,
        [CallerArgumentExpression(nameof(items))] string expression = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        using var e = items.GetEnumerator();
        for (var i = 0; i < max; i++)
        {
            if (!e.MoveNext()) yield break;
            yield return e.Current;
        }

        if (e.MoveNext()) Report($"{expression} exceeds {max}", file, member, line);
    }

    // A dictionary's values with neither the IEnumerable iterator nor its boxed enumerator: foreach takes the struct
    public static BoundedValues<TKey, TValue> Bounded<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection items,
        int max, [CallerArgumentExpression(nameof(items))] string expression = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "") where TKey : notnull
    {
        if (items.Count > max) Report($"{expression} exceeds {max}", file, member, line);
        return new BoundedValues<TKey, TValue>(items, Math.Min(items.Count, max));
    }

    public readonly struct BoundedValues<TKey, TValue>(Dictionary<TKey, TValue>.ValueCollection items, int max)
        where TKey : notnull
    {
        public Enumerator GetEnumerator() => new(items.GetEnumerator(), max);

        public struct Enumerator(Dictionary<TKey, TValue>.ValueCollection.Enumerator inner, int left)
        {
            private Dictionary<TKey, TValue>.ValueCollection.Enumerator _inner = inner;
            private int _left = left;

            public TValue Current => _inner.Current;

            public bool MoveNext()
            {
                if (_left <= 0) return false;
                _left--;
                return _inner.MoveNext();
            }
        }
    }

    private static void Report(string expression, string file, string member, int line)
    {
        lock (Gate)
        {
            if (!Reported.Add((file, member, line))) return;
        }

        var name = Path.GetFileName(file);
        if (_logger is null)
            Console.Error.WriteLine($"Komet: assertion failed in {name}, {member} (line {line}): {expression}");
        else _logger.Error("Komet: assertion failed in {0}, {1} (line {2}): {3}", name, member, line, expression);
    }
}
