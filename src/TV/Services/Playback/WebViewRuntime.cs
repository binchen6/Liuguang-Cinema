using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using TV.Services.Persistence;

namespace TV.Services.Playback;

internal sealed class WebViewRuntimeUnavailableException(Exception inner) :
    NotSupportedException("此页面需要 Microsoft Edge WebView2 运行时。请联网后重试，或安装官方 Evergreen 运行时。", inner);

internal static class WebViewRuntime
{
    private static readonly object InstallLock = new();
    private static Task<bool>? _install;
    private static long _lastAttempt;

    internal static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        try { return await CreateAsync(); }
        catch (Exception ex) when (ex is FileNotFoundException || ex.HResult == unchecked((int)0x80070002))
        {
            Task<bool> install;
            lock (InstallLock)
            {
                var now = Environment.TickCount64;
                if (_install is null || _install.IsCompletedSuccessfully && !_install.Result && now - _lastAttempt >= 60_000)
                {
                    _lastAttempt = now;
                    _install = InstallAsync();
                }
                install = _install;
            }
            if (!await install) throw new WebViewRuntimeUnavailableException(ex);
            try { return await CreateAsync(); }
            catch (Exception retry) when (retry is FileNotFoundException || retry.HResult == unchecked((int)0x80070002))
            {
                throw new WebViewRuntimeUnavailableException(retry);
            }
        }
    }

    private static async Task<CoreWebView2Environment> CreateAsync() =>
        await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(AppPaths.Root, "webview2"),
            new CoreWebView2EnvironmentOptions());

    private static async Task<bool> InstallAsync()
    {
        var bootstrapper = Path.Combine(AppContext.BaseDirectory, "runtime", "MicrosoftEdgeWebView2Setup.exe");
        if (!File.Exists(bootstrapper)) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = bootstrapper,
                Arguments = "/silent /install",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return false;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            Diagnostics.Log($"WebView2 bootstrapper exit code: {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"WebView2 runtime setup did not complete: {ex.GetType().Name}");
            return false;
        }
    }
}
