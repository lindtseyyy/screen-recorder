using System.Windows;
using System.Windows.Threading;
using ScreenRecorder.Infrastructure;

namespace ScreenRecorder;

/// <summary>Application entry point: logging setup and global exception handling.</summary>
public partial class App : Application
{
    public App()
    {
        Log.Init();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception: " + e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception: " + e.ExceptionObject);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception: " + e.Exception);
        MessageBox.Show(
            "Something went wrong:\n" + e.Exception.Message + "\n\nDetails were written to the log.",
            "Screen Recorder",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
