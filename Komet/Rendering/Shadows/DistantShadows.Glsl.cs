using System.Text.RegularExpressions;

namespace Komet.Rendering;

// FS part compiles only with KOMET_DISTANT (an unwritten input fails to link). Uniforms carry a precision qualifier to
// stay out of the engine's uniform scan, where a sampler would take a texture unit and shift later ones.
internal static partial class DistantShadows
{
    internal const string VertexFile = "shadowcoords.vsh", FragmentFile = "fogandlight.fsh", Marker = "kometDistant";
    internal const string DistantDefine = "KOMET_DISTANT", Definition = "#define " + DistantDefine + " 1\r\n",
        DistantMatrix = "kometDistantMatrix";

    private const string VertexDeclarations = """
        #if SHADOWQUALITY > 0
        uniform highp mat4 kometDistantMatrix;
        uniform highp float kometDistantOn;
        out vec4 shadowCoordsDistant;
        #endif

        """;

    // After the far cascade: shadowCoordsFar.w already lost the near weight, so the three sum to at most 1
    private const string VertexBody = """

        #if SHADOWQUALITY > 0
        	shadowCoordsDistant = kometDistantMatrix * worldPos;
        	float kometEdge = clamp((max(0.0, 0.02 - shadowCoordsDistant.x) + max(0.0, shadowCoordsDistant.x - 0.98) +
        		max(0.0, 0.02 - shadowCoordsDistant.y) + max(0.0, shadowCoordsDistant.y - 0.98)) * 50.0, 0.0, 1.0);
        	shadowCoordsDistant.w = kometDistantOn * max(0.0, 1.0 - kometEdge - shadowCoordsFar.w - nearSub);
        	if (shadowCoordsDistant.z >= 0.999) shadowCoordsDistant.w = 0.0;
        #endif

        """;

    private const string FragmentDeclarations = """

        #if SHADOWQUALITY > 0 && defined(KOMET_DISTANT)
        in vec4 shadowCoordsDistant;
        uniform highp sampler2DShadow kometDistantMap;
        uniform highp vec2 kometDistantTexel;
        #endif

        """;

    // kometDistantTexel: x the size of a texel, y the depth bias
    private const string FragmentBody = """

        	#if defined(KOMET_DISTANT)
        	if (shadowCoordsDistant.w > 0) {
        		float kometLit = 0.0;
        		for (int x = 0; x < 2; x++) {
        			for (int y = 0; y < 2; y++) {
        				kometLit += texture(kometDistantMap, vec3(shadowCoordsDistant.xy + (vec2(x, y) - 0.5) * kometDistantTexel.x,
        					shadowCoordsDistant.z - kometDistantTexel.y));
        			}
        		}
        		b -= shadowIntensity * (1.0 - kometLit / 4.0) * shadowCoordsDistant.w * 0.5;
        	}
        	#endif

        """;

    // Anchors: after the far cascade's declarations, and after the statement setting its weight or brightness
    private static readonly Regex VertexDeclared = Pattern(@"out vec4 shadowCoordsFar;\s*#endif[^\n]*\n");
    private static readonly Regex VertexWeighted =
        Pattern(@"shadowCoordsFar\.w = max\(0\.0, clamp\(1\.0 - distanceFar, 0\.0, 1\.0\) - nearSub\);[^\n]*\n" +
                @"\s*if \(shadowCoordsFar\.z >= 0\.999\) shadowCoordsFar\.w = 0\.0;[^\n]*\n\s*#endif[^\n]*\n");
    private static readonly Regex FragmentDeclared = Pattern(@"uniform float shadowMapHeightInv;\s*#endif[^\n]*\n");
    private static readonly Regex FragmentShaded =
        Pattern(@"float b = 1\.0 - shadowIntensity \* totalFar \* shadowCoordsFar\.w \* 0\.5;[^\n]*\n");

    private static Regex Pattern(string pattern) =>
        NotNull(pattern)
            ? new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2))
            : throw new ArgumentNullException(nameof(pattern));

    // Include with the distant cascade, or null for unexpected text (shader pack, new engine)
    internal static string? Vertex(string? source) =>
        Add(source, VertexDeclared, VertexDeclarations, VertexWeighted, VertexBody);

    internal static string? Fragment(string? source) =>
        Add(source, FragmentDeclared, FragmentDeclarations, FragmentShaded, FragmentBody);

    private static string? Add(string? source, Regex declared, string declarations, Regex anchor, string body)
    {
        if (source is null || source.Contains(Marker, StringComparison.Ordinal)) return null;
        var (first, second) = (declared.Matches(source), anchor.Matches(source));
        if (first.Count != 1 || second.Count != 1 || !Assert(first[0].Index < second[0].Index)) return null;
        var (at, to) = (first[0].Index + first[0].Length, second[0].Index + second[0].Length);
        return Assert(at <= to) ? source[..at] + declarations + source[at..to] + body + source[to..] : null;
    }
}
