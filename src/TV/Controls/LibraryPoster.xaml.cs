using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TV.Services;

namespace TV.Controls;

public sealed partial class LibraryPoster : UserControl
{
    private CancellationTokenSource? _load;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(LibraryPoster), new PropertyMetadata("", OnArtworkChanged));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(LibraryPoster), new PropertyMetadata(""));
    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(nameof(Picture), typeof(string), typeof(LibraryPoster), new PropertyMetadata(null, OnArtworkChanged));
    public static readonly DependencyProperty LocalPathProperty = DependencyProperty.Register(nameof(LocalPath), typeof(string), typeof(LibraryPoster), new PropertyMetadata(null, OnArtworkChanged));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(LibraryPoster), new PropertyMetadata(0d));
    public static readonly DependencyProperty HasProgressProperty = DependencyProperty.Register(nameof(HasProgress), typeof(bool), typeof(LibraryPoster), new PropertyMetadata(false));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? Picture { get => (string?)GetValue(PictureProperty); set => SetValue(PictureProperty, value); }
    public string? LocalPath { get => (string?)GetValue(LocalPathProperty); set => SetValue(LocalPathProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool HasProgress { get => (bool)GetValue(HasProgressProperty); set => SetValue(HasProgressProperty, value); }

    public LibraryPoster() => InitializeComponent();

    private static void OnArtworkChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is LibraryPoster { IsLoaded: true } card) card.QueueArtwork();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => QueueArtwork();
    private void OnUnloaded(object sender, RoutedEventArgs e) => _load?.Cancel();

    private void QueueArtwork()
    {
        _load?.Cancel();
        _load = new CancellationTokenSource();
        Artwork.Source = null;
        TitleBlock.Text = Title;
        _ = LoadArtworkAsync(_load);
    }

    private async Task LoadArtworkAsync(CancellationTokenSource work)
    {
        using (work)
        {
            var token = work.Token;
            try
            {
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                var services = App.Services;
                var picture = Picture;
                if (LocalPath is { Length: > 0 } path)
                {
                    var info = await services.LocalArtwork.ReadAsync(path, Title, token, coverOnly: true);
                    token.ThrowIfCancellationRequested();
                    TitleBlock.Text = info.Title;
                    if (info.Picture.Length > 0) picture = info.Picture;
                }
                var bitmap = await services.Images.GetImageAsync(picture, token);
                if (!token.IsCancellationRequested) Artwork.Source = bitmap;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Diagnostics.Log("媒体库封面: " + ex.GetType().Name); }
            finally { if (ReferenceEquals(_load, work)) _load = null; }
        }
    }
}
