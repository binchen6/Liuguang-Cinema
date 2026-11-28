using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using TV.Models;
using TV.Services;

namespace TV.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services, DispatcherQueue dispatcher)
    {
        Services = services;
        Player = new PlayerViewModel(services, dispatcher);
        if (services.PlayerOnly)
        {
            Vod = null!;
            Search = null!;
            Live = null!;
            Library = null!;
            CloudDrive = null!;
            Settings = null!;
            return;
        }
        Vod = new VodViewModel(services, Play);
        Vod.DetailRequested += (_, _) => Navigate("detail");
        Live = new LiveViewModel(services, Play);
        Library = new LibraryViewModel(services, Play, Vod.OpenFavorite);
        Search = new SearchViewModel(services, card => Vod.SelectedCard = card)
        {
            SuggestionCatalog = CollectSuggestionCatalog
        };
        CloudDrive = new CloudDriveViewModel(services, Play);
        Settings = new SettingsViewModel(services);
    }

    private IEnumerable<string> CollectSuggestionCatalog()
    {
        foreach (var card in Vod.Items)
            if (!string.IsNullOrWhiteSpace(card.Item.Name)) yield return card.Item.Name;
        foreach (var card in Vod.GalleryItems)
            if (!string.IsNullOrWhiteSpace(card.Item.Name)) yield return card.Item.Name;
        foreach (var item in Library.History)
            if (!string.IsNullOrWhiteSpace(item.Title)) yield return item.Title;
        foreach (var item in Library.Favorites)
            if (!string.IsNullOrWhiteSpace(item.Title)) yield return item.Title;
        foreach (var item in Library.LocalFiles)
            if (!string.IsNullOrWhiteSpace(item.Title)) yield return item.Title;
    }

    public AppServices Services { get; }
    public VodViewModel Vod { get; }
    public SearchViewModel Search { get; }
    public LiveViewModel Live { get; }
    public PlayerViewModel Player { get; }
    public LibraryViewModel Library { get; }
    public CloudDriveViewModel CloudDrive { get; }
    public SettingsViewModel Settings { get; }
    [ObservableProperty] private string _pageTitle = "点播";
    public event EventHandler<string>? NavigationRequested;

    [RelayCommand]
    private void Navigate(string? tag)
    {
        tag ??= "vod";
        PageTitle = tag switch { "live" => "直播", "cast" => "投屏", "library" => "我的媒体库", "cloud" => "我的云盘", "settings" => "设置", "player" => "正在播放", _ => "点播" };
        NavigationRequested?.Invoke(this, tag);
    }

    internal void Play(PlaybackRequest request)
    {
        Diagnostics.Log($"Play requested: {request.Kind}; site={request.SiteKey}");
        Navigate("player");
        _ = Player.LoadAsync(request);
    }
}
