using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneAudioConsole;

public sealed class AdbResult
{
    public int Code = -1;
    public string Output = "";
    public List<string> Lines = new();
    public bool TimedOut;
}

/// <summary>
/// adb 封装：所有调用都带超时（P0-4 规避：旧版 connect+devices 各 8 秒最长卡 16 秒）。
/// </summary>
public sealed class AdbService
{
    private readonly string _adbExe;
    private readonly string _root;

    public AdbService(string root)
    {
        _root = root;
        _adbExe = Path.Combine(root, "adb.exe");
    }

    public bool Available => File.Exists(_adbExe);

    /// <summary>默认 5 秒超时（决策 D-05 / P0-4）</summary>
    public const int DefaultTimeoutMs = 5000;

    public async Task<AdbResult> RunAsync(string args, int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        var r = new AdbResult();
        if (!Available) { r.Output = "找不到 adb.exe"; return r; }

        var psi = new ProcessStartInfo
        {
            FileName = _adbExe,
            Arguments = args,
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // 清掉可能指向外部 adb 服务的环境变量
        psi.Environment.Remove("ADB_SERVER_SOCKET");
        psi.Environment.Remove("ANDROID_ADB_SERVER_PORT");

        try
        {
            using var p = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                r.TimedOut = true;
                try { p.Kill(true); } catch { }
                r.Output = "adb 超时无响应";
                r.Lines.Add("adb 超时无响应");
                return r;
            }

            // 等异步输出读完
            try { p.WaitForExit(500); } catch { }

            string text;
            lock (sb) text = sb.ToString();

            r.Code = p.ExitCode;
            r.Output = text;
            foreach (var ln in text.Split('\n'))
            {
                var s = ln.Trim();
                if (s.Length > 0) r.Lines.Add(s);
            }
        }
        catch (Exception ex)
        {
            r.Output = ex.Message;
            r.Lines.Add(ex.Message);
            r.Code = -3;
        }
        return r;
    }

    /// <summary>只输 IP 时自动补 :5555（D-17）</summary>
    public static string NormalizeAddress(string input)
    {
        var v = (input ?? "").Trim();
        if (v.Length == 0) return v;
        if (Regex.IsMatch(v, @"^\d{1,3}(\.\d{1,3}){3}$")) return v + ":5555";
        return v;
    }

    public static bool LooksLikeIp(string v) =>
        Regex.IsMatch(v ?? "", @"^\d{1,3}(\.\d{1,3}){3}(:\d+)?$");

    /// <summary>连接设备；返回真实状态字符串</summary>
    public async Task<(bool ok, string state, AdbResult raw)> ConnectAsync(
        string address, CancellationToken ct = default)
    {
        string serial = NormalizeAddress(address);

        if (LooksLikeIp(serial))
        {
            var c = await RunAsync($"connect {serial}", 8000, ct).ConfigureAwait(false);
            if (c.TimedOut) return (false, "timeout", c);
        }

        var dev = await RunAsync("devices", DefaultTimeoutMs, ct).ConfigureAwait(false);
        foreach (var ln in dev.Lines)
        {
            int tab = ln.IndexOf('\t');
            if (tab <= 0) continue;
            var s = ln[..tab].Trim();
            var st = ln[(tab + 1)..].Trim();
            int sp = st.IndexOf(' ');
            if (sp > 0) st = st[..sp];
            if (string.Equals(s, serial, StringComparison.OrdinalIgnoreCase))
                return (st == "device", st, dev);
        }
        return (false, "notfound", dev);
    }

    /// <summary>
    /// 手机音量 0–15（D-09 / D-16）。
    /// 优先 media volume，失败退回 keyevent 逐格。
    /// </summary>
    public async Task<bool> SetVolumeAsync(string serial, int level, CancellationToken ct = default)
    {
        level = Math.Clamp(level, 0, 15);
        var r = await RunAsync($"-s {serial} shell media volume --stream 3 --set {level}", 3000, ct)
            .ConfigureAwait(false);
        if (!r.TimedOut && r.Code == 0 && !r.Output.Contains("Error", StringComparison.OrdinalIgnoreCase)
            && !r.Output.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            return true;

        // 降级：keyevent 24 加 / 25 减
        var cur = await GetVolumeAsync(serial, ct).ConfigureAwait(false);
        int delta = level - cur;
        var key = delta > 0 ? "24" : "25";
        for (int i = 0; i < Math.Min(Math.Abs(delta), 16); i++)
        {
            await RunAsync($"-s {serial} shell input keyevent {key}", 2000, ct).ConfigureAwait(false);
            await Task.Delay(80, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>读当前音量（失败返回 -1）</summary>
    public async Task<int> GetVolumeAsync(string serial, CancellationToken ct = default)
    {
        var r = await RunAsync($"-s {serial} shell media volume --stream 3 --get", 3000, ct)
            .ConfigureAwait(false);
        if (!r.TimedOut)
        {
            var m = Regex.Match(r.Output, @"volume is (\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) return v;
        }
        return -1;
    }

    /// <summary>
    /// 判断 adb server 是否**在本次程序启动之前**就已经在运行。
    /// 用进程启动时间与自身启动时间比较，避免 netstat 时序竞争误判。
    /// </summary>
    public static bool IsServerRunningBeforeMe()
    {
        try
        {
            var selfStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("adb"))
            {
                try
                {
                    // 有别的 adb 比我早启动 → 那个才是 server，不该被我关掉
                    if (p.StartTime < selfStart.AddSeconds(-2))
                        return true;
                }
                catch { }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
