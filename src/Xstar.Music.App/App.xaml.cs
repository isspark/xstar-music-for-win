using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Xstar.Music.App;

public partial class App : Application
{
    private static readonly string CrashLog =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XstarMusic", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteLog(args.ExceptionObject?.ToString() ?? "unknown");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        base.OnStartup(e);
        ThemeManager.Initialize(ThemeManager.ThemeMode.System);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog(e.Exception.ToString());
        MessageBox.Show(e.Exception.Message, "XSTAR 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
