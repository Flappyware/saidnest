using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SaidNest.Capture;

// Read-only Windows endpoint routing probe. No session names or speech are collected.
static class EndpointSessions
{
    public static int DiscordCount(string endpoint)
    {
        object enumerator = new DeviceEnumerator();
        object? manager = null;
        IDevice? device = null;
        ISessions? sessions = null;
        try
        {
            Marshal.ThrowExceptionForHR(((IDevices)enumerator).GetDevice(endpoint, out device));
            var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out manager));
            Marshal.ThrowExceptionForHR(((IManager)manager).GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out int count));
            int matches=0;
            for(int index=0;index<count;index++)
            {
                Marshal.ThrowExceptionForHR(sessions.GetSession(index,out var session));
                try {
                    Marshal.ThrowExceptionForHR(((IControl2)session).GetProcessId(out uint pid));
                    try { using var process=Process.GetProcessById((int)pid); if(process.ProcessName.Contains("Discord",StringComparison.OrdinalIgnoreCase)) matches++; }
                    catch(ArgumentException) { }
                } finally { Marshal.FinalReleaseComObject(session); }
            }
            return matches;
        }
        finally {
            if(sessions!=null) Marshal.FinalReleaseComObject(sessions);
            if(manager!=null) Marshal.FinalReleaseComObject(manager);
            if(device!=null) Marshal.FinalReleaseComObject(device);
            Marshal.FinalReleaseComObject(enumerator);
        }
    }
    [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumerator { }
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevices {
        [PreserveSig] int EnumAudioEndpoints(int flow,int state,out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id,out IDevice device);
    }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevice {
        [PreserveSig] int Activate(ref Guid iid,int context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }
    [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IManager {
        [PreserveSig] int GetAudioSessionControl(ref Guid id,int flags,out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid id,int flags,out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out ISessions sessions);
    }
    [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISessions {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index,[MarshalAs(UnmanagedType.IUnknown)] out object session);
    }
    [ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IControl2 {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr value);
        [PreserveSig] int SetDisplayName(IntPtr value,IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr value);
        [PreserveSig] int SetIconPath(IntPtr value,IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid value);
        [PreserveSig] int SetGroupingParam(ref Guid value,IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr value);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr value);
        [PreserveSig] int GetProcessId(out uint pid);
    }
}
