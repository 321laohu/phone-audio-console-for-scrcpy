using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using IOPath = System.IO.Path;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PhoneAudioConsole;

public partial class MainWindow : Window
{
    // ---------- 基础设施 ----------
    private readonly string _root;
    private readonly AdbService _adb;
    /// <summary>本程序是否自己启动了 adb server（决定退出时要不要 kill 掉）</summary>
    private bool _adbStartedServer;
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private Process? _scrcpy = null;
    private DateTime _startedAt;
    private bool _realExit;

    // ---------- 状态 ----------
    private AppState _state = AppState.Idle;
    private AppTheme _theme = AppTheme.Light;
    private string _address = "192.168.1.100:5555";
    private string _serial = "";
    private string _bitRate = "256K";
    private AudioSource _source = AudioSource.All[0];
    private int _volume = 100;   // 0-100，100 = 不衰减（面板默认）
    private int _lastVolume = 100;
    private bool _muted;

    // ---------- 托盘 ----------
    private WinForms.NotifyIcon? _tray = null;
    private WindowState _savedState;

    // ---------- 设置 ----------
    private readonly string _settingsPath;

    public MainWindow()
    {
        InitializeComponent();
        _root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        _adb = new AdbService(_root);
        // 记录 adb server 初始状态：如果本来没跑，说明是本程序拉起来的，退出时应该关掉
        _adbStartedServer = !AdbService.IsServerRunningBeforeMe();

        var dir = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "手机音频控制台");
        try { Directory.CreateDirectory(dir); } catch { }
        _settingsPath = IOPath.Combine(AppDataDir, "settings.ini");

        LoadSettings();
        BuildNoiseTexture();
        SetupBars();
        ApplyTheme(_theme);
        UpdateVolumeVisual();
        SetState(AppState.Idle);

        _tickTimer.Tick += TickTimer_Tick;
        _pulseTimer.Tick += (_, _) => PulseDot();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        SizeChanged += (_, _) => RedrawNoise();
    }

    // ============================================================
    // 启动
    // ============================================================
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RedrawNoise();
        CreateTray();
        _tickTimer.Start();
        _pulseTimer.Start();

        // 布局完成后强制同步一次界面值（修复首次渲染丢值）
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        SyncUiFromState();
        Log("界面已就绪");

        if (!_adb.Available)
        {
            ShowError("找不到 adb.exe", $"请把 adb.exe 放回 {_root}", "");
            return;
        }

        // 启动时自动探测一次设备（不阻塞界面）
        await ProbeSilentlyAsync();
    }

    /// <summary>把内存状态同步到界面（修复首次渲染丢值）</summary>
    private void SyncUiFromState()
    {
        AddrText.Text = _address;
        RateText.Text = _bitRate;
        SrcText.Text = _source.Label;
        UpdateVolumeVisual();
        Log($"[同步] addr={_address} rate={_bitRate} src={_source.Label} vol={_volume}");
    }

    private async Task ProbeSilentlyAsync()
    {
        try
        {
            var (ok, st, _) = await _adb.ConnectAsync(_address);
            if (ok)
            {
                _serial = AdbService.NormalizeAddress(_address);
                SetState(AppState.Ready);
                // 电量/音量都不再从手机读：本面板只做“降低”方向的输出衰减
                UpdateVolumeVisual();
            }
            else
            {
                SetState(AppState.Idle);
            }
        }
        catch { SetState(AppState.Idle); }
    }

    // ============================================================
    // 噪点纹理（对应 HTML 的 feTurbulence）
    // ============================================================
    private void BuildNoiseTexture()
    {
        var w = 160; var h = 160;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[w * h * 4];
        var rnd = new Random(7);
        for (int i = 0; i < w * h; i++)
        {
            byte v = (byte)rnd.Next(0, 255);
            px[i * 4 + 0] = v;
            px[i * 4 + 1] = v;
            px[i * 4 + 2] = v;
            px[i * 4 + 3] = 255;
        }
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);

        NoiseLayer.Fill = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, w, h),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
    }

    /// <summary>
    /// 真正的圆角裁剪：ClipToBounds 只做矩形裁剪，圆角需要 RectangleGeometry。
    /// 窗口尺寸变化时同步更新裁剪区域。
    /// </summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RootClip != null)
            RootClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        RedrawNoise();
    }

    private void RedrawNoise()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w > 0 && h > 0 && RootClip != null)
            RootClip.Rect = new Rect(0, 0, w, h);
        NoiseLayer.Width = Math.Max(0, w);
        NoiseLayer.Height = Math.Max(0, h);
    }

    // ============================================================
    // 声波动画
    // ============================================================
    private readonly (double min, double max, int ms)[] _barSpec =
    {
        (10, 26, 700), (26, 52, 820), (40, 80, 640), (30, 58, 900),
        (48, 96, 580), (26, 54, 760), (36, 74, 860), (14, 34, 700)
    };

    private void SetupBars()
    {
        var bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8 };
        for (int i = 0; i < bars.Length; i++)
        {
            var (min, max, ms) = _barSpec[i];
            var st = new Storyboard();
            var a = new DoubleAnimation
            {
                From = min,
                To = max,
                Duration = TimeSpan.FromMilliseconds(ms),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 90),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(a, bars[i]);
            Storyboard.SetTargetProperty(a, new PropertyPath("Height"));
            st.Children.Add(a);
            st.Begin(bars[i], true);
        }
    }

    // ============================================================
    // 主题（D-05）
    // ============================================================
    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(_theme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
        SaveSettings();
    }

    private void ApplyTheme(AppTheme t)
    {
        _theme = t;
        bool dark = t == AppTheme.Dark;

        Brush mainText = dark ? B("#EAE4D7") : B("#1C1914");
        Brush subText = dark ? B("#99EAE4D7") : B("#991C1914");
        Brush panelBg = dark ? B("#17FFFFFF") : B("#9EFFFFFF");
        Brush panelBorder = dark ? B("#29FFFFFF") : B("#E6FFFFFF");
        Brush iconBg = dark ? B("#26FFFFFF") : B("#241C1914");

        // 背景光斑
        SetBlob(dark, Blob1Center, Blob1Edge, "#2B4A6E", "#002B4A6E", "#A8B8FF", "#00A8B8FF");
        SetBlob(dark, Blob2Center, Blob2Edge, "#4A2A5E", "#004A2A5E", "#FFC4E8", "#00FFC4E8");
        SetBlob(dark, Blob3Center, Blob3Edge, "#245A52", "#00245A52", "#9FE8FF", "#009FE8FF");
        if (BgLayer.Background is LinearGradientBrush baseBrush)
        {
            var c = dark ? B("#0D1013") : B("#D8D2C4");
            baseBrush.GradientStops[0].Color = (c as SolidColorBrush)!.Color;
            baseBrush.GradientStops[1].Color = (c as SolidColorBrush)!.Color;
        }

        // 玻璃
        var glassBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        if (dark)
        {
            glassBrush.GradientStops.Add(new GradientStop(HexA("#B7121517", 0x75), 0));
            glassBrush.GradientStops.Add(new GradientStop(C("#26FFFFFF"), 0.16));
            glassBrush.GradientStops.Add(new GradientStop(C("#00FFFFFF"), 0.42));
            glassBrush.GradientStops.Add(new GradientStop(C("#00FFFFFF"), 0.76));
            glassBrush.GradientStops.Add(new GradientStop(C("#10FFFFFF"), 1));
        }
        else
        {
            glassBrush.GradientStops.Add(new GradientStop(C("#8CFCF9F3"), 0));
            glassBrush.GradientStops.Add(new GradientStop(C("#40FCF9F3"), 0.16));
            glassBrush.GradientStops.Add(new GradientStop(C("#00FCF9F3"), 0.42));
            glassBrush.GradientStops.Add(new GradientStop(C("#00FCF9F3"), 0.76));
            glassBrush.GradientStops.Add(new GradientStop(C("#28FCF9F3"), 1));
        }
        GlassLayer.Background = glassBrush;
        GlassLayer.BorderBrush = dark ? B("#2EFFFFFF") : B("#BFFFFFFF");

        // 文字
        TitleText.Foreground = mainText;
        DeviceSub.Foreground = subText;
        ClockText.Foreground = mainText;
        ClockUnit.Foreground = subText;
        VolPct.Foreground = mainText;

        // 封面
        CoverBorder.Background = new RadialGradientBrush(
            dark ? C("#39474A") : C("#FDFAF3"),
            dark ? C("#0B0D0E") : C("#C7BDA6"));
        var barBrush = dark
            ? new LinearGradientBrush(C("#F2EDE1"), C("#8E8778"), 90)
            : new LinearGradientBrush(C("#2A251E"), C("#7A6F5C"), 90);
        foreach (var b in new[] { Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8 })
            b.Background = barBrush;
        CoverBorder.Effect = new DropShadowEffect
        {
            BlurRadius = 26,
            ShadowDepth = 8,
            Direction = 270,
            Opacity = dark ? 0.55 : 0.35,
            Color = Colors.Black
        };

        // 喇叭按钮圆底（悬停色随主题）
        try
        {
            VolIconBtn.ApplyTemplate();
            var res = VolIconBtn.Template?.Resources;
            // 用触发器无法改色，改为动态设置视觉树里的 Ellipse
            var ell = FindInButton<System.Windows.Shapes.Ellipse>(VolIconBtn, "IcBg");
            if (ell != null && ell.Fill is SolidColorBrush)
                ell.Fill = Brushes.Transparent;
        }
        catch { }

        // 音量条
        VolumeBar.Background = panelBg;
        VolumeBar.BorderBrush = panelBorder;
        VolFill.Background = dark ? B("#E8E3D6") : B("#1C1914");
        if (VolTrack.Children[0] is Border trackBg)
            trackBg.Background = dark ? B("#33FFFFFF") : B("#241C1914");

        // 参数框
        ParamsBox.Background = dark ? B("#1AFFFFFF") : B("#8CFFFFFF");
        ParamsBox.BorderBrush = dark ? B("#26FFFFFF") : B("#B3FFFFFF");
        foreach (var tb in FindAll<TextBlock>(ParamsBox))
        {
            if (tb.Text is "手机地址" or "码率" or "音源")
                tb.Foreground = subText;
            else
                tb.Foreground = mainText;
        }
        // 三个值文本单独指定，确保不被上面循环漏掉
        AddrText.Foreground = mainText;
        RateText.Foreground = mainText;
        SrcText.Foreground = mainText;
        // 重新渲染一次值，避免主题切换后文本丢失
        AddrText.Text = _address;
        RateText.Text = _bitRate;
        SrcText.Text = _source.Label;
        foreach (var bd in FindAll<Border>(ParamsBox))
            if (bd.Height == 1) bd.Background = dark ? B("#1FFFFFFF") : B("#241C1914");

        // 状态标签
        StateTag.Background = dark ? B("#1AFFFFFF") : B("#121C1914");
        StateTag.BorderBrush = dark ? B("#2BFFFFFF") : B("#1F1C1914");
        StateText.Foreground = mainText;
        StateDot.Fill = mainText;

        // 主按钮：模板内的渐变与文字
        try
        {
            MainBtn.ApplyTemplate();
            if (MainBtn.Template.FindName("BtnG1", MainBtn) is GradientStop g1)
                g1.Color = C(dark ? "#F6F2E9" : "#2C2820");
            if (MainBtn.Template.FindName("BtnG2", MainBtn) is GradientStop g2)
                g2.Color = C(dark ? "#DDD7CA" : "#131110");
            SetMainBtnColor(dark ? "#14171A" : "#F8F5EE");
        }
        catch (Exception ex) { Log("[主题] 主按钮异常：" + ex.Message); }

        // 标题栏圆形按钮
        foreach (var btn in new[] { ThemeBtn, MinBtn, CloseBtn })
        {
            btn.Background = iconBg;
            if (btn.Content is System.Windows.Shapes.Path p) p.Stroke = dark ? B("#E0FFFFFF") : B("#D91C1914");
        }

        // − / + 按钮
        foreach (var btn in new[] { VolMinus, VolPlus })
        {
            if (btn.Template?.LoadContent() is Border bd0)
            {
                bd0.Background = dark ? B("#1AFFFFFF") : B("#CCFFFFFF");
                bd0.BorderBrush = dark ? B("#26FFFFFF") : B("#F0FFFFFF");
            }
        }

        // 明暗图标：浅色时显示月亮样式
        ThemeIcon.Stroke = dark ? B("#E0FFFFFF") : B("#D91C1914");
    }

    private void SetBlob(bool dark, GradientStop center, GradientStop edge, string cDark, string eDark,
        string cLight, string eLight)
    {
        center.Color = C(dark ? cDark : cLight);
        edge.Color = C(dark ? eDark : eLight);
    }

    private static Color C(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush B(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private static Color HexA(string rgb, byte alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(rgb);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindAll<T>(child)) yield return d;
        }
    }

    // ============================================================
    // 状态
    // ============================================================
    private void SetState(AppState s, string? detail = null)
    {
        _state = s;
        bool dark = _theme == AppTheme.Dark;
        Brush main = dark ? B("#EAE4D7") : B("#1C1914");
        Brush muted = dark ? B("#8AEAE4D7") : B("#8A1C1914");

        switch (s)
        {
            case AppState.Idle:
                StateText.Text = "未连接";
                StateText.Foreground = muted;
                StateDot.Fill = muted;
                DeviceSub.Text = "点下方按钮开始";
                break;
            case AppState.Connecting:
                StateText.Text = "连接中…";
                StateText.Foreground = B("#E8A33D");
                StateDot.Fill = B("#E8A33D");
                DeviceSub.Text = detail ?? "正在连接…";
                break;
            case AppState.Ready:
                StateText.Text = "已连接";
                StateText.Foreground = main;
                StateDot.Fill = main;
                DeviceSub.Text = $"{_source.Label} · {_bitRate}";
                break;
            case AppState.Starting:
                StateText.Text = "启动中…";
                StateText.Foreground = B("#E8A33D");
                StateDot.Fill = B("#E8A33D");
                DeviceSub.Text = "正在拉起 scrcpy…";
                break;
            case AppState.Playing:
                StateText.Text = "正在播放";
                StateText.Foreground = main;
                StateDot.Fill = main;
                DeviceSub.Text = $"{_source.Label} · {_bitRate}";
                break;
            case AppState.Failed:
                StateText.Text = "连接失败";
                StateText.Foreground = B("#C0392B");
                StateDot.Fill = B("#C0392B");
                break;
            case AppState.Lost:
                StateText.Text = "设备已掉线";
                StateText.Foreground = B("#C0392B");
                StateDot.Fill = B("#C0392B");
                break;
        }

        bool playing = s == AppState.Playing || s == AppState.Starting;
        SetMainBtnText(playing ? "■　停止播放" : "▶　开始播放");
        MainBtn.IsEnabled = s != AppState.Connecting && s != AppState.Starting;
        MainBtn.Cursor = MainBtn.IsEnabled ? Cursors.Hand : Cursors.Arrow;

        // 播放中参数区禁用（P0-6）
        // 参数区任何状态都可编辑：连接后改码率/音源会在下一次开始时生效
        ParamsBox.IsEnabled = true;
        ParamsBox.Opacity = 1.0;
        VolumeBar.IsEnabled = s is AppState.Playing;
        VolumeBar.Opacity = VolumeBar.IsEnabled ? 1.0 : 0.45;

        RefreshTrayText();
    }

    /// <summary>在按钮的可视化树里找模板内的文字控件</summary>
    private TextBlock? FindMainBtnText()
    {
        try
        {
            MainBtn.ApplyTemplate();
            MainBtn.UpdateLayout();
            // 先在命名范围内找
            if (MainBtn.Template?.FindName("MainBtnText", MainBtn) is TextBlock t0) return t0;
            // 再遍历可视化树兜底
            return FindAll<TextBlock>(MainBtn).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>设置主按钮文字</summary>
    private void SetMainBtnText(string text)
    {
        var tb = FindMainBtnText();
        if (tb != null) { tb.Text = text; Log($"[按钮] 文字='{text}'"); }
        else Log("[按钮] 找不到文字控件");
    }

    /// <summary>设置主按钮文字颜色</summary>
    private void SetMainBtnColor(string hex)
    {
        var tb = FindMainBtnText();
        if (tb != null) tb.Foreground = B(hex);
    }

    /// <summary>播放中改了启动参数，提示需要重新开始才生效</summary>
    private void NotifyNeedRestart(string what)
    {
        Log($"[提示] 播放中修改了{what}，需重新开始才生效");
        // 用状态标签短暂变色提示，不弹窗
        var old = StateText.Text;
        StateText.Text = $"{what}已改，重开生效";
        StateText.Foreground = B("#E8A33D");
        StateDot.Fill = B("#E8A33D");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            SetState(_state);   // 恢复原状态显示
        };
        t.Start();
    }

    private void ShowError(string title, string detail, string suggest)
    {
        ErrTitle.Text = title;
        ErrDetail.Text = suggest.Length > 0 ? $"{detail} {suggest}" : detail;
        ErrorBar.Visibility = Visibility.Visible;
        StateTag.Visibility = Visibility.Collapsed;
        SetState(AppState.Failed);
        Log($"[错误] {title} {detail}");
    }

    private void ClearError()
    {
        ErrorBar.Visibility = Visibility.Collapsed;
        StateTag.Visibility = Visibility.Visible;
    }

    // ============================================================
    // 主按钮 / 播放（对应流程图主链）
    // ============================================================
    private bool _busy;   // 防止按钮被连点导致重复启动/停止

    private async void MainBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        MainBtn.IsEnabled = false;
        try
        {
            if (_state == AppState.Playing) { StopPlayback(userStopped: true); return; }
            if (_state is AppState.Connecting or AppState.Starting) return;
            await StartAsync();
        }
        finally
        {
            _busy = false;
            MainBtn.IsEnabled = true;
        }
    }

    private async Task StartAsync()
    {
        ClearError();
        if (!_adb.Available)
        {
            ShowError("找不到 adb.exe", $"请放回 {_root}", "");
            return;
        }

        var addr = _address.Trim();
        if (addr.Length == 0)
        {
            ShowError("手机地址为空", "", "请填写 IP:端口");
            return;
        }

        SetState(AppState.Connecting, $"正在连接 {addr} …");

        // 1) 连接
        var (ok, st, raw) = await _adb.ConnectAsync(addr);
        if (!ok)
        {
            HandleConnectFailure(st, raw);
            return;
        }
        _serial = AdbService.NormalizeAddress(addr);

        SetState(AppState.Ready);

        // 3) 拉起 scrcpy
        await SpawnScrcpyAsync();
    }

    private void HandleConnectFailure(string stage, AdbResult raw)
    {
        switch (stage)
        {
            case "timeout":
                ShowError("手机没有响应", "连接超时（8 秒）。", "确认手机和电脑在同一个 WiFi。");
                break;
            case "unauthorized":
                ShowError("手机还没允许这台电脑调试", "", "请在手机屏幕上点「允许 USB 调试」。");
                break;
            case "offline":
                ShowError("手机显示为离线", "", "点「连接并开始」重试，或重新打开手机无线调试。");
                break;
            case "notfound":
                ShowError("连不上 " + _address, "目标计算机积极拒绝或无此设备。", "确认同 WiFi、手机没熄屏、IP 没变。");
                break;
            default:
                ShowError("连接失败（" + stage + "）", raw.Output.Trim(), "点「连接并开始」重试。");
                break;
        }
    }

    private async Task SpawnScrcpyAsync()
    {
        SetState(AppState.Starting);
        var exe = IOPath.Combine(_root, "scrcpy.exe");
        if (!File.Exists(exe))
        {
            ShowError("找不到 scrcpy.exe", $"请放回 {_root}", "");
            return;
        }

        var args = new StringBuilder();
        args.Append("-s ").Append(Q(_serial));
        args.Append(" --no-video");
        args.Append(" --audio-source=").Append(_source.Value);
        args.Append(" --audio-bit-rate=").Append(Q(_bitRate));
        args.Append(" --require-audio");
        // 不显示 scrcpy 自己的窗口：音频照常转发，只由本面板控制
        args.Append(" --no-window");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args.ToString(),
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var log = new StringBuilder();
        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, ev) => { if (ev.Data != null) lock (log) log.AppendLine(ev.Data); };
            p.ErrorDataReceived += (_, ev) => { if (ev.Data != null) lock (log) log.AppendLine(ev.Data); };
            p.Exited += (_, _) => Dispatcher.BeginInvoke(() => OnScrcpyExited(p, log));
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _scrcpy = p;

            // 1.8 秒内退出视为启动失败
            bool exitedEarly;
            try
            {
                using var cts = new CancellationTokenSource(1800);
                await p.WaitForExitAsync(cts.Token);
                exitedEarly = true;
            }
            catch (OperationCanceledException)
            {
                exitedEarly = false;
            }

            if (exitedEarly && _scrcpy == p)
            {
                string text;
                lock (log) text = log.ToString();
                _scrcpy = null;
                ShowError("投屏进程异常退出", Hint(text), "");
                return;
            }
            if (_scrcpy != p) return; // 已被用户停止

            _startedAt = DateTime.Now;
            ClockText.Text = "00:00";
            SetState(AppState.Playing);
            Log($"已开始播放：{args}");
            _ = ApplyVolumeWithRetryAsync();
        }
        catch (Exception ex)
        {
            _scrcpy = null;
            ShowError("启动失败", ex.Message, "");
        }
    }

    private void OnScrcpyExited(Process p, StringBuilder log)
    {
        if (_scrcpy != p) return;   // 用户主动停止，已处理
        _scrcpy = null;
        string text;
        lock (log) text = log.ToString();
        ShowError("播放中断", Hint(text), "点「开始播放」重试。");
        SetState(AppState.Lost);
        ErrorBar.Visibility = Visibility.Visible;
        StateTag.Visibility = Visibility.Collapsed;
    }

    private void StopPlayback(bool userStopped)
    {
        var p = _scrcpy;
        _scrcpy = null;

        // 先复位界面，避免界面线程被进程退出等待卡住（会导致按钮点不动/状态不刷新）
        ClockText.Text = "00:00";
        if (userStopped)
        {
            ClearError();
            // 停止 = 断开连接：清空已连接标记，状态回「未连接」
            var oldSerial = _serial;
            _serial = "";
            SetState(AppState.Idle);
            Log($"[停止] 已断开 {oldSerial}");
        }
        else
        {
            SetState(_serial.Length > 0 ? AppState.Ready : AppState.Idle);
        }

        if (p == null) return;

        // 进程收尸放到后台线程，界面不受影响
        Task.Run(() =>
        {
            try
            {
                if (!p.HasExited)
                {
                    try { p.CloseMainWindow(); } catch { }
                    if (!p.WaitForExit(2000))
                    {
                        try { p.Kill(true); p.WaitForExit(1500); } catch { }
                    }
                }
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        });
    }

    private static string Q(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;

    private static string Hint(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (t.Contains("could not find any") || t.Contains("no devices"))
            return "找不到设备，请重新连接。";
        if (t.Contains("unauthorized") || t.Contains("authoriz"))
            return "手机未授权。";
        if (t.Contains("connection refused") || t.Contains("cannot connect"))
            return "连不上手机。";
        if (t.Contains("device offline"))
            return "手机离线。";
        if (t.Contains("audio") && (t.Contains("fail") || t.Contains("error")))
            return "音频捕获失败，建议把音源改成「仅媒体播放」或「手机麦克风」。";
        if (t.Contains("adb server"))
            return "adb 服务异常。";
        var lines = (text ?? "").Replace("\r", "").Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var s = lines[i].Trim();
            if (s.Length > 0 && !s.StartsWith("INFO:")) return "scrcpy: " + s;
        }
        return "scrcpy 意外退出。";
    }

    // ============================================================
    // 计时
    // ============================================================
    private void TickTimer_Tick(object? sender, EventArgs e)
    {
        if (_state == AppState.Playing)
        {
            var t = DateTime.Now - _startedAt;
            ClockText.Text = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    private void PulseDot()
    {
        if (_state is AppState.Connecting or AppState.Starting)
        {
            StateDot.Opacity = StateDot.Opacity < 0.5 ? 1.0 : 0.35;
        }
        else StateDot.Opacity = 1.0;
    }

    // ============================================================
    // 音量（D-09 / D-16）
    // ============================================================
    private void UpdateVolumeVisual()
    {
        double pct = _volume / 100.0;
        double w = VolTrack.ActualWidth;
        if (w <= 10) w = Math.Max(60, VolTrack.ActualWidth);
        if (w <= 10) w = 120;
        VolFill.Width = Math.Max(0, w * pct);
        VolKnob.Margin = new Thickness(Math.Max(0, w * pct), 0, 0, 0);
        VolPct.Text = $"{(int)Math.Round(pct * 100)}%";
        SetVolIcon(_volume == 0 ? "🔇" : _volume < 34 ? "🔉" : _volume < 67 ? "🔊" : "📢");
    }

    /// <summary>设置喇叭图标（模板内部元素，需运行时查找）</summary>
    private void SetVolIcon(string glyph)
    {
        var tb = FindInButton<TextBlock>(VolIconBtn, "VolIcon");
        if (tb != null) tb.Text = glyph;
    }

    /// <summary>通用：在按钮模板里找控件，先命名范围再遍历可视化树</summary>
    private static T? FindInButton<T>(Button btn, string name) where T : FrameworkElement
    {
        try
        {
            btn.ApplyTemplate();
            btn.UpdateLayout();
            if (btn.Template?.FindName(name, btn) is T t0) return t0;
            foreach (var c in FindAll<T>(btn))
                if (c.Name == name) return c;
        }
        catch { }
        return null;
    }

    private async void VolIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_volume == 0)
        {
            _volume = _lastVolume > 0 ? _lastVolume : 100;
            _muted = false;
        }
        else
        {
            _lastVolume = _volume;
            _volume = 0;
            _muted = true;
        }
        UpdateVolumeVisual();
        await PushVolumeAsync();
    }

    private async void VolMinus_Click(object sender, RoutedEventArgs e)
    {
        _volume = Math.Max(0, _volume - 5); _muted = _volume == 0;
        UpdateVolumeVisual();
        await PushVolumeAsync();
    }

    private async void VolPlus_Click(object sender, RoutedEventArgs e)
    {
        _volume = Math.Min(100, _volume + 5); _muted = false;
        UpdateVolumeVisual();
        await PushVolumeAsync();
    }

    private bool _dragging;

    private void VolTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        VolTrack.CaptureMouse();
        SetVolumeFromPoint(e.GetPosition(VolTrack).X);
        e.Handled = true;
    }

    private void VolTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        SetVolumeFromPoint(e.GetPosition(VolTrack).X);
    }

    private void VolTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        VolTrack.ReleaseMouseCapture();
        _ = PushVolumeAsync();
        e.Handled = true;
    }

    private void SetVolumeFromPoint(double x)
    {
        double w = VolTrack.ActualWidth;
        if (w <= 0) return;
        int v = (int)Math.Round(Math.Clamp(x / w, 0, 1) * 100);
        if (v == _volume) return;
        _volume = v; _muted = v == 0;
        UpdateVolumeVisual();
    }

    /// <summary>
    /// 把滑杆值推送到电脑侧音量（Core Audio，作用于 scrcpy 进程的音频会话）。
    /// 已弃用 adb 控手机音量：部分国产 ROM（如 OriginOS）实测 media / media_session / keyevent 三条路都被拦。
    /// </summary>
    private Task PushVolumeAsync()
    {
        var p = _scrcpy;
        if (p == null || p.HasExited) return Task.CompletedTask;
        try
        {
            float level = _volume / 100f;
            bool ok = WindowsVolume.SetProcessVolume(p.Id, level, _volume == 0);
            Log($"[音量] 电脑侧 level={level:0.00} 成功={ok}");
            if (!ok) _volumeProbeTries++;
        }
        catch (Exception ex) { Log("[音量] " + ex.Message); }
        return Task.CompletedTask;
    }

    private int _volumeProbeTries;

    /// <summary>
    /// scrcpy 刚启动时音频会话还没建立，需要轮询等待后再应用一次音量。
    /// </summary>
    private async Task ApplyVolumeWithRetryAsync()
    {
        var p = _scrcpy;
        if (p == null) return;
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(250);
            if (_scrcpy != p || p.HasExited) return;
            float level = _volume / 100f;
            if (WindowsVolume.SetProcessVolume(p.Id, level, _volume == 0))
            {
                // 成功后反向读一次，确保真的生效
                float actual = WindowsVolume.GetProcessVolume(p.Id);
                Log($"[音量] 已应用 {level:0.00}，读回 {actual:0.00}");
                return;
            }
        }
        Log("[音量] 等待 3 秒仍找不到 scrcpy 音频会话");
    }

    // ============================================================
    // 参数编辑（D-17 / D-18 / D-19）
    // ============================================================
    private void RowAddr_Click(object sender, MouseButtonEventArgs e)
    {
        AddrEdit.Text = _address;
        AddrText.Visibility = Visibility.Collapsed;
        AddrEdit.Visibility = Visibility.Visible;
        AddrEdit.Focus();
        AddrEdit.SelectAll();
    }

    private void AddrEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitAddress(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            AddrEdit.Visibility = Visibility.Collapsed;
            AddrText.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void AddrEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AddrEdit.Visibility == Visibility.Visible) CommitAddress();
    }

    private void CommitAddress()
    {
        var v = AdbService.NormalizeAddress(AddrEdit.Text);
        if (v.Length > 0) _address = v;
        AddrText.Text = _address;
        AddrEdit.Visibility = Visibility.Collapsed;
        AddrText.Visibility = Visibility.Visible;
        SaveSettings();
    }

    private void RowRate_Click(object sender, MouseButtonEventArgs e)
    {
        // 码率任何状态都可改；播放中改的话，提示需重新开始才生效
        var items = BitRateOption.All.Select(o => (o.Label, o.Note)).ToList();
        ShowDropdown(RowRate, items, RateText.Text, sel =>
        {
            _bitRate = sel;
            RateText.Text = sel;
            SaveSettings();
            RefreshSubText();
            if (_state == AppState.Playing) NotifyNeedRestart("码率");
        });
    }

    private void RowSrc_Click(object sender, MouseButtonEventArgs e)
    {
        // 音源任何状态都可改
        var items = AudioSource.All.Select(o => (o.Label, o.Note)).ToList();
        ShowDropdown(RowSrc, items, _source.Label, sel =>
        {
            var s = AudioSource.All.FirstOrDefault(x => x.Label == sel);
            if (s != null)
            {
                _source = s; SrcText.Text = s.Label; SaveSettings(); RefreshSubText();
                if (_state == AppState.Playing) NotifyNeedRestart("音源");
            }
        });
    }

    private void RefreshSubText()
    {
        if (_state is AppState.Ready or AppState.Playing or AppState.Starting)
            DeviceSub.Text = $"{_source.Label} · {_bitRate}";
    }

    private Popup? _openPopup;
    private Window? _popupOwner;
    private MouseButtonEventHandler? _ownerDown;

    /// <summary>关闭当前下拉</summary>
    private void CloseDropdown()
    {
        if (_openPopup != null) { _openPopup.IsOpen = false; _openPopup = null; }
        if (_popupOwner != null && _ownerDown != null)
        {
            _popupOwner.PreviewMouseLeftButtonDown -= _ownerDown;
            _ownerDown = null;
        }
        _popupOwner = null;
    }

    private void ShowDropdown(FrameworkElement anchor, List<(string label, string note)> items,
        string current, Action<string> onPick)
    {
        CloseDropdown();   // 已有打开的，先关掉（再次点击同一个即为收起）
        var pop = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,          // 自己控制关闭：避免鼠标抬起被误判为外部点击
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };

        bool dark = _theme == AppTheme.Dark;
        var panel = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(5),
            Background = dark ? B("#F21A1F26") : B("#F2FBFAF6"),
            BorderBrush = dark ? B("#26FFFFFF") : B("#221C1914"),
            BorderThickness = new Thickness(1),
            MinWidth = 200,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black
            }
        };

        var sp = new StackPanel();
        foreach (var (label, note) in items)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t1 = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = dark ? B("#D8E0EC") : B("#1C1914"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var t2 = new TextBlock
            {
                Text = note,
                FontSize = 10,
                Opacity = 0.45,
                Foreground = t1.Foreground,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(t1, 0); Grid.SetColumn(t2, 1);
            g.Children.Add(t1); g.Children.Add(t2);
            if (label == current)
                row.Background = dark ? B("#2E78AAFF") : B("#2278AAFF");

            row.Child = g;
            row.MouseEnter += (_, _) => row.Background = dark ? B("#1CFFFFFF") : B("#141C1914");
            row.MouseLeave += (_, _) => row.Background = label == current
                ? (dark ? B("#2E78AAFF") : B("#2278AAFF"))
                : Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) =>
            {
                CloseDropdown();
                onPick(label);
            };
            sp.Children.Add(row);
        }
        panel.Child = sp;
        pop.Child = panel;
        pop.IsOpen = true;
        _openPopup = pop;

        // 关键：挂"点外部关闭"必须放到当前这一轮鼠标事件处理完之后，
        // 否则本次点击会立刻把自己关掉（这正是"必须按住鼠标才显示"的根因）。
        _popupOwner = this;
        _ownerDown = (_, me) =>
        {
            if (_openPopup == null) return;
            if (IsInsideDropdown(me.GetPosition(this))) return;
            CloseDropdown();
        };
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_popupOwner != null && _ownerDown != null)
                _popupOwner.PreviewMouseLeftButtonDown += _ownerDown;
        }), DispatcherPriority.Background);
    }

    /// <summary>判断屏幕坐标是否落在下拉面板内</summary>
    private bool IsInsideDropdown(Point windowPoint)
    {
        if (_openPopup?.Child is not FrameworkElement child) return false;
        try
        {
            var topLeft = child.PointToScreen(new Point(0, 0));
            var bottomRight = child.PointToScreen(new Point(child.ActualWidth, child.ActualHeight));
            var p = PointToScreen(windowPoint);
            return p.X >= topLeft.X && p.X <= bottomRight.X
                && p.Y >= topLeft.Y && p.Y <= bottomRight.Y;
        }
        catch { return false; }
    }

    // ============================================================
    // 窗口 / 托盘（D-12）
    // ============================================================
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) return;
        try { DragMove(); } catch { }
    }

    /// <summary>
    /// 最小化：收到任务栏（左下角），点任务栏图标即可还原。不进托盘。
    /// 只有 X 关闭才进托盘。
    /// </summary>
    private void MinBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray()
    {
        _savedState = WindowState;
        ShowInTaskbar = false;
        Hide();
        RefreshTrayText();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = _savedState == WindowState.Minimized ? WindowState.Normal : _savedState;
        Activate();
        Topmost = true; Topmost = false;
    }

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "手机音频控制台",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开界面", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        var toggle = new WinForms.ToolStripMenuItem("开始播放", null, (_, _) =>
            Dispatcher.Invoke(() =>
            {
                if (_state == AppState.Playing) StopPlayback(true);
                else _ = StartAsync();
            }));
        menu.Items.Add(toggle);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("打开日志目录", null, (_, _) =>
        {
            var d = IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "手机音频控制台");
            try { Process.Start(new ProcessStartInfo(d) { UseShellExecute = true }); } catch { }
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出程序", null, (_, _) => Dispatcher.Invoke(RealExit));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        _trayMenuToggle = toggle;
    }

    private WinForms.ToolStripMenuItem? _trayMenuToggle = null;

    private static System.Drawing.Icon LoadIcon()
    {
        // 1) 自身 exe 图标
        try
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) && File.Exists(self))
            {
                var ic = System.Drawing.Icon.ExtractAssociatedIcon(self);
                if (ic != null) return ic;
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void RefreshTrayText()
    {
        if (_tray == null) return;
        var st = _state switch
        {
            AppState.Playing => "正在播放",
            AppState.Connecting or AppState.Starting => "连接中",
            AppState.Ready => "已连接",
            AppState.Failed or AppState.Lost => "已中断",
            _ => "已停止"
        };
        var t = $"手机音频控制台 - {st}";
        _tray.Text = t.Length > 63 ? t[..63] : t;
        if (_trayMenuToggle != null)
            _trayMenuToggle.Text = _state == AppState.Playing ? "停止播放" : "开始播放";
    }

    /// <summary>从任务栏还原时，确保窗口可见且处于正常状态（不是托盘隐藏态）</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && !IsVisible)
        {
            ShowInTaskbar = true;
            Show();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_realExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        CloseDropdown();
        SaveSettings();
        _tickTimer.Stop();
        _pulseTimer.Stop();
        StopPlayback(false);

        // 退出时断开本程序建立的无线连接，避免 adb 留下失效会话导致下次连接异常
        CleanupOnExit();

        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }

    /// <summary>
    /// 退出清理：只断开本程序连接过的那台设备（不 kill-server，避免影响其他 adb 使用者）。
    /// 无线连接在 adb server 重启或进程被强杀后会留下失效记录，是"下次打开连不上"的主因。
    /// </summary>
    /// <summary>
    /// 退出清理（顺序很重要）：
    /// 1) 先杀 scrcpy 子进程 —— 否则它占着音频会话，音量合成器里会一直留着 "scrcpy" 条目
    /// 2) 断开本程序的无线连接
    /// 3) 关掉本程序启动的 adb server —— 否则后台残留 adb.exe
    /// </summary>
    private void CleanupOnExit()
    {
        // 1) scrcpy：连子进程一起杀
        try
        {
            var p = _scrcpy;
            _scrcpy = null;
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { try { p.Kill(); } catch { } }
                        p.WaitForExit(2000);
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }

            // 兜底：还有同名残留就按名字清
            foreach (var r in Process.GetProcessesByName("scrcpy"))
            {
                try { r.Kill(); r.WaitForExit(1500); } catch { }
            }
            Log("[退出] scrcpy 已清理");
        }
        catch (Exception ex) { Log("[退出] scrcpy 清理异常：" + ex.Message); }

        // 2) 断开无线连接（USB 设备不需要）
        try
        {
            var addr = AdbService.NormalizeAddress(_address);
            if (addr.Length > 0 && AdbService.LooksLikeIp(addr))
            {
                var t = _adb.RunAsync($"disconnect {addr}", 3000);
                bool done = t.Wait(3500);
                Log($"[退出] adb disconnect {addr} 完成={done}");
            }
        }
        catch (Exception ex) { Log("[退出] adb disconnect 异常：" + ex.Message); }

        // 3) 关掉 adb server（只关本程序自己启动的，通过 _adbStartedServer 标记判断）
        try
        {
            if (_adbStartedServer)
            {
                var t = _adb.RunAsync("kill-server", 3000);
                bool done = t.Wait(3500);
                Log($"[退出] adb kill-server 完成={done}");
            }
            else
            {
                Log("[退出] adb server 非本程序启动，保留");
            }
        }
        catch (Exception ex) { Log("[退出] adb kill-server 异常：" + ex.Message); }
    }

    private bool _exiting;

    private void RealExit()
    {
        if (_exiting) return;
        _exiting = true;
        Log("[退出] 用户请求退出");
        _realExit = true;

        // ShutdownMode 是 OnExplicitShutdown，Close() 只会关窗口、进程不退，
        // 托盘图标和单实例 Mutex 都还在，导致下次双击提示"已经在运行"。
        // 必须显式 Shutdown() 才会走 OnExit 释放 Mutex。
        Application.Current.Shutdown();
    }

    // ============================================================
    // 设置（D-11）
    // ============================================================
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            foreach (var raw in File.ReadAllLines(_settingsPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                switch (k.ToLowerInvariant())
                {
                    case "address": _address = v; break;
                    case "bitrate": _bitRate = v; break;
                    case "source":
                        var s = AudioSource.All.FirstOrDefault(x => x.Value == v);
                        if (s != null) _source = s;
                        break;
                    case "volume":
                        if (int.TryParse(v, out var vol)) { _volume = Math.Clamp(vol, 0, 100); _lastVolume = _volume; }
                        break;
                    case "theme": _theme = v == "dark" ? AppTheme.Dark : AppTheme.Light; break;
                }
            }
        }
        catch { }

        // 同步到界面
        AddrText.Text = _address;
        RateText.Text = _bitRate;
        SrcText.Text = _source.Label;
    }

    private void SaveSettings()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 手机音频控制台 设置");
            sb.AppendLine("Address=" + _address);
            sb.AppendLine("BitRate=" + _bitRate);
            sb.AppendLine("Source=" + _source.Value);
            sb.AppendLine("Volume=" + _volume);
            sb.AppendLine("Theme=" + (_theme == AppTheme.Dark ? "dark" : "light"));
            File.WriteAllText(_settingsPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 设置存不下来不该静默：写日志，便于排查权限/路径问题
            Log("[设置] 保存失败：" + ex.Message);
        }
    }

    // ============================================================
    // 日志
    // ============================================================
    private static void Log(string msg)
    {
        try
        {
            var dir = AppDataDir;
            Directory.CreateDirectory(dir);
            var file = IOPath.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file,
                $"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 日志失败时写到程序目录兜底，不再静默
            try
            {
                File.AppendAllText(IOPath.Combine(AppContext.BaseDirectory, "app-fallback.log"),
                    $"{DateTime.Now:HH:mm:ss}  [LOG-FAIL]{ex.GetType().Name}: {ex.Message} | msg={msg}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>设置与日志目录（D-11）。首选 LocalAppData，不可写则退到程序目录。</summary>
    private static string AppDataDir
    {
        get
        {
            if (_appDataDir != null) return _appDataDir;
            string[] candidates =
            {
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "手机音频控制台"),
                IOPath.Combine(AppContext.BaseDirectory, "用户数据"),
                IOPath.Combine(IOPath.GetTempPath(), "手机音频控制台")
            };
            foreach (var d in candidates)
            {
                try
                {
                    if (string.IsNullOrEmpty(d)) continue;
                    Directory.CreateDirectory(d);
                    var probe = IOPath.Combine(d, ".w.tmp");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                    _appDataDir = d;
                    return d;
                }
                catch { }
            }
            _appDataDir = AppContext.BaseDirectory;
            return _appDataDir;
        }
    }
    private static string? _appDataDir;
}
