using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Below 100 % resolution BlitPrimaryToDefault stretches the scene with one bilinear sample: soft edges. The blit shader
// instead upscales like FSR 1 EASU (twelve texels, edge-direction filter clamped to the nearest four, no ringing) and
// sharpens like RCAS; the GUI still draws at window size. The same program copies 1:1 into the luma buffer without
// FXAA: where the scene is not smaller than the target (texcoord derivative) it samples as before.
internal static class SharpUpscale
{
    private const string Sample = "outColor = texture(scene, texCoord);";

    private const string Easu = """

        vec3 kometTexel(sampler2D image, ivec2 at, ivec2 size) { return texelFetch(image, clamp(at, ivec2(0), size - 1), 0).rgb; }
        float kometLuma(vec3 c) { return c.g + 0.5 * (c.r + c.b); }

        void kometSet(inout vec2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE) {
        	float lenX = max(abs(lD - lC), abs(lC - lB));
        	float dirX = lD - lB;
        	dir.x += dirX * w;
        	lenX = clamp(abs(dirX) / lenX, 0.0, 1.0);
        	len += lenX * lenX * w;
        	float lenY = max(abs(lE - lC), abs(lC - lA));
        	float dirY = lE - lA;
        	dir.y += dirY * w;
        	lenY = clamp(abs(dirY) / lenY, 0.0, 1.0);
        	len += lenY * lenY * w;
        }

        void kometTap(inout vec3 aC, inout float aW, vec2 off, vec2 dir, vec2 len, float lob, float clp, vec3 c) {
        	vec2 v = vec2(dot(off, dir), dot(off, vec2(-dir.y, dir.x))) * len;
        	float d2 = min(dot(v, v), clp);
        	float wB = 0.4 * d2 - 1.0;
        	float wA = lob * d2 - 1.0;
        	wB *= wB;
        	wA *= wA;
        	float w = (1.5625 * wB - 0.5625) * wA;
        	aC += c * w;
        	aW += w;
        }

        // b c / e f g h / i j k l / n o around f, the texel left of and below the sample
        vec3 kometEasu(sampler2D image, ivec2 size, vec2 outSize) {
        	vec2 pp = gl_FragCoord.xy * (vec2(size) / outSize) - 0.5;
        	vec2 fp = floor(pp);
        	pp -= fp;
        	ivec2 f0 = ivec2(fp);
        	vec3 bC = kometTexel(image, f0 + ivec2(0, -1), size), cC = kometTexel(image, f0 + ivec2(1, -1), size);
        	vec3 eC = kometTexel(image, f0 + ivec2(-1, 0), size), fC = kometTexel(image, f0, size);
        	vec3 gC = kometTexel(image, f0 + ivec2(1, 0), size), hC = kometTexel(image, f0 + ivec2(2, 0), size);
        	vec3 iC = kometTexel(image, f0 + ivec2(-1, 1), size), jC = kometTexel(image, f0 + ivec2(0, 1), size);
        	vec3 kC = kometTexel(image, f0 + ivec2(1, 1), size), lC = kometTexel(image, f0 + ivec2(2, 1), size);
        	vec3 nC = kometTexel(image, f0 + ivec2(0, 2), size), oC = kometTexel(image, f0 + ivec2(1, 2), size);
        	float bL = kometLuma(bC), cL = kometLuma(cC), eL = kometLuma(eC), fL = kometLuma(fC), gL = kometLuma(gC);
        	float hL = kometLuma(hC), iL = kometLuma(iC), jL = kometLuma(jC), kL = kometLuma(kC), lL = kometLuma(lC);
        	float nL = kometLuma(nC), oL = kometLuma(oC);
        	vec2 dir = vec2(0.0);
        	float len = 0.0;
        	kometSet(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
        	kometSet(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
        	kometSet(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
        	kometSet(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);
        	float dirR = dot(dir, dir);
        	bool zero = dirR < 1.0 / 32768.0;
        	dir = zero ? vec2(1.0, 0.0) : dir * inversesqrt(dirR);
        	len = len * 0.5;
        	len *= len;
        	float stretch = dot(dir, dir) / max(abs(dir.x), abs(dir.y));
        	vec2 len2 = vec2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
        	float lob = 0.5 - 0.29 * len;
        	float clp = 1.0 / lob;
        	vec3 aC = vec3(0.0);
        	float aW = 0.0;
        	kometTap(aC, aW, vec2(0.0, -1.0) - pp, dir, len2, lob, clp, bC);
        	kometTap(aC, aW, vec2(1.0, -1.0) - pp, dir, len2, lob, clp, cC);
        	kometTap(aC, aW, vec2(-1.0, 1.0) - pp, dir, len2, lob, clp, iC);
        	kometTap(aC, aW, vec2(0.0, 1.0) - pp, dir, len2, lob, clp, jC);
        	kometTap(aC, aW, vec2(0.0, 0.0) - pp, dir, len2, lob, clp, fC);
        	kometTap(aC, aW, vec2(-1.0, 0.0) - pp, dir, len2, lob, clp, eC);
        	kometTap(aC, aW, vec2(1.0, 1.0) - pp, dir, len2, lob, clp, kC);
        	kometTap(aC, aW, vec2(2.0, 1.0) - pp, dir, len2, lob, clp, lC);
        	kometTap(aC, aW, vec2(2.0, 0.0) - pp, dir, len2, lob, clp, hC);
        	kometTap(aC, aW, vec2(1.0, 0.0) - pp, dir, len2, lob, clp, gC);
        	kometTap(aC, aW, vec2(1.0, 2.0) - pp, dir, len2, lob, clp, oC);
        	kometTap(aC, aW, vec2(0.0, 2.0) - pp, dir, len2, lob, clp, nC);
        	vec3 lo = min(min(fC, gC), min(jC, kC)), hi = max(max(fC, gC), max(jC, kC));
        	vec3 c = clamp(aC / aW, lo, hi);
        	// FSR pairs EASU with sharpening (RCAS): here against the four nearest texels' mean, as strong as their contrast
        	// leaves room for, clamped to them so it cannot ring
        	vec3 mean = 0.25 * (fC + gC + jC + kC);
        	vec3 room = min(lo, 1.0 - hi) / max(hi, vec3(1.0 / 64.0));
        	vec3 amount = 0.5 * sqrt(clamp(room, 0.0, 1.0));
        	return clamp(c + amount * (c - mean), lo, hi);
        }

        """;

    private const string Upscaled = """
        ivec2 kometIn = textureSize(scene, 0);
        	vec2 kometOut = floor(1.0 / abs(vec2(dFdx(texCoord.x), dFdy(texCoord.y))) + 0.5);
        	if (float(kometIn.x) < kometOut.x - 0.5 && float(kometIn.y) < kometOut.y - 0.5)
        		outColor = vec4(kometEasu(scene, kometIn, kometOut), 1.0);
        	else outColor = texture(scene, texCoord);
        """;

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
        _ = Assert(Easu.Contains("kometEasu", StringComparison.Ordinal) && Upscaled.Contains("kometEasu", StringComparison.Ordinal));
        ShaderEdits.Install(harmony, api);
    }

    internal static void Edit(ShaderProgram program)
    {
        if (!Enabled || !NotNull(program) || program.PassName != "blit" || program.FragmentShader is not { Code: { } code } shader ||
            code.Contains("kometEasu", StringComparison.Ordinal)) return;
        var (sample, main) = (code.IndexOf(Sample, StringComparison.Ordinal), code.IndexOf("void main", StringComparison.Ordinal));
        if (sample < main || main < 0 || code.IndexOf(Sample, sample + 1, StringComparison.Ordinal) >= 0 ||
            !code.Contains("uniform sampler2D scene;", StringComparison.Ordinal)) return;
        shader.Code = code[..main] + Easu + code[main..sample] + Upscaled + code[(sample + Sample.Length)..];
        _ = Assert(shader.Code.Length > code.Length);
    }
}
