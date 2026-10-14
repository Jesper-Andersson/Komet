using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Terrain VS hand ~56 values a vertex to the FS, and the export is most of the vertex cost (VD 1024 probe: dropping
// light outputs saved 1.35 ms, shadow coordinates 1.0 ms; skipping only their arithmetic nothing). Outputs derivable in
// the FS are computed there: from flat render flags (normal, glow, normal brightness, SSAO normal), uniforms (fog
// colour), or linear in the interpolated world position (camera position; shadow coordinates, w still from vertices):
// the same numbers to the last rounding bit. Each only where the VS sets it exactly as written here and nowhere else,
// so other shader packs or engine versions keep their outputs.
internal static partial class LeanVaryings
{
    private static readonly string[] Passes = ["chunkopaque", "chunktopsoil", "chunktransparent"];
    private const string Marker = "kometLean";

    private const string Flags = "flat in int renderFlags;", World = "in vec4 worldPos;";
    private const int MaxRecipes = 16, MaxShadows = 4;

    // FS-computed varying: the VS's only statements setting it, the FS's own (in order: later read earlier), needed
    // uniforms, texts one of the shaders must contain
    private sealed record Recipe(string Name, string Type, string[] Statements, string Fs, string[] Uniforms, string[] Needs);

    private static readonly Recipe[] Recipes =
    [
        new("normal", "vec3", ["normal = unpackNormal(renderFlags);"], "normal = unpackNormal(renderFlags);", [], [Flags]),
        new("glowLevel", "float", ["float bGlow = glowLevel = (renderFlags & GlowLevelBitMask) / 256.0;"],
            "glowLevel = (renderFlags & GlowLevelBitMask) / 256.0;", [], [Flags]),
        new("nb", "float", ["nb = max(max(intensity, 0.5 + 0.5 * dot(normal, lightPosition)), normal.y * 0.95);"],
            """
            #if SHADOWQUALITY > 0
            	float intensity = 0.34 + (1 - shadowIntensity)/8.0;
            #else
            	float intensity = 0.45;
            #endif
            	nb = max(max(intensity, 0.5 + 0.5 * dot(normal, lightPosition)), normal.y * 0.95);
            """, ["vec3 lightPosition", "float shadowIntensity"], [Flags]),
        new("gnormal", "vec4", ["gnormal = modelViewMatrix * vec4(normal.xyz, 0);", "gnormal.w = isLeaves ? 1 : 0;"],
            """
            #if SSAOLEVEL > 0
            	gnormal = modelViewMatrix * vec4(normal.xyz, 0);
            	gnormal.w = (renderFlags & WindModeBitMask) > 0 ? 1 : 0;
            #endif
            """, ["mat4 modelViewMatrix"], [Flags, "bool isLeaves = ((renderFlags & WindModeBitMask) > 0);"]),
        new("gnormal", "vec4", ["gnormal = modelViewMatrix * vec4(normal, 0);", "gnormal.w=0;"],
            """
            #if SSAOLEVEL > 0
            	gnormal = modelViewMatrix * vec4(normal, 0);
            	gnormal.w=0;
            #endif
            """, ["mat4 modelViewMatrix"], [Flags]),
        new("camPos", "vec4", ["camPos = modelViewMatrix * worldPos;"], "camPos = modelViewMatrix * worldPos;",
            ["mat4 modelViewMatrix"], [World]),
        new("fragPosition", "vec4", ["fragPosition = cameraPos;"],
            """
            #if SSAOLEVEL > 0
            	fragPosition = modelViewMatrix * worldPos;
            #endif
            """, ["mat4 modelViewMatrix"], [World, "vec4 cameraPos = modelViewMatrix * worldPos;"]),
        new("rgbaFog", "vec4", ["rgbaFog = rgbaFogIn;"], "rgbaFog = rgbaFogIn;", ["vec4 rgbaFogIn"], [])
    ];

    // Shadow coordinates: xyz linear in world position, w from vertices; VS/FS guards (distant FS part needs its
    // define)
    private static readonly (string Name, string Weight, string Matrix, string VsGuard, string FsGuard)[] Shadows =
    [
        ("shadowCoordsFar", "kometFarW", "toShadowMapSpaceMatrixFar", "SHADOWQUALITY > 0", "SHADOWQUALITY > 0"),
        ("shadowCoordsNear", "kometNearW", "toShadowMapSpaceMatrixNear", "SHADOWQUALITY > 1", "SHADOWQUALITY > 1"),
        ("shadowCoordsDistant", "kometDistantW", DistantShadows.DistantMatrix, "SHADOWQUALITY > 0",
            $"SHADOWQUALITY > 0 && defined({DistantShadows.DistantDefine})")
    ];

    public static bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            ShaderEdits.Reload();
            _ = Assert(field == value);
        }
    } = true;

    public static void Install(Harmony harmony, ICoreClientAPI? api)
    {
        _ = Assert(Recipes.Length <= MaxRecipes) && Assert(Shadows.Length <= MaxShadows);
        ShaderEdits.Install(harmony, api);
    }

    internal static void Edit(ShaderProgram program)
    {
        if (!Enabled || !NotNull(program) || Array.IndexOf(Passes, program.PassName) < 0 ||
            program.VertexShader is not { Code: { } vs } vertex || program.FragmentShader is not { Code: { } fs } fragment ||
            vs.Contains(Marker, StringComparison.Ordinal)) return;
        var (body, end, uniforms, dropped) = (new StringBuilder(), new StringBuilder(), new List<string>(), 0);
        foreach (var recipe in Recipes.Bounded(MaxRecipes))
        {
            if (!Applies(recipe, vs, fs)) continue;
            Declared(ref vs, ref fs, recipe.Type, recipe.Name);
            _ = body.Append("\n\t").Append(recipe.Fs).Append('\n');
            uniforms.AddRange(recipe.Uniforms);
            dropped++;
        }

        foreach (var (name, weight, matrix, vsGuard, fsGuard) in Shadows.Bounded(MaxShadows))
        {
            if (!Varying(vs, fs, "vec4", name)) continue;
            Declared(ref vs, ref fs, "vec4", name, $"out float {weight}; ", $"in float {weight}; uniform highp mat4 {matrix}; ");
            _ = end.Append(CultureInfo.InvariantCulture, $"\n#if {vsGuard}\n\t{weight} = {name}.w;\n#endif\n");
            _ = body.Append(CultureInfo.InvariantCulture, $"\n#if {fsGuard}\n\t{name} = vec4(({matrix} * worldPos).xyz, {weight});\n#endif\n");
            dropped++;
        }

        if (Colormapped(ref vs, ref fs, uniforms))
        {
            _ = body.Append(ClimateFs);
            dropped++;
        }

        if (Varying(vs, fs, "vec4", "worldPos") && Count(@"\bworldPos\.w\s*=", vs) == 0)
        {
            // w stays 1: every warp moves x, y and z only
            Declared(ref vs, ref fs, "vec4", "worldPos", "out vec3 kometWorldPos; ", "in vec3 kometWorldPos; ");
            _ = end.Append("\n\tkometWorldPos = worldPos.xyz;\n");
            _ = body.Insert(0, "\n\tworldPos = vec4(kometWorldPos, 1.0);\n");
            dropped++;
        }

        if (dropped == 0 || !Main(vs, out var vsOpen, last: true) || !Main(fs, out var fsOpen, last: false)) return;
        var declared = string.Concat(uniforms.Distinct().Where(u => !Has(fs, u)).Select(u => $"uniform {u};\n"));
        vertex.Code = vs[..vsOpen] + end + vs[vsOpen..] + $"\n// {Marker}\n";
        var main = fs.IndexOf("void main", StringComparison.Ordinal);
        fragment.Code = fs[..main] + declared + fs[main..fsOpen] + body + fs[fsOpen..];
        _ = Assert(main > 0) && Assert(dropped <= MaxRecipes + MaxShadows + 2);
    }

    // Climate map coordinates and temperature follow from the face's colour map data (calcColorMapUvs): one flat int
    // instead of three values, recomputed in the FS
    private const string Climate = "void calcColorMapUvs(int colormapData, vec4 worldPos, float sunlightLevel, bool isLeaves) {";
    private const string Heretemp = "heretemp = tempRel + seasonTemperature;";
    private const string ClimateFs = """

        	{
        		int climateMapIndex = ((kometColormap >> 8) & 0xf) - 1;
        		float tempRel = clamp(((kometColormap >> 16) & 0xff) / 255.0, 0.001, 0.999);
        		float rainfallRel = clamp(((kometColormap >> 24) & 0xff) / 255.0, 0.001, 0.999);
        		heretemp = tempRel + seasonTemperature;
        		if (climateMapIndex >= 0) {
        			vec4 rect = colorMapRects[climateMapIndex];
        			climateColorMapUv = vec2(rect.x + rect.z * tempRel, rect.y + rect.w * rainfallRel);
        		} else {
        			climateColorMapUv = vec2(-1,-1);
        		}
        	}

        """;

    private static bool Colormapped(ref string vs, ref string fs, List<string> uniforms)
    {
        if (!Varying(vs, fs, "vec2", "climateColorMapUv") || !Varying(vs, fs, "float", "heretemp") ||
            Count(Assigned("climateColorMapUv"), vs) != 2 || Count(Assigned("heretemp"), vs) != 1 ||
            Count(Regex.Escape(Climate), vs) != 1 || !vs.Contains(Heretemp, StringComparison.Ordinal) ||
            Count(@"climateColorMapUv\s*=\s*vec2\(\s*rect\.x \+ rect\.z \* tempRel,\s*rect\.y \+ rect\.w \* rainfallRel\s*\);", vs) != 1 ||
            Count(@"climateColorMapUv = vec2\(-1,-1\);", vs) != 1 || Count(@"\bkometColormap\b", vs + fs) != 0) return false;
        Declared(ref vs, ref fs, "vec2", "climateColorMapUv", "flat out int kometColormap; ", "flat in int kometColormap; ");
        Declared(ref vs, ref fs, "float", "heretemp");
        vs = vs.Replace(Climate, Climate + "\n\tkometColormap = colormapData;", StringComparison.Ordinal);
        uniforms.AddRange(["vec4 colorMapRects[40]", "float seasonTemperature"]);
        return Assert(vs.Contains("kometColormap = colormapData;", StringComparison.Ordinal));
    }

    private static bool Applies(Recipe recipe, string vs, string fs) =>
        Assert(recipe.Statements.Length > 0) && NotNull(fs) && Varying(vs, fs, recipe.Type, recipe.Name) &&
        Count(Assigned(recipe.Name), vs) == recipe.Statements.Length &&
        Array.TrueForAll(recipe.Statements, s => vs.Contains(s, StringComparison.Ordinal)) &&
        Array.TrueForAll(recipe.Needs, n => vs.Contains(n, StringComparison.Ordinal) || fs.Contains(n, StringComparison.Ordinal));

    // Declared once in both shaders, never set by the FS
    private static bool Varying(string vs, string fs, string type, string name) =>
        Assert(vs.Length > 0) && Assert(fs.Length > 0) && Count(Decl("out", type, name), vs) == 1 &&
        Count(Decl("in", type, name), fs) == 1 && Count(Assigned(name), fs) == 0;

    // Both declarations (flat or not) made plain globals, plus what follows each
    private static void Declared(ref string vs, ref string fs, string type, string name, string vsAfter = "", string fsAfter = "")
    {
        if (!Assert(vs.Length > 0 && fs.Length > 0) || !Assert(name.Length > 0)) return;
        vs = Regex.Replace(vs, Decl("out", type, name), m => Assert(m.Success) ? $"{m.Groups[1].Value}{type} {name}; {vsAfter}" : m.Value,
            RegexOptions.Multiline, TimeSpan.FromSeconds(2));
        fs = Regex.Replace(fs, Decl("in", type, name), m => Assert(m.Success) ? $"{m.Groups[1].Value}{type} {name}; {fsAfter}" : m.Value,
            RegexOptions.Multiline, TimeSpan.FromSeconds(2));
    }

    private static string Decl(string direction, string type, string name) =>
        Assert(direction is "in" or "out") && Assert(type.Length > 0)
            ? $@"^([ \t]*)(?:flat[ \t]+)?{direction}[ \t]+{type}[ \t]+{name}[ \t]*;"
            : "$^";

    // Not a same-named local declared with a value (fogandlight.fsh's float heretemp = -10)
    private static string Assigned(string name) =>
        Assert(name.Length > 0) ? $@"(?<!\b(?:float|int|vec[234]|mat4|bool)\s+)\b{name}(?:\.\w+)?\s*=(?!=)" : "$^";

    private static int Count(string pattern, string code) =>
        Assert(pattern.Length > 0) && NotNull(code) ? Regex.Count(code, pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(2)) : -1;

    private static bool Has(string code, string declaration) =>
        Assert(declaration.Length > 0) && Regex.IsMatch(code, $@"\b{Regex.Escape(declaration).Replace(@"\ ", @"\s+", StringComparison.Ordinal)}\b",
            RegexOptions.None, TimeSpan.FromSeconds(2)) || code.Contains("uniform " + declaration, StringComparison.Ordinal);

    // main's body start (brace + 1), or for the VS its closing brace (main is last)
    private static bool Main(string code, out int at, bool last)
    {
        var main = code.IndexOf("void main", StringComparison.Ordinal);
        at = -1;
        if (main < 0) return false;
        at = last ? code.LastIndexOf('}') : code.IndexOf('{', main) + 1;
        return at > main && Assert(at <= code.Length);
    }
}
