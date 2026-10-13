using System.Runtime.InteropServices;
using System.Text;
using Spectari.Audio;

namespace Spectari.Capture;

internal static class DeviceContainerIdentity
{
    internal static readonly CoreAudioInterop.PropertyKey ContainerId = new(
        new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    internal static Guid? ForVideoInterface(string symbolicLink)
    {
        var instanceKey = new CoreAudioInterop.PropertyKey(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
        byte[] instanceBuffer = new byte[4096];
        uint size = (uint)instanceBuffer.Length;
        if (CM_Get_Device_Interface_PropertyW(symbolicLink, ref instanceKey, out uint type,
            instanceBuffer, ref size, 0) != 0 || type != 18 || size < 2 || size > instanceBuffer.Length) return null;
        string instance = Encoding.Unicode.GetString(instanceBuffer, 0, (int)size).TrimEnd('\0');
        if (CM_Locate_DevNodeW(out uint node, instance, 0) != 0) return null;
        byte[] container = new byte[16];
        size = 16;
        var key = ContainerId;
        if (CM_Get_DevNode_PropertyW(node, ref key, out type, container, ref size, 0) != 0
            || type != 13 || size != 16) return null;
        Guid id = new(container);
        return id == Guid.Empty ? null : id;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string deviceInterface,
        ref CoreAudioInterop.PropertyKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node,
        ref CoreAudioInterop.PropertyKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
}
