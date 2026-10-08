using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AntigravityQuotaWidget;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\AntigravityQuotaWidget_SingleInstance";
    private const string EventName = @"Local\AntigravityQuotaWidget_ShowEvent";
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_startup.log");

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");
        Log("=== App.OnStartup Started ===");

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log($"[CRASH] UnhandledException: {args.ExceptionObject}");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Log($"[CRASH] DispatcherUnhandledException: {args.Exception.Message}\r\n{args.Exception.StackTrace}");
        };

        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            Log($"Mutex created. createdNew={createdNew}");

            if (!createdNew)
            {
                Log("Another instance is running. Signaling existing instance and exiting.");
                try
                {
                    using var showEvt = EventWaitHandle.OpenExisting(EventName);
                    showEvt.Set();
                }
                catch (Exception ex)
                {
                    Log($"Failed to signal showEvt: {ex.Message}");
                }

                Shutdown();
                return;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        Log("Received showEvent signal on background thread.");
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow is MainWindow mw)
                            {
                                mw.BringToFront();
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"showEvent thread error: {ex.Message}");
                        break;
                    }
                }
            })
            {
                IsBackground = true
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            Log($"Mutex initialization exception: {ex.Message}");
        }

        base.OnStartup(e);
        Log("App.OnStartup Completed.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"App.OnExit called with ExitCode={e.ApplicationExitCode}");
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch { }
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
