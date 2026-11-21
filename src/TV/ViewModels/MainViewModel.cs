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
        Search = new SearchViewModel(services, card => Vod.SelectedCard = card) { SuggestionCatalog = () => Vod.Items.Select(card => card.Item.Name) };
        Vod.DetailRequested += (_, _) => Navigate("detail");
        Live = new LiveViewModel(services, Play);
        Library = new LibraryViewModel(services, Play, Vod.OpenFavorite);
        CloudDrive = new CloudDriveViewModel(services, Play);
        Settings = new SettingsViewModel(services);
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

    private void Play(PlaybackRequest request)
    {
        Diagnostics.Log($"Play requested: {request.Kind}; site={request.SiteKey}");
        Navigate("player");
        _ = Player.LoadAsync(request);
    }
}
