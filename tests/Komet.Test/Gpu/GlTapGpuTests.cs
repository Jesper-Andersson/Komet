using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class GlTapGpuTests
{
    private static readonly float[] Fog = [0.5f, 0.6f, 0.7f, 1], Light = [0.1f, 0.2f, 0.3f];

    [Test]
    public void WhatOpenGlIsGivenTheMirrorHoldsToo()
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        using var scene = new TerrainScene(rig.Device!, true);
        var mirror = new UniformMirror();
        GL.ProgramUniform1(scene.Program, GL.GetUniformLocation(scene.Program, "fogDensityIn"), 0.25f);
        Assert.That(GlTap.Tap(), Is.True);
        try
        {
            GlTap.Watch(scene.Program, scene.Ported, mirror);
            var density = scene.Ported.VertexUniforms.Members.Single(u => u.Name == "fogDensityIn");
            Assert.That(BitConverter.UInt32BitsToSingle(mirror.Held(density)[0]), Is.EqualTo(0.25f),
                "set before the watch began: seeded from the program");
            GL.UseProgram(scene.Program);
            GL.Uniform4(GL.GetUniformLocation(scene.Program, "rgbaFogIn"), 0.5f, 0.6f, 0.7f, 1f);
            GL.Uniform3(GL.GetUniformLocation(scene.Program, "pointLights[1]"), 1, Light);
            GL.Uniform1(GL.GetUniformLocation(scene.Program, "terrainTex"), 5);
            GL.ActiveTexture(TextureUnit.Texture5);
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            var sampler = GL.GenSampler();
            GL.BindSampler(5, sampler);
            GL.ActiveTexture(TextureUnit.Texture0);
            Assert.That(GlTap.Program, Is.EqualTo(scene.Program));
            GL.UseProgram(0);

            var fog = scene.Ported.VertexUniforms.Members.Single(u => u.Name == "rgbaFogIn");
            Assert.That(mirror.Held(fog).Select(BitConverter.UInt32BitsToSingle), Is.EqualTo(Fog));
            var lights = scene.Ported.VertexUniforms.Members.Single(u => u.Name == "pointLights");
            Assert.That(mirror.Held(lights).Skip(3).Take(3).Select(BitConverter.UInt32BitsToSingle), Is.EqualTo(Light),
                "element 1 of the array");
            Assert.That(GlTap.Unit(mirror.Int("terrainTex")), Is.EqualTo((texture, sampler)));
            var held = new float[4];
            GL.GetUniform(scene.Program, GL.GetUniformLocation(scene.Program, "rgbaFogIn"), held);
            Assert.That(held, Is.EqualTo(Fog), "and OpenGL got it");
            GL.DeleteTexture(texture);
            GL.DeleteSampler(sampler);
        }
        finally
        {
            GlTap.Untap();
        }
    }

    [Test]
    public void TheStateIsKeptAsOpenGlHasIt()
    {
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.BlendFunc(1, BlendingFactorSrc.Zero, BlendingFactorDest.OneMinusSrcColor);
        Assert.That(GlTap.Tap(), Is.True);
        var framebuffer = GL.GenFramebuffer();
        try
        {
            var seeded = GlTap.State;
            Assert.That(seeded.Caps & (uint)GlTap.Caps.DepthTest, Is.Not.Zero, "enabled before tapping");
            Assert.That(seeded.Depth & 7, Is.EqualTo(3u), "GL_LEQUAL as VK_COMPARE_OP_LESS_OR_EQUAL");
            Assert.That(seeded.Blend[1] & 0xFFFF, Is.EqualTo(0u | (3u << 4) | (0u << 8) | (3u << 12)),
                "ZERO, ONE_MINUS_SRC_COLOR for draw buffer 1");

            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Front);
            GL.FrontFace(FrontFaceDirection.Cw);
            GL.DepthMask(false);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.BlendFunc(2, BlendingFactorSrc.One, BlendingFactorDest.Zero);
            GL.Disable(IndexedEnableCap.Blend, 3);
            GL.ColorMask(1, false, true, true, false);
            GL.BlendEquation(0, BlendEquationMode.Max);
            GL.Viewport(3, 4, 50, 60);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.DrawBuffers(2, [DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.None]);

            var state = GlTap.State;
            Assert.That(state.Caps & (uint)GlTap.Caps.DepthTest, Is.Zero);
            Assert.That(state.Depth & 8, Is.Zero, "depth writes off");
            Assert.That((state.Depth >> 4) & 3, Is.EqualTo(1u), "front faces culled");
            Assert.That(state.Depth & 64, Is.Zero, "clockwise front faces");
            Assert.That(state.Blend[0], Is.EqualTo(6u | (7u << 4) | (6u << 8) | (7u << 12) | (4u << 16) | (4u << 19) |
                                                  (0xFu << 22) | (1u << 26)), "SRC_ALPHA, 1-SRC_ALPHA, MAX, on");
            Assert.That(state.Blend[2] & 0xFFFF, Is.EqualTo(0x101u), "ONE, ZERO for draw buffer 2");
            Assert.That((state.Blend[3] >> 26) & 1, Is.Zero, "blending off for draw buffer 3");
            Assert.That((state.Blend[1] >> 22) & 0xF, Is.EqualTo(0b0110u), "green and blue written");
            Assert.That(GlTap.Viewport, Is.EqualTo((3, 4, 50, 60)));
            Assert.That(GlTap.DrawFramebuffer, Is.EqualTo(framebuffer));
            Assert.That(GlTap.DrawBuffersOf(framebuffer).Take(3), Is.EqualTo([1, -1, -1]));
        }
        finally
        {
            GlTap.Untap();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(framebuffer);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            GL.DepthMask(true);
            GL.ColorMask(true, true, true, true);
        }
    }
}
