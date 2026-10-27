using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneAudioConsole;

/// <summary>
/// Windows 侧音量：Core Audio 会话音量（作用于单个进程）。
/// 只在“降低音量”方向使用：面板 100% = 不衰减，往下拖才生效。
/// </summary>
public static class WindowsVolume
{
    private const int CLSCTX_ALL = 0x17;
    private const int eRender = 0;
    private const int eMultimedia = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(ref Guid g, int flags, out IntPtr session);
        int GetSimpleAudioVolume(ref Guid g, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid eventContext);
        int GetMute(out bool mute);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, ref Guid eventContext);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, ref Guid eventContext);
        int GetGroupingParam(out Guid grouping);
        int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out int pid);
    }

    /// <summary>诊断：列出所有音频会话（pid、进程名、音量）</summary>
    public static string DumpSessions()
    {
        var sb = new StringBuilder();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device);
            sb.AppendLine($"endpoint hr={hr}");
            if (hr != 0) return sb.ToString();

            var iid = typeof(IAudioSessionManager2).GUID;
            hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            sb.AppendLine($"activate hr={hr}");
            if (hr != 0) return sb.ToString();

            var manager = (IAudioSessionManager2)obj;
            hr = manager.GetSessionEnumerator(out var sessions);
            sb.AppendLine($"enum hr={hr}");
            if (hr != 0) return sb.ToString();

            sessions.GetCount(out int count);
            sb.AppendLine($"sessions={count}");

            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var sess) != 0) continue;
                var ctl = sess as IAudioSessionControl2;
                if (ctl == null) { sb.AppendLine($"  [{i}] not ctl2"); continue; }
                ctl.GetProcessId(out int pid);
                string name = "?";
                try { name = Process.GetProcessById(pid).ProcessName; } catch { }
                string vol = "?";
                if (sess is ISimpleAudioVolume v && v.GetMasterVolume(out float lv) == 0)
                    vol = lv.ToString("0.00");
                sb.AppendLine($"  [{i}] pid={pid} name={name} vol={vol}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EX: " + ex);
        }
        return sb.ToString();
    }

    /// <summary>设置指定进程的会话音量（0~1）</summary>
    public static bool SetProcessVolume(int processId, float level, bool? mute = null)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0) return false;

            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) return false;

            var manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out var sessions) != 0) return false;
            sessions.GetCount(out int count);

            bool found = false;
            var ctx = Guid.Empty;
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var sess) != 0) continue;
                var ctl = sess as IAudioSessionControl2;
                if (ctl == null) continue;
                if (ctl.GetProcessId(out int pid) != 0 || pid != processId) continue;
                if (sess is ISimpleAudioVolume v)
                {
                    v.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref ctx);
                    if (mute.HasValue) v.SetMute(mute.Value, ref ctx);
                    found = true;
                }
            }
            return found;
        }
        catch { return false; }
    }

    /// <summary>读取指定进程的会话音量；找不到返回 -1</summary>
    public static float GetProcessVolume(int processId)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0) return -1f;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) return -1f;
            var manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out var sessions) != 0) return -1f;
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var sess) != 0) continue;
                var ctl = sess as IAudioSessionControl2;
                if (ctl == null) continue;
                if (ctl.GetProcessId(out int pid) != 0 || pid != processId) continue;
                if (sess is ISimpleAudioVolume v && v.GetMasterVolume(out float lv) == 0) return lv;
            }
        }
        catch { }
        return -1f;
    }
}
