using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhoneAudioConsole;

/// <summary>整体状态（对应决策文档的状态机）</summary>
public enum AppState
{
    Idle,        // 未连接
    Connecting,  // 连接中
    Ready,       // 已连接未播放
    Starting,    // 拉起 scrcpy 中
    Playing,     // 正在播放
    Failed,      // 失败（内联提示）
    Lost         // 设备掉线
}

/// <summary>主题</summary>
public enum AppTheme { Light, Dark }

/// <summary>音源选项（值来自 scrcpy 5.0.1 --help 实测）</summary>
public sealed class AudioSource
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Note { get; init; } = "";
    public override string ToString() => Label;

    public static readonly IReadOnlyList<AudioSource> All = new List<AudioSource>
    {
        new() { Label = "整个手机声音", Value = "output", Note = "设备静音，电脑出声" },
        new() { Label = "仅媒体播放",   Value = "playback", Note = "App 可退出捕获" },
        new() { Label = "手机麦克风",   Value = "mic", Note = "" },
        new() { Label = "麦克风（原始）", Value = "mic-unprocessed", Note = "" },
        new() { Label = "麦克风（录像）", Value = "mic-camcorder", Note = "" },
        new() { Label = "麦克风（语音识别）", Value = "mic-voice-recognition", Note = "" },
        new() { Label = "麦克风（通话优化）", Value = "mic-voice-communication", Note = "带回声消除" },
        new() { Label = "通话音",       Value = "voice-call", Note = "" },
        new() { Label = "通话上行",     Value = "voice-call-uplink", Note = "" },
        new() { Label = "通话下行",     Value = "voice-call-downlink", Note = "" },
        new() { Label = "演唱（麦克风+播放）", Value = "voice-performance", Note = "卡拉OK" },
    };
}

/// <summary>码率选项</summary>
public sealed class BitRateOption
{
    public string Label { get; init; } = "";
    public string Note { get; init; } = "";
    public override string ToString() => Label;

    public static readonly IReadOnlyList<BitRateOption> All = new List<BitRateOption>
    {
        new() { Label = "64K",  Note = "低带宽" },
        new() { Label = "128K", Note = "scrcpy 默认" },
        new() { Label = "192K", Note = "" },
        new() { Label = "256K", Note = "" },
        new() { Label = "320K", Note = "高音质" },
        new() { Label = "512K", Note = "极高，吃带宽" },
    };
}

/// <summary>错误提示（D-13 内联提示条）</summary>
public sealed class ErrorInfo
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Suggest { get; init; } = "";
}
