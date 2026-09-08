using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WpfApp1.Models;

namespace WpfApp1
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private static readonly Lock _logLock = new();

        public static CommandLineOptions CliOptions { get; private set; } = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. Single-Instance Enforcement to prevent concurrent Hyper-V command collisions
            const string mutexName = @"Global\WinMigratePro_ElectroMU_SingleInstance";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool isOnlyInstance);

            if (!isOnlyInstance)
            {
                MessageBox.Show(
                    "Another instance of WinMigrate Pro is already running on this workstation.\n\nPlease close the existing session before launching a new one to prevent conflicting Hyper-V orchestration sessions.",
                    "Single Instance Guard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            // 2. Parse incoming CLI switches (Windows Admin Center or automation runbooks)
            if (e.Args.Length > 0)
            {
                CliOptions = CommandLineOptions.Parse(e.Args);

                if (CliOptions.ShowHelp)
                {
                    MessageBox.Show(
                        CommandLineOptions.GetUsageHelp(),
                        "WinMigrate Pro - Command-Line Help",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    Shutdown(0);
                    return;
                }
            }

            // 3. Intercept UI Thread Dispatcher Exceptions
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // 4. Intercept Background AppDomain Exceptions
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // 5. Intercept Async Task Worker Unobserved Exceptions
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            LogStartupDiagnostics();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                    _singleInstanceMutex.Dispose();
                }
                catch { }
            }

            base.OnExit(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogAndNotify("UI Dispatcher", e.Exception, showModal: true);
            e.Handled = true; // Prevents process crash
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogAndNotify("AppDomain Core", ex, showModal: false);
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved(); // Acknowledges task to prevent finalizer abort
            LogAndNotify("Async Task Worker", e.Exception, showModal: false);
        }

        private static void LogAndNotify(string subsystem, Exception ex, bool showModal)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "WinMigrate_ErrorLog.txt");
            var trace = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{subsystem}] {ex.Message}\n{ex.StackTrace}\n\n";

            lock (_logLock)
            {
                try
                {
                    // Rotate log if it exceeds 5 MB
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
                    {
                        var backupPath = Path.Combine(Path.GetTempPath(), "WinMigrate_ErrorLog.bak");
                        File.Move(logPath, backupPath, overwrite: true);
                    }

                    File.AppendAllText(logPath, trace);
                }
                catch { }
            }

            if (showModal)
            {
                MessageBox.Show(
                    $"WinMigrate Pro intercepted an unexpected error in [{subsystem}]:\n\n{ex.Message}\n\nDiagnostic records written to:\n{logPath}",
                    "System Diagnostics Notice",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static void LogStartupDiagnostics()
        {
            var logPath = Path.Combine(Path.GetTempPath(), "WinMigrate_ErrorLog.txt");
            var info = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [STARTUP] WinMigrate Pro (ElectroMU Edition) Initialized. OS: {Environment.OSVersion}, 64-Bit: {Environment.Is64BitProcess}\n";

            lock (_logLock)
            {
                try
                {
                    File.AppendAllText(logPath, info);
                }
                catch { }
            }
        }
    }
}