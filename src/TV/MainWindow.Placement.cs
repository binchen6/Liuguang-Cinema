using Microsoft.UI.Windowing;
using Windows.Graphics;
using System.Text.Json;
using TV.Services;
using TV.Services.Persistence;

namespace TV;

public sealed partial class MainWindow
{
    private sealed record Placement(int ScreenX, int ScreenY, double Left, double Top, double Width, double Height, bool Maximized);
    private Placement? _placement;
    private static string PlacementFile => Path.Combine(AppPaths.Root, "window.json");

    private void RestorePlacement()
    {
        try
        {
            if (File.Exists(PlacementFile)) _placement = JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementFile));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        try
        {
            var saved = _placement;
            var display = saved is null
                ? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
                : DisplayArea.GetFromPoint(new PointInt32(saved.ScreenX, saved.ScreenY), DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            if (work.Width <= 0 || work.Height <= 0) return;

            var ratioW = saved is { Width: > 0.1 and <= 1.0 } ? saved.Width : 0.62;
            var ratioH = saved is { Height: > 0.1 and <= 1.0 } ? saved.Height : 0.68;
            int width = Math.Clamp((int)Math.Round(work.Width * ratioW), Math.Min(saved is null ? 920 : 360, work.Width), work.Width);
            int height = Math.Clamp((int)Math.Round(work.Height * ratioH), Math.Min(saved is null ? 640 : 280, work.Height), work.Height);
            int left = saved is null ? (work.Width - width) / 2 : Math.Clamp((int)Math.Round(saved.Left * work.Width), 0, Math.Max(0, work.Width - width));
            int top = saved is null ? (work.Height - height) / 2 : Math.Clamp((int)Math.Round(saved.Top * work.Height), 0, Math.Max(0, work.Height - height));

            // DisplayArea.WorkArea.X / Y are already in virtual screen coordinates (must NOT add OuterBounds.X/Y again).
            AppWindow.MoveAndResize(new RectInt32(work.X + left, work.Y + top, width, height));
            CapturePlacement();
            if (saved?.Maximized == true && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"窗口位置恢复失败: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void CapturePlacement()
    {
        if (_closing || _compact || _fullScreen || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        try
        {
            if (presenter.State == OverlappedPresenterState.Maximized && _placement is not null)
                _placement = _placement with { Maximized = true };
            if (presenter.State != OverlappedPresenterState.Restored) return;
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            var screen = display.OuterBounds;
            var size = AppWindow.Size;
            var pos = AppWindow.Position;
            if (work.Width <= 0 || work.Height <= 0 || size.Width < 200 || size.Height < 150 || pos.X <= -30000) return;
            _placement = new(screen.X + screen.Width / 2, screen.Y + screen.Height / 2,
                (double)(pos.X - work.X) / work.Width, (double)(pos.Y - work.Y) / work.Height,
                (double)size.Width / work.Width, (double)size.Height / work.Height, false);
        }
        catch { }
    }

    private void SavePlacement()
    {
        if (_placement is null) return;
        try
        {
            AppPaths.EnsureCreated();
            var temp = PlacementFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_placement));
            File.Move(temp, PlacementFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
