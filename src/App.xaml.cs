using System;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;

namespace PhoneAudioConsole;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例
        _mutex = new Mutex(false, @"Local\PhoneAudioConsole.Wpf");
        bool created;
        try { created = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { created = true; }   // 上次异常退出，接管
        if (!created)
        {
            MessageBox.Show("手机音频控制台已经在运行了。", "手机音频控制台",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 诊断：列出所有音频会话
        if (e.Args.Length > 0 && e.Args[0] == "--dump-audio")
        {
            var report = WindowsVolume.DumpSessions();
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "audio-sessions.txt");
            System.IO.File.WriteAllText(path, report, new System.Text.UTF8Encoding(false));
            System.Windows.MessageBox.Show(report.Length > 1500 ? report.Substring(0, 1500) : report, "音频会话");
            Shutdown();
            return;
        }

        var win = new MainWindow();

        MainWindow = win;
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 释放单实例锁，否则下次启动会被判定为"已经在运行"
            _mutex?.ReleaseMutex();
        }
        catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
        base.OnExit(e);
    }
}
