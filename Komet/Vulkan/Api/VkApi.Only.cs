namespace Komet.Vulkan;

// Vulkan-only's commands for mods' raw OpenGL (Vk.Only.cs); each is core, the count draw only with drawIndirectCount
internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<IntPtr, uint, Vk.FormatProperties*, void> GetFormatProperties { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, void> CmdSetStencilCompareMask { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, void> CmdSetStencilWriteMask { get; private set; }
    public static delegate* unmanaged<IntPtr, uint, uint, void> CmdSetStencilReference { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, uint, uint, void> CmdDrawIndirect { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint, uint, void> CmdDrawIndirectCount { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, int, Vk.DepthStencilValue*, uint, Vk.ColorRange*, void>
        CmdClearDepthStencilImage { get; private set; }

    private static void LoadFormats(IntPtr instance)
    {
        if (!Assert(instance != IntPtr.Zero)) return;
        GetFormatProperties = (delegate* unmanaged<IntPtr, uint, Vk.FormatProperties*, void>)Instance(instance,
            "vkGetPhysicalDeviceFormatProperties");
        _ = Assert(_instanceProc != null);
    }

    private static bool LoadOnly(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return false;
        CmdSetStencilCompareMask = (delegate* unmanaged<IntPtr, uint, uint, void>)Device(device, "vkCmdSetStencilCompareMask");
        CmdSetStencilWriteMask = (delegate* unmanaged<IntPtr, uint, uint, void>)Device(device, "vkCmdSetStencilWriteMask");
        CmdSetStencilReference = (delegate* unmanaged<IntPtr, uint, uint, void>)Device(device, "vkCmdSetStencilReference");
        CmdDrawIndirect = (delegate* unmanaged<IntPtr, ulong, ulong, uint, uint, void>)Device(device, "vkCmdDrawIndirect");
        CmdDrawIndirectCount = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, ulong, uint, uint, void>)Device(device,
            "vkCmdDrawIndirectCount");
        CmdClearDepthStencilImage = (delegate* unmanaged<IntPtr, ulong, int, Vk.DepthStencilValue*, uint, Vk.ColorRange*, void>)
            Device(device, "vkCmdClearDepthStencilImage");
        return Assert(CmdDrawIndirect != null) && CmdSetStencilCompareMask != null && CmdSetStencilWriteMask != null &&
               CmdSetStencilReference != null && CmdClearDepthStencilImage != null;
    }
}
