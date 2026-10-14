using Komet.Vulkan;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class GpuTimesTests
{
    private const ulong Bytes = 64UL << 20;

    [Test]
    public void TheTimeLandsInTheSectionThatTookIt()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var device = rig.Device!;
        if (device.TimestampPeriod <= 0) Assert.Ignore("the queue takes no timestamps");
        using var from = HostBuffer.Create(device, Bytes);
        using var to = HostBuffer.Create(device, Bytes);
        using var frame = VulkanFrame.Create(device, out var why);
        Assert.That(frame, Is.Not.Null, why);
        var (copy, idle) = (VulkanFrame.SectionOf("test copy"), VulkanFrame.SectionOf("test idle"));
        var measuring = VulkanFrame.Measuring;
        VulkanFrame.Measuring = true;
        try
        {
            for (var f = 0; f < VulkanFrame.Slots + 2; f++)
            {
                frame!.Next();
                Assert.That(frame.Begin(out why), Is.True, why);
                frame.Section(copy);
                Assert.That(frame.CopyBuffer((from!.Buffer, 0), (to!.Buffer, 0), Bytes), Is.True);
                frame.Section(idle);
                frame.Close("the test's frame");
            }

            frame!.Next();
            Assert.That(device.WaitIdle(), Is.True);
            for (var f = 0; f < VulkanFrame.Slots; f++) frame.Next(); // every slot's fence passed and read
            var line = frame.Times();
            Assert.Multiple(() =>
            {
                Assert.That(device.TimestampPeriod, Is.InRange(0.01f, 1000f), "ns per tick");
                Assert.That(Ms(line, "test copy"), Is.GreaterThan(0.05), line);
                Assert.That(Ms(line, "test copy"), Is.GreaterThan(10 * Ms(line, "test idle")), line);
                Assert.That(line, Does.Contain("Vulkan busy"), line);
            });
        }
        finally
        {
            VulkanFrame.Measuring = measuring;
        }
    }

    // What the HUD's GPU rows show while Vulkan draws: the last timed frame's terrain sections in RenderCost's groups (shadow
    // maps, opaque, liquids and transparent) and Vulkan's busy time for the whole frame
    [Test]
    public void TheLastFrameFeedsTheHudsRows()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var device = rig.Device!;
        if (device.TimestampPeriod <= 0) Assert.Ignore("the queue takes no timestamps");
        using var from = HostBuffer.Create(device, Bytes);
        using var to = HostBuffer.Create(device, Bytes);
        using var frame = VulkanFrame.Create(device, out var why);
        Assert.That(frame, Is.Not.Null, why);
        var (opaque, sky) = (VulkanFrame.SectionOf("opaque terrain"), VulkanFrame.SectionOf("opaque sky"));
        var measuring = VulkanFrame.Measuring;
        VulkanFrame.Measuring = true;
        Span<double> groups = stackalloc double[3];
        try
        {
            Assert.That(frame!.LastBusyMs, Is.NaN, "nothing timed yet");
            for (var f = 0; f < VulkanFrame.Slots + 2; f++)
            {
                frame.Next();
                Assert.That(frame.Begin(out why), Is.True, why);
                frame.Section(opaque);
                Assert.That(frame.CopyBuffer((from!.Buffer, 0), (to!.Buffer, 0), Bytes), Is.True);
                frame.Section(sky);
                frame.Close("the test's frame");
            }

            Assert.That(device.WaitIdle(), Is.True);
            frame.Next(); // an earlier frame's slot comes round, its fence passed: its times are the last
            Assert.That(VulkanRenderer.TerrainMs(frame, groups), Is.True);
            var (shadows, terrain, transparent, busy) = (groups[0], groups[1], groups[2], frame.LastBusyMs);
            Assert.Multiple(() =>
            {
                Assert.That(terrain, Is.GreaterThan(0.05), "the copy, in the opaque terrain");
                Assert.That((shadows, transparent), Is.EqualTo((0.0, 0.0)), "no shadow or transparent terrain drawn");
                Assert.That(busy, Is.GreaterThanOrEqualTo(terrain), "the frame's busy time holds it");
            });
        }
        finally
        {
            VulkanFrame.Measuring = measuring;
        }
    }

    private static double Ms(string line, string section)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, section + @" (\d+)[.,](\d+)");
        return match.Success ? double.Parse(match.Groups[1].Value + "." + match.Groups[2].Value,
            System.Globalization.CultureInfo.InvariantCulture) : 0;
    }
}
