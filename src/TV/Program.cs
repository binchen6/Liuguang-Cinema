using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Security.Cryptography;
using System.Text;
using TV.Services;
using TV.Services.Library;
using TV.Services.Persistence;

namespace TV;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var startupVideo = VideoArgument(args);
        AppInstance? instance = null;
        try
        {
            var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Root.ToUpperInvariant())));
            instance = AppInstance.FindOrRegisterForKey("TV-" + profile);
            if (!instance.IsCurrent)
            {
                try
                {
                    Task.Run(async () =>
                    {
                        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                        await instance.RedirectActivationToAsync(activation).AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                    }).GetAwaiter().GetResult();
                }
                catch (Exception ex) { Diagnostics.Log($"窗口唤回失败: {ex.Message}"); }
                return;
            }
        }
        catch (Exception ex)
        {
            // Fallback gracefully on restricted/older Windows environments where AppLifecycle single-instance IPC is unavailable.
            Diagnostics.Log($"单实例注册降级继续启动: {ex.GetType().Name} {ex.Message}");
            instance = null;
        }

        App.PlayerOnly = startupVideo is not null;
        App.StartupVideoPath = startupVideo;
        Application.Start(parameters =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            if (instance is not null)
            {
                instance.Activated += (_, activation) => dispatcher.TryEnqueue(() =>
                {
                    if (VideoArgument(activation) is { } path) App.OpenLocal(path);
                    else App.Window?.RestoreWindow();
                });
            }
            _ = new App();
        });
    }

    private static string? VideoArgument(AppActivationArguments activation)
    {
        if (activation.Data is not Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch) return null;
        var argument = launch.Arguments.Trim();
        if (argument.StartsWith('"') && argument.IndexOf('"', 1) is > 0 and var closing) argument = argument[1..closing];
        else if (!argument.StartsWith('"')) argument = argument.Split(' ', 2)[0];
        return VideoArgument([argument]);
    }

    private static string? VideoArgument(IEnumerable<string> args)
    {
        var path = args.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) && LocalMediaService.IsVideo(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
