using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Vulkan;

// The one thing Vulkan asks OpenGL's sharing extensions: GL_DEVICE_UUID_EXT, the GPU the context runs on, via any
// extension defining it (GL_EXT_memory_object or GL_EXT_semaphore). Needs a current context.
internal static unsafe class GlInterop
{
    private const int DeviceUuid = 0x9597;

    private static readonly string[] Defining = ["GL_EXT_memory_object", "GL_EXT_semaphore"];

    private static delegate* unmanaged<uint, uint, byte*, void> _getUnsignedBytei;

    // The GPU the context runs on, as Vulkan's VkPhysicalDeviceIDProperties names it; false if no extension offers it
    public static bool Uuid(Span<byte> uuid)
    {
        if (!Assert(uuid.Length == VulkanDevice.UuidBytes) || !Assert(Defining.Length == 2)) return false;
        if (_getUnsignedBytei == null && Array.Exists(Defining, Diagnostics.GpuStats.Offered))
            _getUnsignedBytei = (delegate* unmanaged<uint, uint, byte*, void>)GLFW.GetProcAddress("glGetUnsignedBytei_vEXT");
        if (_getUnsignedBytei == null) return false;
        fixed (byte* bytes = uuid) _getUnsignedBytei(DeviceUuid, 0, bytes);
        return Assert(uuid.Length == 16) && uuid.IndexOfAnyExcept((byte)0) >= 0;
    }
}
