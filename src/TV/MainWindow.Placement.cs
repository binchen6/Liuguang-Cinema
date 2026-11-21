using Microsoft.UI.Windowing;
using Windows.Graphics;
using System.Text.Json;
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
        var saved = _placement;
        var display = saved is null ? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary) :
            DisplayArea.GetFromPoint(new PointInt32(saved.ScreenX, saved.ScreenY), DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        int width = Math.Clamp((int)Math.Round(work.Width * (saved?.Width ?? .55)), Math.Min(saved is null ? 820 : 360, work.Width), work.Width);
        int height = Math.Clamp((int)Math.Round(work.Height * (saved?.Height ?? .55)), Math.Min(saved is null ? 600 : 280, work.Height), work.Height);
        int left = saved is null ? (work.Width - width) / 2 : Math.Clamp((int)Math.Round(saved.Left * work.Width), 0, work.Width - width);
        int top = saved is null ? (work.Height - height) / 2 : Math.Clamp((int)Math.Round(saved.Top * work.Height), 0, work.Height - height);
        AppWindow.MoveAndResize(new RectInt32(display.OuterBounds.X + work.X + left, display.OuterBounds.Y + work.Y + top, width, height));
        CapturePlacement();
        if (saved?.Maximized == true && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    private void CapturePlacement()
    {
        if (_closing || _compact || _fullScreen || AppWindow.Presenter is not OverlappedPresenter presenter) return;
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
            (double)(pos.X - screen.X - work.X) / work.Width, (double)(pos.Y - screen.Y - work.Y) / work.Height,
            (double)size.Width / work.Width, (double)size.Height / work.Height, false);
    }

    private void SavePlacement()
    {
        if (_placement is null) return;
        try
        {
            File.WriteAllText(PlacementFile + ".tmp", JsonSerializer.Serialize(_placement));
            File.Move(PlacementFile + ".tmp", PlacementFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
