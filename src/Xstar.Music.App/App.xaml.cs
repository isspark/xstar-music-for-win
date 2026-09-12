using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Xstar.Music.App;

public partial class App : Application
{
    private static readonly string CrashLog =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XstarMusic", "crash.log");
    private static readonly string StartupLog = Path.Combine(Path.GetTempPath(), "XstarStartup.log");
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        File.AppendAllText(StartupLog, $"[{DateTime.Now:O}] OnStartup begin, proc={Environment.ProcessId}\n");
        try
        {
            // Enforce single instance. Two processes opening the same SQLite DB at once
            // causes Error 14 (unable to open) / lock contention. A Global mutex is
            // auto-released on process death, so it is never truly orphaned — "denied"
            // here always means another live instance, and we must not proceed.
            var mutexName = "Global\\XstarMusic.SingleInstance";
            bool created;
            _singleInstanceMutex = new Mutex(true, mutexName, out created);
            if (!created)
            {
                File.AppendAllText(StartupLog, $"[{DateTime.Now:O}] Single-instance denied by a live copy. Shutting down.\n");
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteLog(args.ExceptionObject?.ToString() ?? "unknown");
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            base.OnStartup(e);
            ThemeManager.Initialize(ThemeManager.ThemeMode.System);
            File.AppendAllText(StartupLog, $"[{DateTime.Now:O}] OnStartup OK\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(StartupLog, $"[{DateTime.Now:O}] OnStartup FAIL\n{ex}\n");
            WriteLog($"OnStartup FAIL\n{ex}");
            MessageBox.Show($"启动失败：{ex.Message}\n\n{ex}", "XSTAR 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        File.AppendAllText(StartupLog, $"[{DateTime.Now:O}] OnExit\n");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog(e.Exception.ToString());
        File.AppendAllText(StartupLog, $"[{DateTime.Now:O}] DispatcherUnhandledException\n{e.Exception}\n");
        MessageBox.Show(e.Exception.ToString(), "XSTAR 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteLog(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, $"[{DateTime.Now:O}]\n{text}\n\n");
        }
        catch { }
    }
}
