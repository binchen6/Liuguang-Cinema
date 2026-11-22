using Microsoft.UI.Xaml;
using TV.Services;
using TV.Services.Persistence;

namespace TV;

public sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Diagnostics.Log(args.ExceptionObject?.ToString() ?? "Unknown unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Diagnostics.Log($"未观察的后台任务异常: {args.Exception?.GetBaseException().GetType().Name} {args.Exception?.GetBaseException().Message}");
            args.SetObserved();
        };
    }

    public static AppServices Services { get; private set; } = null!;
    public static bool PlayerOnly { get; internal set; }
    public static string? StartupVideoPath { get; internal set; }
    internal static MainWindow Window { get; private set; } = null!;

    internal static void OpenLocal(string path)
    {
        StartupVideoPath = path;
        Window?.OpenLocal(path);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = new AppServices(PlayerOnly);
        Diagnostics.Log("Application launch" + (PlayerOnly ? " (direct local player)" : string.Empty));
        _ = Task.Run(async () =>
        {
            try { await Services.Database.InitializeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Diagnostics.Log($"启动数据库读取失败: {ex.Message}"); }
        });
        Window = new MainWindow();
        Window.Activate();
        Diagnostics.Log("Window activated");
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            var path = Path.Combine(AppPaths.Root, "last-error.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {e.Exception}\n");
        }
        catch { }
    }
}
