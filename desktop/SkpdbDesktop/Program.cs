using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SkpdbDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Driver components such as NVIDIA Game Proxy are vendor-signed.
        // Do not impose a Microsoft-only image policy on the desktop client.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
