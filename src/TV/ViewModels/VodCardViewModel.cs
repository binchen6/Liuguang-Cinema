using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using TV.Models;
using TV.Services;

namespace TV.ViewModels;

public sealed partial class VodCardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private Task? _visualLoad;
    private int _searchSourceCount = 1;

    public VodCardViewModel(VodItem item, AppServices services)
    {
        Item = item;
        _services = services;
    }

    public VodItem Item { get; private set; }
    [ObservableProperty] private BitmapImage? _poster;
    [ObservableProperty] private string _rating = string.Empty;
    [ObservableProperty] private bool _isMetadataLoading;
    internal double SearchScore { get; init; }
    internal IReadOnlyList<VodCardViewModel> MatchedSources { get; set; } = [];
    public string SourceName => Item.SiteName;
    public string SearchSourceLabel => SearchSourceCount > 1 ? $"{SearchSourceCount} 个片源" : SourceName;
    public int SearchSourceCount
    {
        get => _searchSourceCount;
        internal set
        {
            if (SetProperty(ref _searchSourceCount, value)) OnPropertyChanged(nameof(SearchSourceLabel));
        }
    }
    public string SearchMetadata => string.Join(" · ", new[] { Item.Year, Item.TypeName, Item.Remarks }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private bool _priorityLoaded;

    public Task LoadVisualAsync(bool priority = false)
    {
        if (_visualLoad is not null && (!priority || _priorityLoaded)) return _visualLoad;
        if (priority) _priorityLoaded = true;
        _visualLoad = LoadCoreAsync(priority);
        return _visualLoad;
    }

    public void ReplaceItem(VodItem item)
    {
        Item = item;
        Rating = item.Rating ?? string.Empty;
        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(SearchMetadata));
    }

    private async Task LoadCoreAsync(bool priority)
    {
        var primaryUrl = Item.BestPicture;
        if (Poster is null || priority)
        {
            var fetched = await _services.Images.GetImageAsync(primaryUrl, highRes: priority);
            if (fetched is not null) Poster = fetched;
        }
        Rating = Item.Rating ?? string.Empty;
        if (!priority && Poster is not null && !string.IsNullOrWhiteSpace(Rating)) return;
        IsMetadataLoading = true;
        try
        {
            var metadata = priority ? await _services.Douban.GetMetadataAsync(Item) : await _services.Douban.GetCachedMetadataAsync(Item);
            if (metadata is null) return;
            Item.DoubanId = metadata.SubjectId;
            if (Poster is null && !string.IsNullOrWhiteSpace(metadata.Picture))
            {
                Item.DoubanPicture = metadata.Picture;
                Poster = await _services.Images.GetImageAsync(metadata.Picture, highRes: priority);
            }
            if (!string.IsNullOrWhiteSpace(metadata.Rating))
            {
                Item.Rating = metadata.Rating;
                Rating = Item.Rating;
            }
            if (string.IsNullOrWhiteSpace(Item.Description) && !string.IsNullOrWhiteSpace(metadata.Summary)) Item.Content = metadata.Summary;
        }
        catch (Exception ex) { Diagnostics.Log($"海报元数据加载失败: {ex.GetType().Name}"); }
        finally { IsMetadataLoading = false; }
    }
}
