using DawnPlayer.Core.Util;
using Microsoft.UI.Xaml;

namespace DawnPlayer.App;

public partial class App : Application
{
    public static MainWindow? MainWin { get; private set; }

    public App()
    {
        InitializeComponent();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"[AppDomain Unhandled] {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"[UnobservedTask] {e.Exception}");
            e.SetObserved();
        };
        UnhandledException += (_, e) =>
        {
            Log($"[Unhandled] {e.Exception}");
            e.Handled = true;
            // A swallowed exception with no user signal leaves a silently half-broken window;
            // the InfoBar pipeline is safe to poke even before Initialize (RunOnUi null-conditions).
            try
            {
                Services.AppServices.RaiseWarning(
                    $"예기치 않은 오류가 발생했습니다: {e.Exception?.Message}\n자세한 내용은 로그를 확인하세요.");
            }
            catch { }
        };
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched");
        try
        {
            MainWin = new MainWindow();
            MainWin.Activate();
            Log("MainWindow activated");
        }
        catch (Exception ex)
        {
            // Rethrowing here reached UnhandledException, which marks everything handled, so a
            // startup failure left a running process with no window: nothing to see, nothing to
            // click, and no hint about what went wrong. Tell the user and exit instead.
            Log($"[FATAL OnLaunched] {ex}");
            try
            {
                var nl = Environment.NewLine;
                MessageBox(IntPtr.Zero,
                    $"Dawn Player를 시작할 수 없습니다.{nl}{nl}{ex.Message}{nl}{nl}자세한 내용: {AppPaths.LogFile}",
                    "Dawn Player", 0x00000010 /* MB_ICONERROR */);
            }
            catch { }
            Environment.Exit(1);
        }
    }
}
