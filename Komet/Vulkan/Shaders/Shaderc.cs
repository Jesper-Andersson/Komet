using System.Runtime.InteropServices;
using System.Text;

namespace Komet.Vulkan;

// Function pointers: without shaderc Komet loads, only Vulkan stays off. Strict compile: leftover loose uniforms must
// fail. Loading guarded by Loading; compiles share the compiler across threads, which shaderc allows.
internal static unsafe class Shaderc
{
    public const int Vertex = 0, Fragment = 1, Compute = 2; // shaderc_shader_kind
    private const int TargetVulkan = 0, Vulkan13 = (1 << 22) | (3 << 12), Success = 0, MaxName = 256;
    private static readonly string[] Libraries =
        ["libshaderc_shared.so.1", "libshaderc_shared.so", "shaderc_shared.dll"];

    private static readonly Lock Loading = new();
    private static IntPtr _library, _compiler;
    private static delegate* unmanaged<IntPtr> _options;
    private static delegate* unmanaged<IntPtr, void> _releaseOptions;
    private static delegate* unmanaged<IntPtr, int, uint, void> _targetEnv;
    private static delegate* unmanaged<IntPtr, byte, void> _relaxed, _autoBind, _autoMap;
    private static delegate* unmanaged<IntPtr, byte*, nuint, int, byte*, byte*, IntPtr, IntPtr> _compile, _preprocess;
    private static delegate* unmanaged<IntPtr, int> _status;
    private static delegate* unmanaged<IntPtr, nuint> _length;
    private static delegate* unmanaged<IntPtr, byte*> _bytes;
    private static delegate* unmanaged<IntPtr, byte*> _error;
    private static delegate* unmanaged<IntPtr, void> _release;

    public static bool Load()
    {
        lock (Loading) return Loaded();
    }

    private static bool Loaded()
    {
        if (_compiler != IntPtr.Zero) return true;
        foreach (var name in Libraries.Bounded(Libraries.Length))
            if (NativeLibrary.TryLoad(name, out _library)) break;
        if (_library == IntPtr.Zero) return false;
        var initialize = (delegate* unmanaged<IntPtr>)Export("shaderc_compiler_initialize");
        _options = (delegate* unmanaged<IntPtr>)Export("shaderc_compile_options_initialize");
        _releaseOptions = (delegate* unmanaged<IntPtr, void>)Export("shaderc_compile_options_release");
        _targetEnv = (delegate* unmanaged<IntPtr, int, uint, void>)Export("shaderc_compile_options_set_target_env");
        _relaxed = (delegate* unmanaged<IntPtr, byte, void>)Export("shaderc_compile_options_set_vulkan_rules_relaxed");
        _autoBind = (delegate* unmanaged<IntPtr, byte, void>)Export("shaderc_compile_options_set_auto_bind_uniforms");
        _autoMap = (delegate* unmanaged<IntPtr, byte, void>)Export("shaderc_compile_options_set_auto_map_locations");
        _compile = (delegate* unmanaged<IntPtr, byte*, nuint, int, byte*, byte*, IntPtr, IntPtr>)Export(
            "shaderc_compile_into_spv");
        _preprocess = (delegate* unmanaged<IntPtr, byte*, nuint, int, byte*, byte*, IntPtr, IntPtr>)Export(
            "shaderc_compile_into_preprocessed_text");
        _status = (delegate* unmanaged<IntPtr, int>)Export("shaderc_result_get_compilation_status");
        _length = (delegate* unmanaged<IntPtr, nuint>)Export("shaderc_result_get_length");
        _bytes = (delegate* unmanaged<IntPtr, byte*>)Export("shaderc_result_get_bytes");
        _error = (delegate* unmanaged<IntPtr, byte*>)Export("shaderc_result_get_error_message");
        _release = (delegate* unmanaged<IntPtr, void>)Export("shaderc_result_release");
        if (initialize == null || _options == null || _compile == null || _status == null || _length == null ||
            _bytes == null || _error == null || _release == null || _releaseOptions == null || _targetEnv == null ||
            _relaxed == null || _autoBind == null || _autoMap == null || _preprocess == null) return false;
        _compiler = initialize();
        return Assert(_compiler != IntPtr.Zero);
    }

    public static uint[]? Compile(string source, int kind, string name, out string error, bool relaxed = true)
    {
        var result = Run(source, kind, name, relaxed, false, out error);
        if (result == IntPtr.Zero || !Assert(_release != null)) return null;
        try
        {
            if (_status(result) != Success)
            {
                error = Marshal.PtrToStringUTF8((IntPtr)_error(result)) ?? "compilation failed";
                return null;
            }

            var length = (int)_length(result);
            if (!Assert(length % 4 == 0 && length > 20)) return null;
            var words = new uint[length / 4];
            new ReadOnlySpan<byte>(_bytes(result), length).CopyTo(MemoryMarshal.AsBytes(words.AsSpan()));
            return Assert(words[0] == Spirv.Magic) ? words : null;
        }
        finally
        {
            _release(result);
        }
    }

    // Macros expanded, conditionals resolved, comments gone, lines kept; null on an error
    public static string? Preprocess(string source, int kind, string name, out string error)
    {
        var result = Run(source, kind, name, false, true, out error);
        if (result == IntPtr.Zero || !Assert(_status != null)) return null;
        try
        {
            if (_status(result) == Success)
                return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(_bytes(result), (int)_length(result)));
            error = Marshal.PtrToStringUTF8((IntPtr)_error(result)) ?? "preprocessing failed";
            return null;
        }
        finally
        {
            _release(result);
        }
    }

    private static IntPtr Run(string source, int kind, string name, bool relaxed, bool preprocess, out string error)
    {
        error = "";
        if (!NotNull(source) || !Assert(kind is Vertex or Fragment or Compute) || !Load())
        {
            error = "no shaderc";
            return IntPtr.Zero;
        }

        var options = _options();
        _targetEnv(options, TargetVulkan, Vulkan13);
        var loose = (byte)(relaxed ? 1 : 0);
        _relaxed(options, loose);
        _autoBind(options, loose);
        _autoMap(options, loose);
        var text = Encoding.UTF8.GetBytes(source);
        var clipped = NotNull(name) && Assert(name.Length > 0) && name.Length < MaxName ? name : "shader";
        var (file, entry) = (Encoding.UTF8.GetBytes(clipped + "\0"), "main\0"u8.ToArray());
        IntPtr result;
        fixed (byte* s = text, f = file, e = entry)
            result = (preprocess ? _preprocess : _compile)(_compiler, s, (nuint)text.Length, kind, f, e, options);
        _releaseOptions(options);
        return result;
    }

    private static IntPtr Export(string name) =>
        Assert(_library != IntPtr.Zero) && Assert(name.StartsWith("shaderc_", StringComparison.Ordinal)) &&
        NativeLibrary.TryGetExport(_library, name, out var address)
            ? address
            : IntPtr.Zero;
}
