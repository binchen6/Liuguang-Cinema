using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TV.Services;
using TV.Services.AI;
using TV.Services.Playback;

namespace TV.Controls;

public sealed partial class MusicPlayerSurface
{
    private CancellationTokenSource? _lyricWork;
    private bool _bilingual = true;
    private bool _translationPrimary;

    private void OnMetadataMenuOpening(object sender, object args)
    {
        GenerateLyricsItem.IsEnabled = _lyricWork is null && _player.CurrentMusicMedia is not null;
        TranslateLyricsItem.IsEnabled = _lyricWork is null && _lines.Count > 0;
        CancelLyricsItem.IsEnabled = _lyricWork is not null;
        TranslationPrimaryItem.IsEnabled = _lines.Any(line => line.Translation is not null);
    }

    private void OnLyricDisplayChanged(object sender, RoutedEventArgs e)
    {
        _bilingual = BilingualLyricsItem.IsChecked;
        _translationPrimary = TranslationPrimaryItem.IsChecked;
        RefreshLyricDisplay();
    }

    private void OnCancelLyrics(object sender, RoutedEventArgs e) => _lyricWork?.Cancel();

    private async void OnGenerateLyrics(object sender, RoutedEventArgs e) =>
        await RunLyricTaskAsync(generate: true);

    private async void OnTranslateLyrics(object sender, RoutedEventArgs e) =>
        await RunLyricTaskAsync(generate: false);

    private async Task RunLyricTaskAsync(bool generate)
    {
        if (_lyricWork is not null || _track is not { } track || _presentation is null ||
            _player.CurrentMusicMedia is not { } media || XamlRoot is not { } root) return;
        var original = _presentation.Lyrics;
        var lines = _lines;
        using var job = CancellationTokenSource.CreateLinkedTokenSource(track.Token);
        _lyricWork = job;
        var token = job.Token;
        try
        {
            var bailian = App.Services.BailianSubtitle;
            var config = await bailian.GetConfigAsync(token);
            if (!config.HasApiKey)
            {
                SetMetadataStatus("请在「设置 → 弹幕与 AI」配置阿里云百炼 API Key。");
                return;
            }
            var language = new ComboBox
            {
                Header = "目标语言", ItemsSource = AiSubtitleModels.TargetLanguages,
                DisplayMemberPath = nameof(AiLanguageOption.DisplayName),
                SelectedIndex = AiSubtitleModels.FindTargetLanguageIndex(config.TargetLanguage),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = generate
                    ? "将当前音轨提交给已配置的百炼服务识别，生成带时间戳的歌词。演唱和背景音乐可能影响准确度。"
                    : "将原词提交给已配置的百炼服务，保留原来的歌词时间。源语言由模型识别。",
                TextWrapping = TextWrapping.Wrap
            });
            if (!generate) panel.Children.Add(language);
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = generate ? "生成滚动歌词" : "翻译歌词", Content = panel,
                PrimaryButtonText = generate ? "识别音轨" : "翻译", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            using var closeOnCancel = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || token.IsCancellationRequested) return;

            var progress = new Progress<AiSubtitleProgress>(state =>
            {
                if (ReferenceEquals(_lyricWork, job) && !token.IsCancellationRequested) SetMetadataStatus(state.Stage);
            });
            if (generate)
            {
                var address = media.Url;
                if (media.Magnet is { } magnet)
                {
                    if (!magnet.TryGetCompleteFile(out var file))
                    {
                        SetMetadataStatus("磁力音轨缓存完成后可生成歌词。");
                        return;
                    }
                    address = new Uri(file).AbsoluteUri;
                }
                var length = _player.Service.LastSnapshot.LengthMs;
                var cues = await bailian.TranscribeAudioAsync(address, media.Headers, config with { SourceLanguage = "auto" },
                    length > 0 ? TimeSpan.FromMilliseconds(length) : null, progress, token);
                if (_presentation is not { } current || current.Lyrics != original || token.IsCancellationRequested) return;
                var lyrics = MusicMetadataService.FormatLyrics(cues.Select(cue => new LyricLine((long)cue.Start.TotalMilliseconds, cue.Text)));
                await ApplyPresentationAsync(current with { Lyrics = lyrics, TranslatedLyrics = null }, token);
                await PersistLyricsAsync(media, token);
                SetMetadataStatus($"已生成 {cues.Count} 句滚动歌词。");
            }
            else
            {
                if (language.SelectedItem is not AiLanguageOption choice || lines.Count == 0) return;
                var cues = lines.Select((line, index) =>
                {
                    var start = TimeSpan.FromMilliseconds(line.TimeMs ?? index * 1000L);
                    return new SubtitleCue(start, start.Add(TimeSpan.FromSeconds(1)), line.Text);
                }).ToArray();
                var translated = await bailian.TranslateCuesAsync(cues, config with { TargetLanguage = choice.ApiName }, progress, token);
                if (_presentation is not { } current || current.Lyrics != original || token.IsCancellationRequested) return;
                var lyrics = MusicMetadataService.FormatLyrics(lines.Select((line, index) =>
                    new LyricLine(line.TimeMs, translated[index].TranslatedText ?? line.Text)));
                await ApplyPresentationAsync(current with { TranslatedLyrics = lyrics }, token);
                await PersistLyricsAsync(media, token);
                SetMetadataStatus($"已翻译为 {choice.DisplayName}。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (ReferenceEquals(_lyricWork, job)) SetMetadataStatus("歌词任务已取消。");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_lyricWork, job) && !token.IsCancellationRequested)
            {
                Diagnostics.Log("百炼歌词任务: " + ex.GetType().Name);
                SetMetadataStatus(ex is InvalidDataException ? ex.Message : "百炼歌词任务失败，请检查网络和百炼设置后重试。");
            }
        }
        finally { if (ReferenceEquals(_lyricWork, job)) _lyricWork = null; }
    }

    private Task PersistLyricsAsync(TV.Models.PlaybackRequest media, CancellationToken token) =>
        _presentation is { } current ? MusicMetadataService.SaveLyricsAsync(media, current, token) : Task.CompletedTask;

    private void OnAudioQualityOpening(object sender, object args)
    {
        AudioQualityFlyout.Items.Clear();
        foreach (var option in _player.QualityOptions.Where(option => option.Height <= 0 && option.IsAvailable))
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = "musicAudioQuality", Text = option.Name, IsChecked = option == _player.SelectedQuality
            };
            item.Click += async (_, _) =>
            {
                if (_track is not { } track) return;
                var token = track.Token;
                await _player.SelectAudioQualityAsync(option);
                if (token.IsCancellationRequested) return;
                AudioQualityButton.Content = _player.SelectedQuality.Name;
                if (!string.IsNullOrWhiteSpace(_player.QualityMessage)) SetMetadataStatus(_player.QualityMessage);
            };
            AudioQualityFlyout.Items.Add(item);
        }
    }
}
