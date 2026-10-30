using Microsoft.UI.Xaml;

namespace SkpdbDesktop;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogStartupError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogStartupError(e.ExceptionObject as Exception ?? new Exception("unknown unhandled error"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogStartupError(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception error)
        {
            LogStartupError(error);
            throw;
        }
    }

    private static void LogStartupError(Exception error)
    {
        var text = $"{DateTime.Now:O}\n{error}\n";
        foreach (var directory in new[]
                 {
                     Path.GetTempPath(),
                     AppContext.BaseDirectory,
                     AppPaths.DataDirectory
                 })
        {
            try
            {
                File.AppendAllText(Path.Combine(directory, "skpdb_startup.log"), text);
                return;
            }
            catch
            {
                // Try the next writable location.
            }
        }
    }
}
